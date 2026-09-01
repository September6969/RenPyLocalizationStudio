using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.ViewModels;

public enum TranslationViewMode { Flow, Strings, Unbound, Bookmarks }
public enum InspectorMode { Empty, Flow, TranslationEditor }

public sealed class ImagePreviewViewModel : ObservableObject
{
    private const int PreviewDecodePixelWidth = 1600;
    private ImagePreviewInfo? _selectedImage;
    private BitmapImage? _currentBitmap;
    private bool _hasImages;
    private string _sceneTag = string.Empty;
    private string _statusHint = "选择包含 scene 或 show 的对白行即可自动预览画面";
    private bool _isCollapsed;

    public ImagePreviewViewModel()
    {
        OpenLocationCommand = new RelayCommand(OpenLocation, () => SelectedImage is not null && File.Exists(SelectedImage.FilePath));
        ToggleCollapseCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);
    }

    public ObservableCollection<ImagePreviewInfo> AvailableImages { get; } = [];

    public ImagePreviewInfo? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (!SetProperty(ref _selectedImage, value)) return;
            LoadBitmap(value?.FilePath);
            OpenLocationCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedImageDetails));
        }
    }

    public BitmapImage? CurrentBitmap
    {
        get => _currentBitmap;
        private set => SetProperty(ref _currentBitmap, value);
    }

    public bool HasImages { get => _hasImages; private set => SetProperty(ref _hasImages, value); }
    public string SceneTag { get => _sceneTag; private set => SetProperty(ref _sceneTag, value); }
    public string StatusHint { get => _statusHint; private set => SetProperty(ref _statusHint, value); }
    public bool IsCollapsed { get => _isCollapsed; set => SetProperty(ref _isCollapsed, value); }
    public bool HasMultipleImages => AvailableImages.Count > 1;
    public string SelectedImageDetails => SelectedImage is not null && !string.IsNullOrWhiteSpace(SelectedImage.Resolution)
        ? $"{SelectedImage.Resolution} · {SelectedImage.FileSizeText}"
        : string.Empty;

    public IRelayCommand OpenLocationCommand { get; }
    public IRelayCommand ToggleCollapseCommand { get; }

    public void UpdateContext(RenPySceneContext context)
    {
        AvailableImages.Clear();
        SceneTag = context.SceneStatement ?? context.SceneTag ?? string.Empty;

        foreach (var img in context.AvailableImages)
        {
            AvailableImages.Add(img);
        }

        HasImages = AvailableImages.Count > 0;
        OnPropertyChanged(nameof(HasMultipleImages));
        SelectedImage = AvailableImages.FirstOrDefault();

        if (!HasImages)
        {
            CurrentBitmap = null;
            StatusHint = !string.IsNullOrWhiteSpace(SceneTag)
                ? $"已识别到指令：{SceneTag}，但未在项目中找到同名图片素材。"
                : "当前对白行未检测到前置 scene/show 图片指令。";
        }
    }

    private void LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            CurrentBitmap = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = PreviewDecodePixelWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            CurrentBitmap = bitmap;
        }
        catch
        {
            CurrentBitmap = null;
        }
    }

    private void OpenLocation()
    {
        if (SelectedImage is null || !File.Exists(SelectedImage.FilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{SelectedImage.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }
}

public abstract class InspectorStateViewModel(InspectorMode mode) : ObservableObject
{
    public InspectorMode Mode { get; } = mode;
}

public sealed class EmptyInspectorStateViewModel : InspectorStateViewModel
{
    public EmptyInspectorStateViewModel() : base(InspectorMode.Empty) { }
}

public sealed class FlowInspectorStateViewModel(string title, string details) : InspectorStateViewModel(InspectorMode.Flow)
{
    public string Title { get; } = title;
    public string Details { get; } = details;
}

public sealed class TranslationEditorInspectorStateViewModel(TranslationInspectorViewModel owner)
    : InspectorStateViewModel(InspectorMode.TranslationEditor)
{
    public TranslationInspectorViewModel Owner { get; } = owner;
}

public sealed class TranslationSidebarViewModel : WorkspaceSidebarViewModelBase
{
    public TranslationSidebarViewModel(TranslationWorkspaceViewModel workspace) => Workspace = workspace;
    public TranslationWorkspaceViewModel Workspace { get; }
}

public sealed class TranslationMainContentViewModel : WorkspaceMainContentViewModelBase
{
    public TranslationMainContentViewModel(TranslationWorkspaceViewModel workspace) => Workspace = workspace;
    public TranslationWorkspaceViewModel Workspace { get; }
}

public sealed partial class TranslationInspectorViewModel : WorkspaceInspectorViewModelBase, IDisposable
{
    private readonly TranslationValidator _validator = new();
    private readonly TaskCenterViewModel _tasks;
    private EditorTarget? _target;
    private string _title = string.Empty;
    private string _location = string.Empty;
    private string _sourceText = string.Empty;
    private string _translationText = string.Empty;
    private string _impact = string.Empty;
    private string _validationMessage = string.Empty;
    private string _documentKey = "none";
    private long _focusRequest;
    private bool _updating;
    private InspectorStateViewModel _currentInspectorState = new EmptyInspectorStateViewModel();

    [GeneratedRegex(@"\[[^\]\r\n]+\]|%\([^)]+\)[#0\- +]?[0-9.*]*[a-zA-Z]|%(?:[#0\- +]?[0-9.*]*)[a-zA-Z]|\{/?[^{}\r\n]+\}")]
    private static partial Regex PlaceholderTokenRegex();

    public TranslationInspectorViewModel(TaskCenterViewModel tasks)
    {
        _tasks = tasks;
        UnifyConflictCommand = new RelayCommand(UnifyConflict, () => _target?.HasConflict == true);
        _tasks.PropertyChanged += OnTaskCenterPropertyChanged;
    }

    public ObservableCollection<string> PlaceholderTokens { get; } = [];
    public IRelayCommand UnifyConflictCommand { get; }
    public bool HasTarget => _target is not null;
    public bool IsReadOnly => _target is null || _tasks.IsBusy;
    public bool HasConflict => _target?.HasConflict == true;
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Location { get => _location; private set => SetProperty(ref _location, value); }
    public string SourceText { get => _sourceText; private set => SetProperty(ref _sourceText, value); }
    public string Impact { get => _impact; private set => SetProperty(ref _impact, value); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }
    public string DocumentKey { get => _documentKey; private set => SetProperty(ref _documentKey, value); }
    public long FocusRequest { get => _focusRequest; private set => SetProperty(ref _focusRequest, value); }
    public InspectorStateViewModel CurrentInspectorState { get => _currentInspectorState; private set => SetProperty(ref _currentInspectorState, value); }

    public string TranslationText
    {
        get => _translationText;
        set
        {
            if (!_updating && _tasks.IsBusy) return;
            if (!SetProperty(ref _translationText, value) || _updating || _target is null) return;
            _target.SetTranslation(value);
            ValidationMessage = string.Join(Environment.NewLine, _validator.Validate(_target.Source, value).Select(x => x.Message));
            TranslationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? TranslationChanged;

    public void SetTarget(EditorTarget? target, string? fallbackTitle = null, string? fallbackDetails = null)
    {
        _updating = true;
        _target = target;
        Title = target?.Title ?? fallbackTitle ?? string.Empty;
        Location = target?.Location ?? fallbackDetails ?? string.Empty;
        SourceText = target?.Source ?? string.Empty;
        TranslationText = target?.Translation ?? string.Empty;
        Impact = target?.Impact ?? string.Empty;
        ValidationMessage = string.Empty;
        DocumentKey = target?.Key ?? "none:" + fallbackTitle;
        PlaceholderTokens.Clear();
        foreach (Match token in PlaceholderTokenRegex().Matches(SourceText))
            if (!PlaceholderTokens.Contains(token.Value)) PlaceholderTokens.Add(token.Value);
        CurrentInspectorState = target is not null
            ? new TranslationEditorInspectorStateViewModel(this)
            : string.IsNullOrWhiteSpace(fallbackTitle)
                ? new EmptyInspectorStateViewModel()
                : new FlowInspectorStateViewModel(fallbackTitle, fallbackDetails ?? string.Empty);
        _updating = false;
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(HasConflict));
        UnifyConflictCommand.NotifyCanExecuteChanged();
    }

    public void RequestFocus() => FocusRequest++;

    public void Dispose() => _tasks.PropertyChanged -= OnTaskCenterPropertyChanged;

    private void OnTaskCenterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TaskCenterViewModel.IsBusy)) OnPropertyChanged(nameof(IsReadOnly));
    }

    private void UnifyConflict()
    {
        _target?.SharedString?.Unify(TranslationText);
        OnPropertyChanged(nameof(HasConflict));
        UnifyConflictCommand.NotifyCanExecuteChanged();
        TranslationChanged?.Invoke(this, EventArgs.Empty);
        _tasks.StatusMessage = "已在内存中统一冲突译文，保存后写入全部物理定义。";
    }
}

