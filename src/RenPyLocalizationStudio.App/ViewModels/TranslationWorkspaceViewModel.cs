using System.IO;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.ViewModels;

public enum TranslationViewMode { Flow, Strings, Unbound }
public enum InspectorMode { Empty, Flow, TranslationEditor }

public sealed class ImagePreviewViewModel : ObservableObject
{
    private ImagePreviewInfo? _selectedImage;
    private BitmapImage? _currentBitmap;
    private bool _hasImages;
    private bool _isLoading;
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
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
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

public sealed class TranslationInspectorViewModel : WorkspaceInspectorViewModelBase
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

    public TranslationInspectorViewModel(TaskCenterViewModel tasks)
    {
        _tasks = tasks;
        UnifyConflictCommand = new RelayCommand(UnifyConflict, () => _target?.HasConflict == true);
    }

    public ObservableCollection<string> PlaceholderTokens { get; } = [];
    public IRelayCommand UnifyConflictCommand { get; }
    public bool HasTarget => _target is not null;
    public bool IsReadOnly => _target is null;
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
        foreach (Match token in Regex.Matches(SourceText, @"\[[^\]\r\n]+\]|%\([^)]+\)[#0\- +]?[0-9.*]*[a-zA-Z]|%(?:[#0\- +]?[0-9.*]*)[a-zA-Z]|\{/?[^{}\r\n]+\}"))
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

    private void UnifyConflict()
    {
        _target?.SharedString?.Unify(TranslationText);
        OnPropertyChanged(nameof(HasConflict));
        UnifyConflictCommand.NotifyCanExecuteChanged();
        _tasks.StatusMessage = "已在内存中统一冲突译文，保存后写入全部物理定义。";
    }
}

public sealed class TranslationWorkspaceViewModel : WorkspaceViewModelBase
{
    private readonly ProjectSessionViewModel _session;
    private readonly IRenPyImagePreviewService _imagePreviewService;
    private TranslationViewMode _viewMode;
    private FlowGroupingMode _groupingMode;
    private string _searchText = string.Empty;
    private ContentItem? _selectedItem;
    private NavigationItem? _selectedLabel;
    private object? _anchorItem;
    private ScrollAnchor? _scrollAnchor;
    private IReadOnlyList<ContentItem> _coverageItems = [];
    private CancellationTokenSource? _previewCancellation;
    private long _previewVersion;

