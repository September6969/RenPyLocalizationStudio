using System.Collections.Concurrent;

namespace RenPyLocalizationStudio.App.Services;

/// <summary>集中拥有可替换的工作区后台任务，保证取消和异常都可观察。</summary>
public sealed class WorkspaceTaskCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, TaskSlot> _slots = new(StringComparer.Ordinal);
    private readonly Action<Exception> _reportException;
    private bool _disposed;

    public WorkspaceTaskCoordinator(Action<Exception> reportException) => _reportException = reportException;

    public Task RunLatestAsync(string key, Func<CancellationToken, Task> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slot = _slots.GetOrAdd(key, _ => new TaskSlot());
        return slot.ReplaceAsync(operation, _reportException);
    }

    /// <summary>合并同一键的高频请求，并在安静期结束后只执行最后一次操作。</summary>
    public Task RunLatestAfterDelayAsync(
        string key,
        TimeSpan delay,
        Func<CancellationToken, Task> operation) =>
        RunLatestAsync(key, async cancellationToken =>
        {
            await Task.Delay(delay, cancellationToken);
            await operation(cancellationToken);
        });

    public void StartLatest(string key, Func<CancellationToken, Task> operation) =>
        _ = RunLatestAsync(key, operation);

    public void StartLatestAfterDelay(
        string key,
        TimeSpan delay,
        Func<CancellationToken, Task> operation) =>
        _ = RunLatestAfterDelayAsync(key, delay, operation);

    public async Task CancelAsync(string key)
    {
        if (_slots.TryGetValue(key, out var slot)) await slot.CancelAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots.Values) slot.Dispose();
        _slots.Clear();
    }

    private sealed class TaskSlot : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private CancellationTokenSource? _cancellation;
        private Task _running = Task.CompletedTask;

        public async Task ReplaceAsync(Func<CancellationToken, Task> operation, Action<Exception> reportException)
        {
            CancellationTokenSource cancellation;
            Task running;
            await _gate.WaitAsync();
            try
            {
                _cancellation?.Cancel();
                cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                running = ObserveAsync(operation, cancellation.Token, reportException);
                _running = running;
            }
            finally { _gate.Release(); }
            // 必须等待本次请求自己的任务；否则并发替换后会错误等待后一个请求。
            await running;
        }

        public async Task CancelAsync()
        {
            Task running;
            await _gate.WaitAsync();
            try
            {
                _cancellation?.Cancel();
                running = _running;
            }
            finally { _gate.Release(); }

            try { await running; }
            catch (OperationCanceledException) { }
        }

        private static async Task ObserveAsync(Func<CancellationToken, Task> operation, CancellationToken token, Action<Exception> reportException)
        {
            try { await operation(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { reportException(exception); }
        }

        public void Dispose()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _gate.Dispose();
        }
    }
}