public sealed class TranslationWorkspaceViewModel : WorkspaceViewModelBase, IDisposable
{
    private readonly ProjectSessionViewModel _session;
    private readonly IRenPyImagePreviewService _imagePreviewService;
    private readonly bool _ownsImagePreviewService;
    private TranslationViewMode _viewMode;
    private FlowGroupingMode _groupingMode;
    private string _searchText = string.Empty;
    private ContentItem? _selectedItem;
    private NavigationItem? _selectedLabel;
    private object? _anchorItem;
    private ScrollAnchor? _scrollAnchor;
    private IReadOnlyList<ContentItem> _coverageItems = [];
    private readonly Dictionary<ContentItem, bool> _completionStates = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SharedStringEntry, List<ContentItem>> _sharedPresentationItems = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _visibleItemIndexes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bookmarkedNodeIds = new(StringComparer.Ordinal);
    private readonly object _previewSync = new();
    private readonly HashSet<Task> _previewRequests = [];
    private CancellationTokenSource? _previewCancellation;
    private string? _indexedPreviewProjectPath;
    private long _previewVersion;
    private int _disposed;

    public TranslationWorkspaceViewModel(ProjectSessionViewModel session, IRenPyImagePreviewService? imagePreviewService = null)
        : base("translation", "翻译", "\uE8A5")
    {
        _session = session;
        _imagePreviewService = imagePreviewService ?? new RenPyImagePreviewService();
        _ownsImagePreviewService = imagePreviewService is null;
        Sidebar = new TranslationSidebarViewModel(this);
        Main = new TranslationMainContentViewModel(this);
        Inspector = new TranslationInspectorViewModel(session.Tasks);
        ImagePreview = new ImagePreviewViewModel();
        OpenSelectedCommand = new RelayCommand(OpenSelected);
        NavigateBookmarkCommand = new RelayCommand<ContentItem>(NavigateBookmarkToSource,
            item => ViewMode == TranslationViewMode.Bookmarks && item?.Node is not null);
        NavigateLabelCommand = new RelayCommand(NavigateSelectedLabel, () => SelectedLabel is not null);
        MovePreviousCommand = new RelayCommand(() => MoveEditable(-1));
        MoveNextCommand = new RelayCommand(() => MoveEditable(1));
        ToggleBookmarkCommand = new RelayCommand<ContentItem>(ToggleBookmark, item => item?.Node is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => SearchText.Length > 0);
        Inspector.TranslationChanged += OnTranslationChanged;
        session.SnapshotChanged += OnSnapshotChanged;
        session.SaveCompleted += OnSaveCompleted;
        session.PropertyChanged += OnSessionPropertyChanged;
    }

