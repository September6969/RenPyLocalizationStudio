using System.Collections.Concurrent;

namespace RenPyLocalizationStudio.App.Services;

/// <summary>集中拥有可替换的工作区后台任务，保证取消和异常都可观察。</summary>
public sealed class WorkspaceTaskCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, TaskSlot> _slots = new(StringComparer.Ordinal);
    private readonly Action<Exception> _reportException;
    private readonly SynchronizationContext? _synchronizationContext;
    private int _disposed;

    public WorkspaceTaskCoordinator(Action<Exception> reportException, SynchronizationContext? synchronizationContext = null)
    {
        _reportException = reportException;
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
    }

    public Task RunLatestAsync(string key, Func<CancellationToken, Task> operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var slot = _slots.GetOrAdd(key, _ => new TaskSlot());
        return slot.ReplaceAsync(operation, ReportException);
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

    public async Task CancelAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_slots.TryGetValue(key, out var slot))
            await slot.CancelAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取消并等待协调器拥有的全部后台任务，供窗口关闭时统一冲刷。</summary>
    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        var slots = _slots.Values.ToArray();
        foreach (var slot in slots) slot.Cancel();
        foreach (var slot in slots)
            await slot.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var slot in _slots.Values) slot.Dispose();
        _slots.Clear();
    }

    private void ReportException(Exception exception)
    {
        if (_synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
        {
            _reportException(exception);
            return;
        }

        _synchronizationContext.Post(static state =>
        {
            var report = (ExceptionReport)state!;
            report.Callback(report.Exception);
        }, new ExceptionReport(_reportException, exception));
    }

    private sealed record ExceptionReport(Action<Exception> Callback, Exception Exception);

    private sealed class TaskSlot : IDisposable
    {
        private readonly object _sync = new();
        private CancellationTokenSource? _cancellation;
        private Task _running = Task.CompletedTask;
        private bool _disposed;

        public Task ReplaceAsync(Func<CancellationToken, Task> operation, Action<Exception> reportException)
        {
            CancellationTokenSource cancellation;
            Task previous;
            Task running;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _cancellation?.Cancel();
                previous = _running;
                cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                running = ObserveAsync(previous, operation, cancellation, reportException);
                _running = running;
            }

            // 每个调用只等待自己的替换任务；新任务内部会先等待旧任务完成清理。
            return running;
        }

        public void Cancel()
        {
            lock (_sync) _cancellation?.Cancel();
        }

        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            Cancel();
            await WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Task running;
            lock (_sync) running = _running;
            try { await running.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }

        private async Task ObserveAsync(
            Task previous,
            Func<CancellationToken, Task> operation,
            CancellationTokenSource cancellation,
            Action<Exception> reportException)
        {
            try
            {
                await previous.ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                await operation(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception) { reportException(exception); }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                }
                cancellation.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                // CTS 由对应任务在 finally 中释放；这里仅取消，避免任务仍注册回调时误释放。
                _cancellation?.Cancel();
            }
        }
    }
}