    public TranslationWorkspaceViewModel(ProjectSessionViewModel session, IRenPyImagePreviewService? imagePreviewService = null)
        : base("translation", "翻译", "\uE8A5")
    {
        _session = session;
        _imagePreviewService = imagePreviewService ?? new RenPyImagePreviewService();
        Sidebar = new TranslationSidebarViewModel(this);
        Main = new TranslationMainContentViewModel(this);
        Inspector = new TranslationInspectorViewModel(session.Tasks);
        ImagePreview = new ImagePreviewViewModel();
        OpenSelectedCommand = new RelayCommand(OpenSelected);
        NavigateLabelCommand = new RelayCommand(NavigateSelectedLabel, () => SelectedLabel is not null);
        MovePreviousCommand = new RelayCommand(() => MoveEditable(-1));
        MoveNextCommand = new RelayCommand(() => MoveEditable(1));
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => SearchText.Length > 0);
        Inspector.TranslationChanged += (_, _) => UpdateSelectedPresentation();
        session.SnapshotChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(session.ProjectPath)) _imagePreviewService.InvalidateProject(session.ProjectPath);
            RefreshFromSnapshot();
        };
    }

    public TranslationSidebarViewModel Sidebar { get; }
    public TranslationMainContentViewModel Main { get; }
    public TranslationInspectorViewModel Inspector { get; }
    public ImagePreviewViewModel ImagePreview { get; }
    public override WorkspaceSidebarViewModelBase SidebarContent => Sidebar;
    public override WorkspaceMainContentViewModelBase MainContent => Main;
    public override WorkspaceInspectorViewModelBase InspectorContent => Inspector;
    public ObservableCollection<NavigationItem> Labels { get; } = [];
    public ObservableCollection<ContentItem> VisibleItems { get; } = [];
    public IReadOnlyList<TranslationViewMode> ViewModes { get; } = Enum.GetValues<TranslationViewMode>();
    public IReadOnlyList<FlowGroupingMode> GroupingModes { get; } = Enum.GetValues<FlowGroupingMode>();
    public IRelayCommand OpenSelectedCommand { get; }
    public IRelayCommand NavigateLabelCommand { get; }
    public IRelayCommand MovePreviousCommand { get; }
    public IRelayCommand MoveNextCommand { get; }
    public IRelayCommand ClearSearchCommand { get; }

    public TranslationViewMode ViewMode
    {
        get => _viewMode;
        set { if (SetProperty(ref _viewMode, value)) RebuildVisibleItems(); }
    }

    public FlowGroupingMode GroupingMode
    {
        get => _groupingMode;
        set
        {
            if (!SetProperty(ref _groupingMode, value)) return;
            RebuildVisibleItems();
            RebuildNavigation();
            OnPropertyChanged(nameof(NavigationHeading));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ClearSearchCommand.NotifyCanExecuteChanged();
            RebuildVisibleItems();
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
            if (value is not null) ScrollAnchor = new ScrollAnchor(value.Key, FallbackIndex: VisibleItems.IndexOf(value));
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
    public string NavigationHeading => GroupingMode switch { FlowGroupingMode.SourceFile => "文件", FlowGroupingMode.Label => "LABEL", _ => "剧情入口" };
    public string ViewTitle => ViewMode switch { TranslationViewMode.Strings => "字符串表", TranslationViewMode.Unbound => "未绑定", _ => "剧情流" };
    public int EditableCount { get; private set; }
    public int TranslatedCount { get; private set; }
    public double CoveragePercent { get; private set; }
    public bool HasCoverage => EditableCount > 0;
    public string CoverageText => HasCoverage ? $"{TranslatedCount:N0}/{EditableCount:N0} · {CoveragePercent:0}%" : string.Empty;

    public override Task RefreshAsync(CancellationToken cancellationToken)
    {
        RefreshFromSnapshot();
        return Task.CompletedTask;
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

    private void RefreshFromSnapshot()
    {
        var selectedId = SelectedItem?.Key ?? ScrollAnchor?.ItemId;
        RebuildNavigation();
        RebuildVisibleItems(selectedId);
        Badge = _session.Snapshot is null ? null : _session.Snapshot.TranslationUnits.Count(x => string.IsNullOrWhiteSpace(x.TranslationText)).ToString();
    }

    private void RebuildVisibleItems(string? preferredId = null)
    {
        preferredId ??= SelectedItem?.Key ?? ScrollAnchor?.ItemId;
        VisibleItems.Clear();
        var snapshot = _session.Snapshot;
        if (snapshot is null)
        {
            _coverageItems = [];
            RecalculateCoverage();
            NotifyCollectionPresentation();
            return;
        }

        var projectedItems = (ViewMode switch
        {
            TranslationViewMode.Strings => snapshot.SharedStrings.Select(ContentItem.FromSharedString),
            TranslationViewMode.Unbound => snapshot.TranslationUnits.Where(x => x.IsUnboundFlowTranslation).Select(ContentItem.FromTranslation),
            _ => BuildFlowItems(snapshot, GroupingMode)
        }).ToList();
        _coverageItems = projectedItems;
        RecalculateCoverage();
        IEnumerable<ContentItem> items = projectedItems;
        if (SearchText.Trim().Length > 0) items = items.Where(x => x.SearchText.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        foreach (var item in items) VisibleItems.Add(item);
        SelectedItem = preferredId is null ? null : VisibleItems.FirstOrDefault(x => x.Key == preferredId);
        if (SelectedItem is not null) AnchorItem = SelectedItem;
        NotifyCollectionPresentation();
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
        foreach (var item in TranslationNavigationBuilder.Build(_session.Snapshot, GroupingMode)) Labels.Add(item);
    }

    private void MoveEditable(int direction)
    {
        var index = SelectedItem is null ? (direction > 0 ? 0 : VisibleItems.Count - 1) : VisibleItems.IndexOf(SelectedItem) + direction;
        while (index >= 0 && index < VisibleItems.Count)
        {
            if (VisibleItems[index].IsEditable)
            {
                SelectedItem = VisibleItems[index];
                AnchorItem = SelectedItem;
                Inspector.RequestFocus();
                _session.Tasks.StatusMessage = direction > 0 ? "已切换到下一条可编辑译文。" : "已切换到上一条可编辑译文。";
                return;
            }
            index += direction;
        }
        _session.Tasks.StatusMessage = direction > 0 ? "已经是最后一条可编辑译文。" : "已经是第一条可编辑译文。";
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
        if (item.SharedString is not null)
        {
            foreach (var candidate in _coverageItems.Where(x => ReferenceEquals(x.SharedString, item.SharedString)))
            {
                candidate.Subtitle = item.SharedString.Translation;
                candidate.RefreshStatus();
            }
        }
        else item.RefreshStatus();
        RecalculateCoverage();
    }

    private void RecalculateCoverage()
    {
        var coverage = TranslationCoverageCalculator.Calculate(_coverageItems);
        EditableCount = coverage.EditableCount;
        TranslatedCount = coverage.TranslatedCount;
        CoveragePercent = coverage.Percentage;
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
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _previewVersion);
        _ = UpdateImagePreviewAsync(item, version, _previewCancellation.Token);
    }

    private async Task UpdateImagePreviewAsync(ContentItem? item, long version, CancellationToken cancellationToken)
    {
        var node = item?.Node ?? item?.Unit?.BoundNode;
        var relativePath = node?.Region.RelativePath ?? item?.Unit?.SourcePath;
        var line = node?.Region.StartLine ?? item?.Unit?.SourceLine ?? 0;

        if (string.IsNullOrWhiteSpace(_session.ProjectPath) || string.IsNullOrWhiteSpace(relativePath) || line <= 0)
        {
            ImagePreview.UpdateContext(new RenPySceneContext(null, null, []));
            return;
        }

        try
        {
            var context = await _imagePreviewService.ResolveSceneContextAsync(
                new RenPyImagePreviewRequest(_session.ProjectPath, relativePath, line, item?.Key),
                cancellationToken);
            if (version == Volatile.Read(ref _previewVersion) && ReferenceEquals(item, SelectedItem))
                ImagePreview.UpdateContext(context);
        }
        catch (OperationCanceledException)
        {
            // 快速切换节点时取消旧请求属于正常路径。
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (version == Volatile.Read(ref _previewVersion))
                _session.Tasks.StatusMessage = $"图片预览不可用：{exception.Message}";
        }
    }
}