    public TranslationSidebarViewModel Sidebar { get; }
    public TranslationMainContentViewModel Main { get; }
    public TranslationInspectorViewModel Inspector { get; }
    public ImagePreviewViewModel ImagePreview { get; }
    public override WorkspaceSidebarViewModelBase SidebarContent => Sidebar;
    public override WorkspaceMainContentViewModelBase MainContent => Main;
    public override WorkspaceInspectorViewModelBase InspectorContent => Inspector;
    public ObservableCollection<NavigationItem> Labels { get; } = [];
    public IReadOnlyList<ContentItem> VisibleItems { get; private set; } = [];
    public IReadOnlyList<TranslationViewMode> ViewModes { get; } = Enum.GetValues<TranslationViewMode>();
    public IReadOnlyList<FlowGroupingMode> GroupingModes { get; } = Enum.GetValues<FlowGroupingMode>();
    public IRelayCommand OpenSelectedCommand { get; }
    public IRelayCommand<ContentItem> NavigateBookmarkCommand { get; }
    public IRelayCommand NavigateLabelCommand { get; }
    public IRelayCommand MovePreviousCommand { get; }
    public IRelayCommand MoveNextCommand { get; }
    public IRelayCommand<ContentItem> ToggleBookmarkCommand { get; }
    public IRelayCommand ClearSearchCommand { get; }

    public TranslationViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (!SetProperty(ref _viewMode, value)) return;
            RebuildProjection();
            RebuildNavigation();
            OnPropertyChanged(nameof(NavigationHeading));
            OnPropertyChanged(nameof(IsBookmarkView));
            NavigateBookmarkCommand.NotifyCanExecuteChanged();
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public FlowGroupingMode GroupingMode
    {
        get => _groupingMode;
        set
        {
            if (!SetProperty(ref _groupingMode, value)) return;
            RebuildProjection();
            RebuildNavigation();
            OnPropertyChanged(nameof(NavigationHeading));
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ClearSearchCommand.NotifyCanExecuteChanged();
            ApplySearchFilter();
        }
    }

    public ContentItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            UpdateInspector(value);
            StartImagePreviewUpdate(value);
            if (value is not null)
                ScrollAnchor = new ScrollAnchor(value.Key,
                    FallbackIndex: _visibleItemIndexes.GetValueOrDefault(value.Key, -1));
            NavigateBookmarkCommand.NotifyCanExecuteChanged();
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public NavigationItem? SelectedLabel
    {
        get => _selectedLabel;
        set
        {
            if (!SetProperty(ref _selectedLabel, value)) return;
            NavigateLabelCommand.NotifyCanExecuteChanged();
        }
    }

