using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed partial class TranslationWorkspaceViewModel
{
    private WorkspaceTaskCoordinator _operations = null!;
    private IReadOnlyList<TranslationReadEntry>? _readStructure;
    private ProjectSnapshot? _readSnapshot;
    private IReadOnlyList<TranslationReadEntry> CaptureCurrentTranslations(ProjectSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, _readSnapshot))
        {
            _readSnapshot = snapshot;
            _readStructure = TranslationReadSnapshot.Capture(snapshot);
            return _readStructure;
        }
        return TranslationReadSnapshot.Refresh(_readStructure!);
    }
    private long _operationVersion;
    private long _editVersion;
    private long _filterVersion;
    private bool _restoringState;
    private bool _stateScopeInvalidated;
    private IReadOnlyList<SavedBookmark>? _bookmarkRecords;
    private long _selectionVersion;
    private bool _isComputing;
    private bool _qualityComputing;
    private NavigateToSourceRequest? _pendingNavigation;
    private readonly List<SavedBookmark> _unresolvedBookmarks = [];
    private IReadOnlyList<TranslationReadEntry>? _bulkCapture;
    private ProjectSnapshot? _bulkSnapshot;
    private long _bulkEditVersion;
    private IReadOnlyList<BulkTranslationProposal>? _bulkPlan;
    private IReadOnlyList<BulkTranslationProposal>? _bulkUndo;
    private bool _bulkUndoEdited;
    public Task PendingResults { get; private set; } = Task.CompletedTask;
    public bool IsComputing
    {
        get => _isComputing;
        private set { if (SetProperty(ref _isComputing, value)) OnPropertyChanged(nameof(CanOpenBulk)); }
    }
    public bool CanOpenBulk => _session.Snapshot is not null && !_session.Tasks.IsBusy && !IsComputing;
    public IRelayCommand CancelResultsCommand { get; private set; } = null!;
    public IRelayCommand UndoBulkCommand { get; private set; } = null!;
    public string RecoveryMessage { get; private set; } = string.Empty;
    public bool HasRecoveryMessage => RecoveryMessage.Length > 0;

    private void InitializeOperations()
    {
        PositionChanged += OnWorkspacePositionChanged;
        InitializeEditorial();
        _operations = new(ex =>
        {
            if (Volatile.Read(ref _disposed) != 0) return; IsComputing = false; _qualityComputing = false;
            if (ex is System.Text.RegularExpressions.RegexMatchTimeoutException) SearchError = "正则表达式执行超时，请简化后重试。";
            _session.Tasks.StatusMessage = $"计算失败：{ex.Message}";
        });
        CancelResultsCommand = new RelayCommand(CancelResults);
        UndoBulkCommand = new RelayCommand(() => UndoBulk(), () => _bulkUndo is not null && !_session.Tasks.IsBusy);
        _session.Tasks.PropertyChanged += OnOperationBusyChanged;
    }

    private void OnWorkspacePositionChanged(object? sender, EventArgs args) => RememberWorkspaceState();

    private void OnOperationBusyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        UndoBulkCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenBulk));
    }
    private void CancelResults()
    {
        _operationVersion++;
        _filterVersion++;
        _operations.StartLatest("quality", _ => Task.CompletedTask);
        _operations.StartLatest("filter", _ => Task.CompletedTask);
        IsComputing = false;
        _qualityComputing = false;
        _pendingNavigation = null;
    }
    private void ResetOperations()
    {
        CancelResults();
        _bulkPlan = null;
        _bulkCapture = null;
        _bulkSnapshot = null;
        _bulkUndo = null;
        _editorialUndo = null;
        _comparisonRows = null;
        _comparisonLocal = null;
        _comparisonDisk = null;
        _comparisonCapture = null;
        UndoBulkCommand.NotifyCanExecuteChanged();
        _filterRows = null;
        _readSnapshot = null;
        _readStructure = null;
        _bookmarkRecords = null;
        OnPropertyChanged(nameof(CanOpenBulk));
    }
    private void DisposeOperations()
    {
        _operationVersion++;
        _filterVersion++;
        PositionChanged -= OnWorkspacePositionChanged;
        _session.Tasks.PropertyChanged -= OnOperationBusyChanged;
        _operations.Dispose();
        _session.Tasks.PropertyChanged -= OnEditorialBusyChanged;
    }
    private void InvalidateEditedOperations()
    {
        InvalidateReviewOfSelection();
        _editVersion++;
        if (_bulkUndo?.Any(p => p.Target.Shared is not null ? ReferenceEquals(p.Target.Shared, SelectedItem?.SharedString)
                : ReferenceEquals(p.Target.Unit, SelectedItem?.Unit)) == true) _bulkUndoEdited = true;
        if (IsComputing) { CancelResults(); NeedsResultsRefresh = true; }
        _filterRows = null;
    }

    public async Task<IReadOnlyList<BulkTranslationProposal>> PreviewBulkAsync(bool entireProject, CancellationToken token)
    {
        if (_session.Snapshot is not { } snapshot || _session.Tasks.IsBusy) return [];
        if (!entireProject)
        {
            // 预览范围必须对应当前筛选，不能把仍显示在界面的上一轮结果当成新范围。
            if (NeedsResultsRefresh) RebuildProjection();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var pending = PendingResults;
                await pending.WaitAsync(token);
                if (ReferenceEquals(pending, PendingResults)) break;
            }
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(snapshot, _session.Snapshot) || _session.Tasks.IsBusy || IsComputing) return [];
        }
        var version = _operationVersion;
        var editVersion = _editVersion;
        var filterVersion = _filterVersion;
        var captured = CaptureCurrentTranslations(snapshot);
        var units = VisibleItems.Where(i => i.Unit is not null).Select(i => i.Unit!).ToHashSet();
        var shared = VisibleItems.Where(i => i.SharedString is not null).Select(i => i.SharedString!).ToHashSet();
        var ids = entireProject ? null : captured.Where(e => e.Shared is not null ? shared.Contains(e.Shared)
            : e.Unit is not null && units.Contains(e.Unit)).Select(e => e.Id).ToHashSet();
        var plan = await Task.Run(() => new BulkTranslationPlanner().Plan(captured, ids, token), token);
        token.ThrowIfCancellationRequested();
        if (version != _operationVersion || editVersion != _editVersion || filterVersion != _filterVersion || !ReferenceEquals(snapshot, _session.Snapshot)) return [];
        _bulkSnapshot = snapshot;
        _bulkCapture = captured;
        _bulkEditVersion = editVersion;
        _bulkPlan = plan;
        return plan;
    }

    public bool ApplyBulk(IReadOnlyList<BulkTranslationProposal> selected)
    {
        if (_session.Tasks.IsBusy) return false;
        if (_bulkCapture is null || _bulkPlan is null || _bulkEditVersion != _editVersion ||
            !ReferenceEquals(_bulkSnapshot, _session.Snapshot) || _session.Snapshot is null ||
            !TranslationReadSnapshot.Matches(_bulkCapture, CaptureCurrentTranslations(_session.Snapshot)))
        { _session.Tasks.StatusMessage = "译文或项目已变化，请刷新批量预览。"; return false; }
        var planItems = _bulkPlan.ToHashSet();
        if (selected.Count == 0 || selected.Any(p => !p.CanApply || !planItems.Contains(p))) return false;
        var changes = selected.DistinctBy(p => p.Target.Id).ToArray();
        foreach (var change in changes) SetEntryTranslation(change.Target, change.Translation!);
        _bulkUndo = changes;
        _editorialUndo = null;
        _bulkUndoEdited = false;
        _bulkPlan = null;
        _bulkCapture = null;
        PublishBulkChange();
        _session.Tasks.StatusMessage = $"已应用 {changes.Length:N0} 条批量编辑，可撤销。";
        return true;
    }

    private static void SetEntryTranslation(TranslationReadEntry entry, string translation)
    {
        if (entry.Shared is not null) entry.Shared.Translation = translation;
        else if (entry.Unit is not null) { entry.Unit.TranslationText = translation; entry.Unit.IsDirty = true; }
    }

    public bool UndoBulk()
    {
        if (_bulkUndo is null || _session.Tasks.IsBusy) return false;
        if (_bulkUndoEdited || _bulkUndo.Any(p => (p.Target.Shared?.Translation ?? p.Target.Unit!.TranslationText) != p.Translation || p.Target.Shared?.HasConflict == true))
        { _session.Tasks.StatusMessage = "批量目标已被再次编辑，未执行撤销。"; return false; }
        foreach (var change in _bulkUndo) SetEntryTranslation(change.Target, change.Target.Translation);
        RestoreEditorialUndo();
        _bulkUndo = null;
        PublishBulkChange();
        _session.Tasks.StatusMessage = "已撤销上次批量复用，保存后写回文件。";
        return true;
    }

    private void RebindSavedUndo(ProjectSnapshot snapshot, IReadOnlyList<BulkTranslationProposal> changes, bool edited)
    {
        var captured = CaptureCurrentTranslations(snapshot);
        static (bool Shared, string? Original, string? Path, string? Identifier, int? Line) Key(TranslationReadEntry row) =>
            (row.Shared is not null, row.Original, row.Shared is null ? row.Path : null, row.Unit?.Identifier,
                row.Shared is null && row.Unit?.Identifier is null ? row.Line : null);
        var byIdentity = captured.ToLookup(Key);
        var rebound = new List<BulkTranslationProposal>();
        foreach (var change in changes)
        {
            var old = change.Target;
            var matches = byIdentity[Key(old)].Where(row => row.Translation == change.Translation && !row.Conflict).Take(2).ToArray();
            if (matches.Length != 1) return;
            rebound.Add(change with { Target = matches[0] with { Translation = old.Translation } });
        }
        _bulkUndo = rebound;
        _bulkUndoEdited = edited;
        UndoBulkCommand.NotifyCanExecuteChanged();
    }

    private void PublishBulkChange()
    {
        _editVersion++;
        CancelResults();
        _filterRows = null;
        foreach (var item in _coverageItems)
        {
            item.RefreshStatus();
            if (item.SharedString is not null && item.Diagnostic is null) item.Subtitle = item.SharedString.Translation;
        }
        UpdateInspector(SelectedItem);
        InvalidateChangedReviews();
        RefreshEditorialSelection();
        RecalculateCoverage();
        NeedsResultsRefresh = true;
        UndoBulkCommand.NotifyCanExecuteChanged();
        MoveNextPendingCommand.NotifyCanExecuteChanged();
        _session.ScheduleAutoSave();
    }

    public void RememberWorkspaceState()
    {
        if (_restoringState || _stateScopeInvalidated || _session.Snapshot is not { } snapshot) return;
        _bookmarkRecords ??= (_bookmarkedNodeIds.Count == 0 ? Enumerable.Empty<SavedBookmark>() :
            snapshot.Graph.Nodes.Where(n => _bookmarkedNodeIds.Contains(n.Id)).Select(n => WorkspaceStateIdentity.Bookmark(snapshot, n)))
            .Concat(_unresolvedBookmarks).ToArray();
        _session.RememberWorkspaceState(new(snapshot.ProjectRoot, snapshot.Language,
            SelectedItem?.Key ?? ScrollAnchor?.ItemId, ViewMode.ToString(), GroupingMode.ToString(),
            SearchText, StatusFilter.ToString(), SelectedScope, _bookmarkRecords, _glossary.ToArray(), new Dictionary<string, ReviewRecord>(_reviews),
            SearchField, SearchCaseSensitive, SearchRegex, ReviewFilter));
    }

    private void RestoreSavedState(ProjectSnapshot? snapshot)
    {
        if (snapshot is null) { RestoreEditorial(null); return; }
        var saved = _session.GetWorkspaceState(snapshot.ProjectRoot, snapshot.Language);
        RestoreEditorial(saved);
        if (saved is null)
        {
            _bookmarkedNodeIds.Clear();
            _unresolvedBookmarks.Clear();
            RecoveryMessage = string.Empty;
            OnPropertyChanged(nameof(RecoveryMessage));
            OnPropertyChanged(nameof(HasRecoveryMessage));
            RefreshFromSnapshot();
            return;
        }
        _bookmarkedNodeIds.Clear();
        _unresolvedBookmarks.Clear();
        foreach (var bookmark in saved.Bookmarks ?? [])
        {
            if (bookmark is null) continue;
            var node = WorkspaceStateIdentity.Resolve(snapshot, bookmark);
            if (node is not null) _bookmarkedNodeIds.Add(node.Id);
            else _unresolvedBookmarks.Add(bookmark);
        }
        _viewMode = Enum.TryParse<TranslationViewMode>(saved.ViewMode, out var mode) && Enum.IsDefined(mode) ? mode : TranslationViewMode.Flow;
        _groupingMode = Enum.TryParse<FlowGroupingMode>(saved.GroupingMode, out var grouping) && Enum.IsDefined(grouping) ? grouping : FlowGroupingMode.StoryPath;
        _statusFilter = Enum.TryParse<TranslationStatusFilter>(saved.StatusFilter, out var status) && Enum.IsDefined(status) ? status : TranslationStatusFilter.All;
        _searchText = saved.SearchText ?? string.Empty;
        _selectedScope = saved.Scope ?? "all";
        RecoveryMessage = _unresolvedBookmarks.Count > 0 ? $"{_unresolvedBookmarks.Count} 个书签已失效，未自动猜测位置。" : string.Empty;
        foreach (var name in new[] { nameof(ViewMode), nameof(GroupingMode), nameof(StatusFilter), nameof(SearchText), nameof(SelectedScope),
                     nameof(IsQualityView), nameof(IsBookmarkView), nameof(BookmarkedCount), nameof(RecoveryMessage), nameof(HasRecoveryMessage), nameof(NavigationHeading) })
            OnPropertyChanged(name);
        RebuildNavigation();
        RebuildProjection(saved.ItemId);
    }

    private sealed record FilterRow(int Index, string Text, string Original, string Translation, string Location, ReviewStatus Review, bool Editable, bool Complete, bool Conflict, bool Modified);
    private FilterRow[]? _filterRows;

    private void StartFilter(string? preferredId, bool debounce)
    {
        var snapshot = _session.Snapshot;
        var items = _coverageItems;
        var filter = StatusFilter;
        var query = CreateSearch();
        if (query is null) return;
        var reviewFilter = ReviewFilter;
        var version = ++_filterVersion;
        var editVersion = _editVersion;
        var operation = _operationVersion;
        var selection = _selectionVersion;
        _filterRows ??= items.Select((i, index) =>
        {
            var row = ReadItem(i);
            return new FilterRow(index, i.SearchText, row.Original ?? "", row.Translation, $"{row.Path} {i.Node?.Region.RelativePath} {i.Node?.LabelName}", ReviewFor(i), i.IsEditable, i.IsTranslationComplete, i.SharedString?.HasConflict == true, i.IsModified);
        }).ToArray();
        var rows = _filterRows;
        var inScope = items.Select(i => _progressScopes.Contains(i, SelectedScope)).ToArray();
        IsComputing = true;
        PendingResults = _operations.RunLatestOnContextAsync("filter", async token =>
        {
            if (debounce) await Task.Delay(200, token);
            var indices = await Task.Run(() =>
            {
                var result = new List<int>();
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    if (!inScope[row.Index]) continue;
                    var matches = filter switch
                    {
                        TranslationStatusFilter.Pending => row.Editable && !row.Complete && !row.Conflict,
                        TranslationStatusFilter.Completed => row.Editable && row.Complete,
                        TranslationStatusFilter.Conflict => row.Conflict,
                        TranslationStatusFilter.Modified => row.Modified,
                        _ => true
                    };
                    if (matches && (reviewFilter is null || row.Editable && row.Review == reviewFilter) && query.Matches(row.Text, row.Original, row.Translation, row.Location)) result.Add(row.Index);
                }
                return result;
            }, token);
            if (token.IsCancellationRequested || version != _filterVersion || operation != _operationVersion || editVersion != _editVersion ||
                !ReferenceEquals(snapshot, _session.Snapshot) || Volatile.Read(ref _disposed) != 0) return;
            PublishFilteredItems(indices.Select(i => items[i]).ToArray(), selection == _selectionVersion ? preferredId : SelectedItem?.Key);
            IsComputing = _qualityComputing;
        });
    }

    private void StartQuality(ProjectSnapshot snapshot, string? preferredId)
    {
        var version = ++_operationVersion;
        var editVersion = _editVersion;
        IsComputing = true;
        _qualityComputing = true;
        PendingResults = _operations.RunLatestOnContextAsync("quality", async token =>
        {
            // 合并同一轮界面事件中的重复刷新，取消后不再生成大型数据副本。
            await Task.Delay(1, token);
            if (version != _operationVersion || editVersion != _editVersion || !ReferenceEquals(snapshot, _session.Snapshot)) return;
            var captured = CaptureCurrentTranslations(snapshot);
            var terms = _glossary.ToArray();
            var issues = await Task.Run(() => CheckEditorialQuality(captured, terms, token), token);
            var items = new List<ContentItem>();
            foreach (var issue in issues)
            {
                token.ThrowIfCancellationRequested();
                items.Add(ContentItem.FromQualityIssue(issue));
                if (items.Count % 256 == 0)
                {
                    if (System.Windows.Application.Current is not null)
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    else await Task.Yield();
                }
            }
            if (token.IsCancellationRequested || version != _operationVersion || editVersion != _editVersion ||
                !ReferenceEquals(snapshot, _session.Snapshot) || !IsQualityView || Volatile.Read(ref _disposed) != 0) return;
            FinishProjection(items, preferredId);
            // 大项目的最终筛选仍由协调器执行，等待它完成才结束本次刷新。
            if (items.Count > 2000) await PendingResults;
            if (version == _operationVersion) { _qualityComputing = false; IsComputing = false; }
        });
    }
}
