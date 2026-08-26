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
            if (SetProperty(ref _isInspectorOpen, value)) OnPropertyChanged(nameof(IsInspectorVisible));
        }
    }

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
    private CancellationTokenSource? _cancellation;
    private bool _isBusy;
    private string _statusMessage = "请选择 Ren’Py 项目目录。";
    private double? _percentage;

    public TaskCenterViewModel() => CancelCommand = new RelayCommand(Cancel, () => IsBusy);

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
        new Progress<RenPyLocalizationStudio.Core.Services.ToolOperationProgress>(update =>
        {
            StatusMessage = update.TotalItems is > 0
                ? $"{update.Message}  {update.CompletedItems:N0}/{update.TotalItems:N0}"
                : update.Message;
            Percentage = update.Percentage;
            Logs.Add(new ToolLogEntry(update.RelativePath is null ? update.Message : $"{update.Message} · {update.RelativePath}", update.Level.ToString()));
            while (Logs.Count > 500) Logs.RemoveAt(0);
        });

    public async Task RunAsync(string initialStatus, Func<CancellationToken, Task> operation)
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        Percentage = null;
        StatusMessage = initialStatus;
        Logs.Add(new ToolLogEntry(initialStatus));
        try
        {
            await operation(_cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "操作已取消。";
            Logs.Add(new ToolLogEntry(StatusMessage, "Warning"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Cancel() => _cancellation?.Cancel();
}