    public object? AnchorItem { get => _anchorItem; private set => SetProperty(ref _anchorItem, value); }
    public ScrollAnchor? ScrollAnchor { get => _scrollAnchor; private set => SetProperty(ref _scrollAnchor, value); }
    public string ItemCount => $"{VisibleItems.Count:N0} 项";
    public string NavigationHeading => ViewMode == TranslationViewMode.Bookmarks
        ? "书签"
        : GroupingMode switch { FlowGroupingMode.SourceFile => "文件", FlowGroupingMode.Label => "LABEL", _ => "剧情入口" };
    public string ViewTitle => ViewMode switch
    {
        TranslationViewMode.Strings => "字符串表",
        TranslationViewMode.Unbound => "未绑定",
        TranslationViewMode.Bookmarks => "书签",
        _ => "剧情流"
    };
    public bool IsBookmarkView => ViewMode == TranslationViewMode.Bookmarks;
    public int BookmarkedCount => _bookmarkedNodeIds.Count;
    public int EditableCount { get; private set; }
    public int TranslatedCount { get; private set; }
    public double CoveragePercent { get; private set; }
    public bool HasCoverage => EditableCount > 0;
    public string CoverageText => HasCoverage ? $"{TranslatedCount:N0}/{EditableCount:N0} · {CoveragePercent:0}%" : string.Empty;
    public event EventHandler? PositionChanged;

    public override Task RefreshAsync(CancellationToken cancellationToken)
    {
        RefreshFromSnapshot();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Inspector.TranslationChanged -= OnTranslationChanged;
        Inspector.Dispose();
        _session.SnapshotChanged -= OnSnapshotChanged;
        _session.SaveCompleted -= OnSaveCompleted;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        Task[] requests;
        lock (_previewSync)
        {
            _previewCancellation?.Cancel();
            requests = _previewRequests.ToArray();
        }
        if (_ownsImagePreviewService && _imagePreviewService is IDisposable disposable)
        {
            if (requests.Length == 0) disposable.Dispose();
            else _ = DisposePreviewServiceWhenIdleAsync(requests, disposable);
        }
    }

