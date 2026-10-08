using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RenPyLocalizationStudio.App.ViewModels;

public abstract class WorkspaceRegionViewModelBase : ObservableObject;
public abstract class WorkspaceSidebarViewModelBase : WorkspaceRegionViewModelBase;
public abstract class WorkspaceMainContentViewModelBase : WorkspaceRegionViewModelBase;
public abstract class WorkspaceInspectorViewModelBase : WorkspaceRegionViewModelBase;

public abstract class WorkspaceViewModelBase : ObservableObject
{
    private bool _isInspectorOpen = true;
    private string? _badge;

    protected WorkspaceViewModelBase(string id, string title, string glyph)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorOpen = !IsInspectorOpen, () => InspectorContent is not null);
    }

    public string Id { get; }
    public string Title { get; }
    public string Glyph { get; }
    public abstract WorkspaceSidebarViewModelBase SidebarContent { get; }
    public abstract WorkspaceMainContentViewModelBase MainContent { get; }
    public virtual WorkspaceInspectorViewModelBase? InspectorContent => null;
    public IRelayCommand ToggleInspectorCommand { get; }

    public string? Badge
    {
        get => _badge;
        protected set
        {
            if (SetProperty(ref _badge, value)) OnPropertyChanged(nameof(HasBadge));
        }
    }
    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge) && Badge != "0";

    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set
        {
            if (SetProperty(ref _isInspectorOpen, value))
            {
                OnPropertyChanged(nameof(IsInspectorVisible));
                OnPropertyChanged(nameof(InspectorToggleText));
            }
        }
    }

    public string InspectorToggleText => IsInspectorOpen ? "收起检查器" : "打开检查器";
    public bool IsInspectorVisible => InspectorContent is not null && IsInspectorOpen;

    public virtual Task ActivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record ActivityItem(WorkspaceViewModelBase Workspace)
{
    public string Id => Workspace.Id;
    public string Title => Workspace.Title;
    public string Glyph => Workspace.Glyph;
    public string? Badge => Workspace.Badge;
}

public sealed class ToolLogEntry(string message, string level = "Info")
{
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
    public string Message { get; } = message;
    public string Level { get; } = level;
}

public sealed class TaskCenterViewModel : ObservableObject
{
    private readonly object _runSync = new();
    private readonly SynchronizationContext? _synchronizationContext;
    private CancellationTokenSource? _cancellation;
    private Task _activeTask = Task.CompletedTask;
    private bool _isBusy;
    private string _statusMessage = "请选择 Ren’Py 项目目录。";
    private double? _percentage;

    public TaskCenterViewModel(SynchronizationContext? synchronizationContext = null)
    {
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
    }

    public ObservableCollection<ToolLogEntry> Logs { get; } = [];
    public IRelayCommand CancelCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) CancelCommand.NotifyCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public double? Percentage
    {
        get => _percentage;
        private set => SetProperty(ref _percentage, value);
    }

    public IProgress<RenPyLocalizationStudio.Core.Services.ToolOperationProgress> CreateProgress() =>
        new Progress<RenPyLocalizationStudio.Core.Services.ToolOperationProgress>(update => PostToUi(() =>
        {
            StatusMessage = update.TotalItems is > 0
                ? $"{update.Message}  {update.CompletedItems:N0}/{update.TotalItems:N0}"
                : update.Message;
            Percentage = update.Percentage;
            Logs.Add(new ToolLogEntry(update.RelativePath is null ? update.Message : $"{update.Message} · {update.RelativePath}", update.Level.ToString()));
            while (Logs.Count > 500) Logs.RemoveAt(0);
        }));

    public async Task RunAsync(string initialStatus, Func<CancellationToken, Task> operation)
    {
        CancellationTokenSource cancellation;
        Task previous;
        Task current;
        lock (_runSync)
        {
            // 取消旧操作但不要立即 Dispose；旧操作可能仍在注册取消回调或释放文件流。
            _cancellation?.Cancel();
            previous = _activeTask;
            cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            current = RunCoreAsync(previous, initialStatus, operation, cancellation);
            _activeTask = current;
        }

        await current;
    }

    /// <summary>关闭窗口或切换项目时取消并等待当前任务完成，确保不会留下后台写入。</summary>
    public async Task CancelAndWaitAsync(CancellationToken cancellationToken = default)
    {
        Task current;
        lock (_runSync)
        {
            _cancellation?.Cancel();
            current = _activeTask;
        }

        try { await current.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunCoreAsync(
        Task previous,
        string initialStatus,
        Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await RunOnUiAsync(() =>
            {
                lock (_runSync)
                {
                    if (!ReferenceEquals(_cancellation, cancellation)) return;
                    IsBusy = true;
                    Percentage = null;
                    StatusMessage = initialStatus;
                    Logs.Add(new ToolLogEntry(initialStatus));
                }
            });
            // 同一 TaskCenter 的操作不并发执行；取消旧操作后仍等待其释放文件流，再开始新操作。
            await previous.ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            await RunOnUiAsync(() => operation(cancellation.Token));
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() =>
            {
                lock (_runSync)
                {
                    if (ReferenceEquals(_cancellation, cancellation))
                    {
                        StatusMessage = "操作已取消。";
                        Logs.Add(new ToolLogEntry(StatusMessage, "Warning"));
                    }
                }
            });
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() =>
            {
                lock (_runSync)
                {
                    if (ReferenceEquals(_cancellation, cancellation))
                    {
                        StatusMessage = $"操作失败：{exception.Message}";
                        Logs.Add(new ToolLogEntry(StatusMessage, "Error"));
                    }
                }
            });
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                lock (_runSync)
                {
                    // 新操作已经接管状态时，旧操作不得把 Busy 误清掉。
                    if (ReferenceEquals(_cancellation, cancellation))
                    {
                        _cancellation = null;
                        _activeTask = Task.CompletedTask;
                        IsBusy = false;
                    }
                }
            });
            cancellation.Dispose();
        }
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _synchronizationContext.Post(static state =>
        {
            var invocation = (UiActionInvocation)state!;
            try
            {
                invocation.Action();
                invocation.Completion.SetResult();
            }
            catch (Exception exception) { invocation.Completion.SetException(exception); }
        }, new UiActionInvocation(action, completion));
        return completion.Task;
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
            return action();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _synchronizationContext.Post(async state =>
        {
            var invocation = (UiAsyncInvocation)state!;
            try
            {
                await invocation.Action();
                invocation.Completion.SetResult();
            }
            catch (Exception exception) { invocation.Completion.SetException(exception); }
        }, new UiAsyncInvocation(action, completion));
        return completion.Task;
    }

    private void PostToUi(Action action)
    {
        if (_synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
            action();
        else
            _synchronizationContext.Post(static state => ((Action)state!).Invoke(), action);
    }

    private sealed record UiActionInvocation(Action Action, TaskCompletionSource Completion);
    private sealed record UiAsyncInvocation(Func<Task> Action, TaskCompletionSource Completion);

    private void Cancel()
    {
        lock (_runSync) _cancellation?.Cancel();
    }
}