    /// <summary>窗口关闭前等待全部预览请求退出，避免索引仍在构建时释放 watcher 或锁。</summary>
    public async Task StopPreviewAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] requests;
            lock (_previewSync)
            {
                _previewCancellation?.Cancel();
                requests = _previewRequests.ToArray();
            }
            if (requests.Length == 0) return;
            await Task.WhenAll(requests).WaitAsync(cancellationToken);
        }
    }

    private void OnTranslationChanged(object? sender, EventArgs args)
    {
        UpdateSelectedPresentation();
        _session.ScheduleAutoSave();
    }

    private void OnSaveCompleted(object? sender, EventArgs args)
    {
        foreach (var item in _coverageItems) item.MarkPersisted();
        RecalculateCoverage();
    }

    private void OnSnapshotChanged(object? sender, ProjectSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            InvalidatePreviewProject(_indexedPreviewProjectPath);
            _indexedPreviewProjectPath = snapshot.ProjectRoot;
            _imagePreviewService.InvalidateProject(snapshot.ProjectRoot);
        }
        RefreshFromSnapshot();
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(ProjectSessionViewModel.ProjectPath) or nameof(ProjectSessionViewModel.Language))) return;
        lock (_previewSync) _previewCancellation?.Cancel();
        InvalidatePreviewProject(_indexedPreviewProjectPath);
        _indexedPreviewProjectPath = null;
        ImagePreview.UpdateContext(new RenPySceneContext(null, null, []));
        // 书签 ID 只在当前项目快照内有效，切换项目或语言时必须清空。
        if (_bookmarkedNodeIds.Count > 0)
        {
            _bookmarkedNodeIds.Clear();
            OnPropertyChanged(nameof(BookmarkedCount));
        }
    }

    public bool Navigate(NavigateToSourceRequest request)
    {
        var snapshot = _session.Snapshot;
        if (snapshot is null) return false;
        ViewMode = TranslationViewMode.Flow;
        if (SearchText.Length > 0) SearchText = string.Empty;

        var node = request.NodeId is not null
            ? snapshot.Graph.Nodes.FirstOrDefault(x => x.Id == request.NodeId)
            : null;
        if (node is null && request.Label is not null && snapshot.Graph.Labels.TryGetValue(request.Label, out var label)) node = label;
        if (node is null && request.RelativePath is not null)
        {
            node = snapshot.Graph.Nodes
                .Where(x => string.Equals(x.Region.RelativePath, request.RelativePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => Math.Abs(x.Region.StartLine - (request.Line ?? x.Region.StartLine)))
                .FirstOrDefault();
        }
        if (node is null) return false;
        var item = VisibleItems.FirstOrDefault(x => x.Key == node.Id);
        if (item is null) return false;
        SelectedItem = item;
        AnchorItem = item;
        IsInspectorOpen = request.OpenInspector;
        if (request.FocusTarget == NavigationFocusTarget.TranslationEditor) Inspector.RequestFocus();
        return true;
    }

    /// <summary>从书签投影回到完整剧情流，并选中原节点对应的源码行。</summary>
    private void NavigateBookmarkToSource(ContentItem? item)
    {
        if (item?.Node is null || ViewMode != TranslationViewMode.Bookmarks) return;
        var node = item.Node;
        if (Navigate(new NavigateToSourceRequest(
                node.Id,
                node.Region.RelativePath,
                node.Region.StartLine,
                Label: null,
                OpenInspector: true,
                FocusTarget: NavigationFocusTarget.Inspector)))
        {
            _session.Tasks.StatusMessage = $"已从书签定位到原代码行：{node.Region.RelativePath}:{node.Region.StartLine}。";
        }
        else
        {
            _session.Tasks.StatusMessage = $"无法定位书签对应的原代码行：{node.Region.RelativePath}:{node.Region.StartLine}。";
        }
    }

    /// <summary>恢复上次关闭应用时的投影、分组和条目位置。</summary>
    public bool RestorePosition(SavedTranslationPosition? saved)
    {
        if (saved is null) return false;

        var mode = Enum.TryParse<TranslationViewMode>(saved.ViewMode, ignoreCase: true, out var parsedMode)
            ? parsedMode
            : TranslationViewMode.Flow;
        // 书签只保存在当前会话中，若上次状态没有可用书签则回退到剧情流。
        if (mode == TranslationViewMode.Bookmarks && _bookmarkedNodeIds.Count == 0) mode = TranslationViewMode.Flow;
        var grouping = Enum.TryParse<FlowGroupingMode>(saved.GroupingMode, ignoreCase: true, out var parsedGrouping)
            ? parsedGrouping
            : FlowGroupingMode.StoryPath;

        _viewMode = mode;
        _groupingMode = grouping;
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(GroupingMode));
        OnPropertyChanged(nameof(NavigationHeading));
        OnPropertyChanged(nameof(ViewTitle));
        OnPropertyChanged(nameof(IsBookmarkView));
        NavigateBookmarkCommand.NotifyCanExecuteChanged();
        RebuildNavigation();
        RebuildProjection(saved.ItemId);

        ContentItem? restored = saved.ItemId is null ? null : VisibleItems.FirstOrDefault(x => x.Key == saved.ItemId);
        if (restored is null && mode != TranslationViewMode.Flow)
        {
            _viewMode = TranslationViewMode.Flow;
            OnPropertyChanged(nameof(ViewMode));
            OnPropertyChanged(nameof(ViewTitle));
            OnPropertyChanged(nameof(IsBookmarkView));
            NavigateBookmarkCommand.NotifyCanExecuteChanged();
            RebuildNavigation();
            RebuildProjection(saved.ItemId);
            restored = VisibleItems.FirstOrDefault(x => x.Key == saved.ItemId);
        }

        if (restored is null)
        {
            _session.Tasks.StatusMessage = saved.ItemId is null
                ? "已恢复上次浏览视图。"
                : "上次访问位置已不存在，已恢复到当前剧情流。";
            return false;
        }

        SelectedItem = restored;
        AnchorItem = restored;
        _session.Tasks.StatusMessage = $"已恢复上次访问位置：{restored.Subtitle}。";
        return true;
    }

    private void RefreshFromSnapshot()
    {
        var selectedId = SelectedItem?.Key ?? ScrollAnchor?.ItemId;
        RebuildNavigation();
        RebuildProjection(selectedId);
    }

    private void RebuildProjection(string? preferredId = null)
    {
        preferredId ??= SelectedItem?.Key ?? ScrollAnchor?.ItemId;
        var snapshot = _session.Snapshot;
        if (snapshot is null)
        {
            _coverageItems = [];
            _sharedPresentationItems.Clear();
            RecalculateCoverage();
            ApplySearchFilter(preferredId);
            return;
        }

        var projectedItems = (ViewMode switch
        {
            TranslationViewMode.Strings => snapshot.SharedStrings.Select(ContentItem.FromSharedString),
            TranslationViewMode.Unbound => snapshot.TranslationUnits.Where(x => x.IsUnboundFlowTranslation).Select(ContentItem.FromTranslation),
            TranslationViewMode.Bookmarks => BuildFlowItems(snapshot, FlowGroupingMode.StoryPath)
                .Where(item => item.Node is not null && _bookmarkedNodeIds.Contains(item.Key)),
            _ => BuildFlowItems(snapshot, GroupingMode)
        }).ToList();
        foreach (var item in projectedItems)
            item.SetBookmarked(item.Node is not null && _bookmarkedNodeIds.Contains(item.Key));
        _coverageItems = projectedItems;
        RebuildSharedPresentationIndex();
        RecalculateCoverage();
        ApplySearchFilter(preferredId);
    }

    private void ApplySearchFilter(string? preferredId = null)
    {
        preferredId ??= SelectedItem?.Key ?? ScrollAnchor?.ItemId;
        IEnumerable<ContentItem> items = _coverageItems;
        var search = SearchText.Trim();
        if (search.Length > 0) items = items.Where(x => x.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));
        VisibleItems = items.ToArray();
        _visibleItemIndexes.Clear();
        for (var index = 0; index < VisibleItems.Count; index++)
            _visibleItemIndexes.TryAdd(VisibleItems[index].Key, index);
        OnPropertyChanged(nameof(VisibleItems));
        SelectedItem = preferredId is null ? null : VisibleItems.FirstOrDefault(x => x.Key == preferredId);
        if (SelectedItem is not null) AnchorItem = SelectedItem;
        NotifyCollectionPresentation();
    }

    private void RebuildSharedPresentationIndex()
    {
        _sharedPresentationItems.Clear();
        foreach (var item in _coverageItems)
        {
            if (item.SharedString is null) continue;
            if (!_sharedPresentationItems.TryGetValue(item.SharedString, out var presentations))
            {
                presentations = [];
                _sharedPresentationItems[item.SharedString] = presentations;
            }
            presentations.Add(item);
        }
    }

    private static IEnumerable<ContentItem> BuildFlowItems(ProjectSnapshot snapshot, FlowGroupingMode groupingMode)
    {
        var units = snapshot.TranslationUnits.Where(x => x.BoundNode is not null).GroupBy(x => x.BoundNode!.Id).ToDictionary(x => x.Key, x => x.First());
        var strings = snapshot.SharedStrings.ToDictionary(x => x.OldText, StringComparer.Ordinal);
        foreach (var group in FlowProjectionService.ProjectGroups(snapshot, groupingMode))
        {
            if (groupingMode != FlowGroupingMode.StoryPath)
                yield return ContentItem.FromGroupHeader(group.Key, group.Title, groupingMode);

            foreach (var row in group.Rows)
            {
                units.TryGetValue(row.Node.Id, out var unit);
                SharedStringEntry? shared = null;
                if (row.Node.Kind == FlowNodeKind.Choice && row.Node.OriginalText is not null) strings.TryGetValue(row.Node.OriginalText, out shared);
                else if (unit?.Kind == TranslationUnitKind.String && unit.OldText is not null) strings.TryGetValue(unit.OldText, out shared);
                yield return ContentItem.FromFlow(row.Node, unit, shared, row.Depth);
            }
        }
    }

    private void OpenSelected()
    {
        var transfer = SelectedItem?.Node;
        if (transfer?.Kind is not (FlowNodeKind.Jump or FlowNodeKind.Call)) return;
        var snapshot = _session.Snapshot;
        if (transfer.Target is null || snapshot is null || !snapshot.Graph.Labels.TryGetValue(transfer.Target, out var target))
        {
            Inspector.SetTarget(null, transfer.DisplayText, $"无法定位目标 label：{transfer.Target ?? "动态目标"}");
            _session.Tasks.StatusMessage = $"无法定位目标：{transfer.Target ?? "动态目标"}。";
            return;
        }
        if (Navigate(new NavigateToSourceRequest(target.Id, target.Region.RelativePath, target.Region.StartLine, transfer.Target)))
            _session.Tasks.StatusMessage = $"已从 {transfer.Kind.ToString().ToLowerInvariant()} 定位到 label {transfer.Target}。";
    }

    private void NavigateSelectedLabel()
    {
        if (SelectedLabel is null) return;
        // 书签导航保持在书签投影中，避免点击左侧条目后意外跳回完整剧情流。
        if (ViewMode != TranslationViewMode.Bookmarks)
            ViewMode = TranslationViewMode.Flow;
        if (SearchText.Length > 0) SearchText = string.Empty;
        var projectedItem = VisibleItems.FirstOrDefault(item => item.Key == SelectedLabel.NodeId);
        if (projectedItem is not null)
        {
            SelectedItem = projectedItem;
            AnchorItem = projectedItem;
            _session.Tasks.StatusMessage = $"已定位到 {SelectedLabel.Name}。";
            return;
        }
        if (Navigate(new NavigateToSourceRequest(SelectedLabel.NodeId, null, null, SelectedLabel.Name)))
            _session.Tasks.StatusMessage = $"已定位到 label {SelectedLabel.Name}。";
    }

    private void RebuildNavigation()
    {
        Labels.Clear();
        SelectedLabel = null;
        if (_session.Snapshot is null) return;
        var items = ViewMode == TranslationViewMode.Bookmarks
            ? TranslationNavigationBuilder.BuildBookmarks(_session.Snapshot, _bookmarkedNodeIds)
            : TranslationNavigationBuilder.Build(_session.Snapshot, GroupingMode);
        foreach (var item in items) Labels.Add(item);
    }

    private void ToggleBookmark(ContentItem? item)
    {
        if (item?.Node is null) return;
        var nodeId = item.Node.Id;
        var bookmarked = ToggleBookmarkState(_bookmarkedNodeIds, nodeId);
        item.SetBookmarked(bookmarked);
        OnPropertyChanged(nameof(BookmarkedCount));
        if (ViewMode == TranslationViewMode.Bookmarks)
        {
            RebuildProjection(item.Key);
        }
        _session.Tasks.StatusMessage = bookmarked ? "已添加书签。" : "已移除书签。";
    }

    internal static bool ToggleBookmarkState(HashSet<string> bookmarks, string nodeId)
    {
        if (bookmarks.Add(nodeId)) return true;
        bookmarks.Remove(nodeId);
        return false;
    }

    private void MoveEditable(int direction)
    {
        var selectedIndex = SelectedItem is not null && _visibleItemIndexes.TryGetValue(SelectedItem.Key, out var knownIndex)
            ? knownIndex
            : -1;
        var index = selectedIndex < 0
            ? (direction > 0 ? 0 : VisibleItems.Count - 1)
            : selectedIndex + direction;
        // 手动选中 jump 行后按 Alt+↓ 也应沿目标 label 继续，而不是跳过该转移。
        if (direction > 0 && selectedIndex >= 0 && VisibleItems[selectedIndex].Node?.Kind == FlowNodeKind.Jump)
            index = selectedIndex;

        var followedLabel = string.Empty;
        var visitedJumps = new HashSet<string>(StringComparer.Ordinal);
        while (index >= 0 && index < VisibleItems.Count)
        {
            var item = VisibleItems[index];
            if (direction > 0 && item.Node?.Kind == FlowNodeKind.Jump &&
                visitedJumps.Add(item.Node.Id) &&
                TryResolveJumpTargetIndexFast(item.Node, _session.Snapshot?.Graph, out var targetIndex, out var targetLabel))
            {
                index = targetIndex;
                followedLabel = targetLabel;
                continue;
            }

            if (item.IsEditable)
            {
                SelectedItem = item;
                AnchorItem = SelectedItem;
                Inspector.RequestFocus();
                _session.Tasks.StatusMessage = followedLabel.Length == 0
                    ? direction > 0 ? "已切换到下一条可编辑译文。" : "已切换到上一条可编辑译文。"
                    : $"已沿 jump 跳转到 label {followedLabel}，并切换到下一条可编辑译文。";
                return;
            }
            index += direction;
        }
        _session.Tasks.StatusMessage = direction > 0 ? "已经是最后一条可编辑译文。" : "已经是第一条可编辑译文。";
    }

    private bool TryResolveJumpTargetIndexFast(
        FlowNode jump,
        FlowGraph? graph,
        out int targetIndex,
        out string targetLabel)
    {
        targetIndex = -1;
        targetLabel = string.Empty;
        if (graph is null || string.IsNullOrWhiteSpace(jump.Target) ||
            !graph.Labels.TryGetValue(jump.Target, out var label) ||
            !_visibleItemIndexes.TryGetValue(label.Id, out targetIndex)) return false;
        targetLabel = jump.Target;
        return true;
    }

    internal static bool TryResolveJumpTargetIndex(
        IReadOnlyList<ContentItem> visibleItems,
        FlowGraph? graph,
        int jumpIndex,
        out int targetIndex,
        out string targetLabel)
    {
        targetIndex = -1;
        targetLabel = string.Empty;
        if (graph is null || jumpIndex < 0 || jumpIndex >= visibleItems.Count) return false;
        var jump = visibleItems[jumpIndex].Node;
        if (jump?.Kind != FlowNodeKind.Jump || string.IsNullOrWhiteSpace(jump.Target) ||
            !graph.Labels.TryGetValue(jump.Target, out var label)) return false;

        for (var index = 0; index < visibleItems.Count; index++)
        {
            if (visibleItems[index].Node?.Id != label.Id) continue;
            targetIndex = index;
            targetLabel = jump.Target;
            return true;
        }

        return false;
    }

    private void UpdateInspector(ContentItem? item)
    {
        if (item?.SharedString is not null) Inspector.SetTarget(EditorTarget.ForSharedString(item.SharedString));
        else if (item?.Unit is not null) Inspector.SetTarget(EditorTarget.ForUnit(item.Unit));
        else Inspector.SetTarget(null, item?.Title, item?.Subtitle);
    }

    private void UpdateSelectedPresentation()
    {
        var item = SelectedItem;
        if (item is null) return;
        IReadOnlyList<ContentItem> affectedItems = [item];
        if (item.SharedString is not null)
        {
            if (_sharedPresentationItems.TryGetValue(item.SharedString, out var presentations)) affectedItems = presentations;
            foreach (var candidate in affectedItems)
            {
                candidate.Subtitle = item.SharedString.Translation;
                candidate.RefreshStatus();
            }
        }
        else item.RefreshStatus();

        var translatedDelta = 0;
        foreach (var candidate in affectedItems)
        {
            var wasComplete = _completionStates.GetValueOrDefault(candidate);
            var isComplete = candidate.IsTranslationComplete;
            if (wasComplete != isComplete) translatedDelta += isComplete ? 1 : -1;
            _completionStates[candidate] = isComplete;
        }
        if (translatedDelta == 0) return;
        TranslatedCount += translatedDelta;
        PublishCoverage();
    }

    private void RecalculateCoverage()
    {
        _completionStates.Clear();
        EditableCount = 0;
        TranslatedCount = 0;
        foreach (var item in _coverageItems)
        {
            if (!item.IsEditable) continue;
            EditableCount++;
            var isComplete = item.IsTranslationComplete;
            _completionStates[item] = isComplete;
            if (isComplete) TranslatedCount++;
        }
        PublishCoverage();
    }

    private void PublishCoverage()
    {
        CoveragePercent = EditableCount == 0 ? 0 : TranslatedCount * 100d / EditableCount;
        Badge = EditableCount == 0 ? null : Math.Max(0, EditableCount - TranslatedCount).ToString();
        OnPropertyChanged(nameof(EditableCount));
        OnPropertyChanged(nameof(TranslatedCount));
        OnPropertyChanged(nameof(CoveragePercent));
        OnPropertyChanged(nameof(HasCoverage));
        OnPropertyChanged(nameof(CoverageText));
    }

    private void NotifyCollectionPresentation()
    {
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(ViewTitle));
    }

    private void StartImagePreviewUpdate(ContentItem? item)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var projectPath = _session.ProjectPath;
        if (!string.IsNullOrWhiteSpace(projectPath) &&
            !string.Equals(_indexedPreviewProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
        {
            InvalidatePreviewProject(_indexedPreviewProjectPath);
            _indexedPreviewProjectPath = projectPath;
        }

        var cancellation = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _previewVersion);
        var task = UpdateImagePreviewAsync(item, projectPath, version, cancellation.Token);
        CancellationTokenSource? previousCancellation;
        lock (_previewSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                cancellation.Cancel();
                cancellation.Dispose();
                return;
            }
            previousCancellation = _previewCancellation;
            _previewCancellation = cancellation;
            _previewRequests.Add(task);
        }
        previousCancellation?.Cancel();
        _ = TrackPreviewRequestAsync(task, cancellation);
    }

    private async Task TrackPreviewRequestAsync(Task task, CancellationTokenSource cancellation)
    {
        try { await task; }
        finally
        {
            lock (_previewSync)
            {
                _previewRequests.Remove(task);
                if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task UpdateImagePreviewAsync(ContentItem? item, string projectPath, long version, CancellationToken cancellationToken)
    {
        var node = item?.Node ?? item?.Unit?.BoundNode;
        var relativePath = node?.Region.RelativePath ?? item?.Unit?.SourcePath;
        var line = node?.Region.StartLine ?? item?.Unit?.SourceLine ?? 0;

        if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(relativePath) || line <= 0)
        {
            if (Volatile.Read(ref _disposed) == 0)
                ImagePreview.UpdateContext(new RenPySceneContext(null, null, []));
            return;
        }

        try
        {
            var context = await _imagePreviewService.ResolveSceneContextAsync(
                new RenPyImagePreviewRequest(projectPath, relativePath, line, item?.Key),
                cancellationToken);
            if (Volatile.Read(ref _disposed) == 0 &&
                version == Volatile.Read(ref _previewVersion) &&
                string.Equals(projectPath, _session.ProjectPath, StringComparison.OrdinalIgnoreCase) &&
                ReferenceEquals(item, SelectedItem))
                ImagePreview.UpdateContext(context);
        }
        catch (OperationCanceledException)
        {
            // 快速切换节点时取消旧请求属于正常路径。
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (Volatile.Read(ref _disposed) == 0 && version == Volatile.Read(ref _previewVersion))
                _session.Tasks.StatusMessage = $"图片预览不可用：{exception.Message}";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 预览属于非阻断功能，但后台异常必须被观察并进入任务日志。
            if (Volatile.Read(ref _disposed) == 0 && version == Volatile.Read(ref _previewVersion))
            {
                _session.Tasks.StatusMessage = $"图片预览失败：{exception.Message}";
                _session.Tasks.Logs.Add(new ToolLogEntry(_session.Tasks.StatusMessage, "Error"));
            }
        }
    }

    private void InvalidatePreviewProject(string? projectPath)
    {
        if (!string.IsNullOrWhiteSpace(projectPath)) _imagePreviewService.InvalidateProject(projectPath);
    }

    private static async Task DisposePreviewServiceWhenIdleAsync(Task[] requests, IDisposable service)
    {
        try { await Task.WhenAll(requests).ConfigureAwait(false); }
        finally { service.Dispose(); }
    }
}
