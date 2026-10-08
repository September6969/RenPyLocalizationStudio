using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed record ReviewFilterOption(ReviewStatus? Value, string Label);
public sealed record SearchFieldOption(TranslationSearchField Value, string Label);
public sealed partial class TranslationInspectorViewModel
{
    private string _editorialHint = "";
    public string EditorialHint { get => _editorialHint; internal set => SetProperty(ref _editorialHint, value); }
}

public sealed partial class TranslationWorkspaceViewModel
{
    private List<GlossaryTerm> _glossary = [];
    private Dictionary<string, ReviewRecord> _reviews = new(StringComparer.Ordinal);
    private Dictionary<string, ReviewRecord?>? _editorialUndo;
    private ReviewStatus? _reviewFilter;
    private TranslationSearchField _searchField;
    private bool _searchCaseSensitive;
    private bool _searchRegex;
    private string _searchError = "";
    public IReadOnlyList<GlossaryTerm> Glossary => _glossary;
    public IReadOnlyList<ReviewFilterOption> ReviewFilters { get; } = [new(null, "全部校对状态"), new(ReviewStatus.Pending, "待校对"), new(ReviewStatus.Reviewed, "已校对"), new(ReviewStatus.Discussion, "需讨论")];
    public IReadOnlyList<SearchFieldOption> SearchFields { get; } = [new(TranslationSearchField.All, "全部字段"), new(TranslationSearchField.Original, "仅原文"), new(TranslationSearchField.Translation, "仅译文"), new(TranslationSearchField.Location, "仅位置")];
    public ReviewStatus? ReviewFilter { get => _reviewFilter; set { if (SetProperty(ref _reviewFilter, value)) ApplySearchFilter(); } }
    public TranslationSearchField SearchField { get => _searchField; set { if (SetProperty(ref _searchField, value)) ApplySearchFilter(); } }
    public bool SearchCaseSensitive { get => _searchCaseSensitive; set { if (SetProperty(ref _searchCaseSensitive, value)) ApplySearchFilter(); } }
    public bool SearchRegex { get => _searchRegex; set { if (SetProperty(ref _searchRegex, value)) ApplySearchFilter(); } }
    public string SearchError { get => _searchError; private set => SetProperty(ref _searchError, value); }
    public TranslationSearchOptions SearchOptions => new(SearchField, SearchCaseSensitive, SearchRegex);
    public bool CanReviewSelection => SelectedItem?.IsEditable == true && SelectedItem.Unit?.IsRawMode != true && SelectedItem.SharedString?.HasConflict != true && !_session.Tasks.IsBusy;
    public string SelectedReviewText => SelectedItem is null ? "未选择译文" : ReviewLabel(ReviewFor(SelectedItem));
    public string SelectedReviewNote => SelectedItem is null ? "" : _reviews.GetValueOrDefault(TranslationIdentity.Key(ReadItem(SelectedItem)))?.Note ?? "";
    public string EditorialLanguage => _session.Snapshot?.Language ?? "";
    public string EditorialProject => _session.Snapshot?.ProjectRoot ?? "";
    public IRelayCommand MarkReviewedCommand { get; private set; } = null!;
    public IRelayCommand MarkDiscussionCommand { get; private set; } = null!;
    public IRelayCommand MarkPendingCommand { get; private set; } = null!;

    private void InitializeEditorial()
    {
        MarkReviewedCommand = new RelayCommand(() => SetReview(ReviewStatus.Reviewed, SelectedReviewNote), () => CanReviewSelection);
        MarkDiscussionCommand = new RelayCommand(() => SetReview(ReviewStatus.Discussion, SelectedReviewNote), () => CanReviewSelection);
        MarkPendingCommand = new RelayCommand(() => SetReview(ReviewStatus.Pending, SelectedReviewNote), () => CanReviewSelection);
        _session.Tasks.PropertyChanged += OnEditorialBusyChanged;
    }
    private void OnEditorialBusyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshEditorialSelection();
    public static string ReviewLabel(ReviewStatus status) => status switch { ReviewStatus.Reviewed => "已校对", ReviewStatus.Discussion => "需讨论", _ => "待校对" };
    private static TranslationReadEntry ReadItem(ContentItem item)
    {
        var unit = item.Unit; var shared = item.SharedString;
        return new(0, shared?.OldText ?? (unit is not null ? TranslationIdentity.Original(unit) : item.BodyText),
            shared?.Translation ?? (unit?.IsRawMode == true ? unit.RawBodyText : unit?.TranslationText) ?? "", shared?.HasConflict == true,
            unit?.IsRawMode == true, item.IsEditable, unit?.RelativeTlPath ?? shared?.Definitions.FirstOrDefault()?.RelativeTlPath,
            unit?.HeaderLine, item.Node is { } node ? $"{node.Region.RelativePath}:{node.Region.StartLine}" : item.Subtitle, [], unit, shared);
    }
    private ReviewStatus ReviewFor(ContentItem item)
    {
        if (!item.IsEditable || _reviews.Count == 0) return ReviewStatus.Pending;
        var row = ReadItem(item);
        return TranslationIdentity.Status(_reviews.GetValueOrDefault(TranslationIdentity.Key(row)), row.Original ?? "", row.Translation);
    }
    private void RefreshEditorialSelection()
    {
        OnPropertyChanged(nameof(CanReviewSelection)); OnPropertyChanged(nameof(SelectedReviewText)); OnPropertyChanged(nameof(SelectedReviewNote));
        MarkReviewedCommand.NotifyCanExecuteChanged(); MarkDiscussionCommand.NotifyCanExecuteChanged(); MarkPendingCommand.NotifyCanExecuteChanged();
        if (Inspector is null) return;
        var row = SelectedItem?.IsEditable == true ? ReadItem(SelectedItem) : null;
        Inspector.EditorialHint = row is null ? "" : SelectedReviewText + (SelectedReviewNote.Length > 0 ? " · " + SelectedReviewNote : "") + "\n" +
            string.Join("\n", GlossaryChecker.Check(row.Original ?? "", row.Translation, _glossary).Select(d => d.Message));
    }
    public void SetReview(ReviewStatus status, string note)
    {
        if (!CanReviewSelection) return;
        var row = ReadItem(SelectedItem!);
        if (_bulkUndo?.Any(change => TranslationIdentity.Key(change.Target) == TranslationIdentity.Key(row)) == true) _bulkUndoEdited = true;
        _reviews[TranslationIdentity.Key(row)] = new(row.Original ?? "", row.Translation, status, note);
        EditorialChanged();
    }
    public async Task SaveGlossaryAsync(IEnumerable<GlossaryTerm> terms)
    {
        if (_session.Snapshot is null || _session.Tasks.IsBusy) throw new InvalidOperationException("请先等待当前任务完成。");
        var copy = terms.ToArray();
        if (copy.Any(t => string.IsNullOrWhiteSpace(t.Source) || string.IsNullOrWhiteSpace(t.Preferred) || t.Source.Length > 200))
            throw new ArgumentException("术语原文和推荐译名不能为空，原文最多 200 字符。");
        if (copy.GroupBy(t => t.Source, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new ArgumentException("请合并重复术语。");
        _glossary = copy.ToList(); OnPropertyChanged(nameof(Glossary));
        EditorialChanged(); NeedsResultsRefresh = true;
        if (!await _session.PersistSettingsAsync()) throw new System.IO.IOException("术语已保留在内存，但设置保存失败，请重试。");
    }
    public Task<bool> SaveEditorialSettingsAsync() { RememberWorkspaceState(); return _session.PersistSettingsAsync(); }
    private void EditorialChanged()
    {
        if (IsComputing) CancelResults();
        _filterRows = null;
        RefreshEditorialSelection();
        NeedsResultsRefresh = true;
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void InvalidateReviewOfSelection()
    {
        if (SelectedItem?.IsEditable != true) return;
        var row = ReadItem(SelectedItem); var key = TranslationIdentity.Key(row);
        if (_reviews.TryGetValue(key, out var record) && (record.Original != row.Original || record.Translation != row.Translation))
            _reviews[key] = new(row.Original ?? "", row.Translation, ReviewStatus.Pending, record.Note);
        EditorialChanged();
    }
    private void InvalidateChangedReviews()
    {
        if (_session.Snapshot is not { } snapshot) return;
        foreach (var row in TranslationReadSnapshot.Capture(snapshot))
        {
            var key = TranslationIdentity.Key(row);
            if (_reviews.TryGetValue(key, out var record) && (record.Original != row.Original || record.Translation != row.Translation))
                _reviews[key] = new(row.Original ?? "", row.Translation, ReviewStatus.Pending, record.Note);
        }
        RememberWorkspaceState();
    }
    private void RestoreEditorial(TranslationWorkspaceState? saved)
    {
        _searchField = saved is not null && Enum.IsDefined(saved.SearchField) ? saved.SearchField : TranslationSearchField.All;
        _searchCaseSensitive = saved?.SearchCaseSensitive ?? false;
        _searchRegex = saved?.SearchRegex ?? false;
        _reviewFilter = saved?.ReviewFilter is { } filter && Enum.IsDefined(filter) ? filter : null;
        SearchError = "";
        foreach (var name in new[] { nameof(SearchField), nameof(SearchCaseSensitive), nameof(SearchRegex), nameof(ReviewFilter) }) OnPropertyChanged(name);
        _glossary = (saved?.Glossary ?? []).Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Source) && !string.IsNullOrWhiteSpace(t.Preferred)).ToList();
        _reviews = new Dictionary<string, ReviewRecord>((saved?.Reviews ?? new Dictionary<string, ReviewRecord>()).Where(kv => kv.Value is not null), StringComparer.Ordinal);
        InvalidateChangedReviews(); OnPropertyChanged(nameof(Glossary)); RefreshEditorialSelection();
    }
    private IReadOnlyList<TranslationQualityIssue> CheckEditorialQuality(IReadOnlyList<TranslationReadEntry> captured, IReadOnlyList<GlossaryTerm> terms, CancellationToken token)
    {
        var existing = _quality.Check(captured, token).ToDictionary(i => (object?)i.SharedString ?? i.Unit!);
        foreach (var row in captured)
        {
            token.ThrowIfCancellationRequested();
            if (row.Raw || row.Conflict) continue;
            var issues = GlossaryChecker.Check(row.Original ?? "", row.Translation, terms);
            if (issues.Count == 0) continue;
            var key = (object?)row.Shared ?? row.Unit!;
            existing[key] = existing.TryGetValue(key, out var prior) ? prior with { Diagnostics = prior.Diagnostics.Concat(issues).ToArray() }
                : new(row.Unit, row.Shared, row.Original ?? "", issues);
        }
        return existing.Values.ToArray();
    }
    private TranslationSearch? CreateSearch()
    {
        try { var query = new TranslationSearch(SearchText, SearchOptions); SearchError = ""; return query; }
        catch (ArgumentException ex) { SearchError = "搜索表达式无效：" + ex.Message; return null; }
    }
    private bool SearchMatches(TranslationSearch query, ContentItem item)
    {
        var row = ReadItem(item);
        return query.Matches(item.SearchText, row.Original ?? "", row.Translation, $"{row.Path} {item.Node?.Region.RelativePath} {item.Node?.LabelName}");
    }

    public IReadOnlyList<ReviewExchangeRow> ExportReviewRows()
    {
        if (_session.Snapshot is not { } snapshot) return [];
        return TranslationReadSnapshot.Capture(snapshot).Where(r => r.Writable && !r.Raw && !r.Conflict).Select(row =>
        {
            var key = TranslationIdentity.Key(row); var record = _reviews.GetValueOrDefault(key);
            return new ReviewExchangeRow(snapshot.Language, key, row.Original ?? "", row.Translation, row.Translation,
                ReviewLabel(TranslationIdentity.Status(record, row.Original ?? "", row.Translation)), record?.Note ?? "");
        }).ToArray();
    }

    private IReadOnlyList<ReviewExchangeRow>? _importMetadata;
    public async Task<IReadOnlyList<BulkTranslationProposal>> PreviewEditorialAsync(string mode, string find, string replacement,
        TranslationSearchOptions options, IReadOnlyList<ReviewExchangeRow>? csv, bool entireProject, CancellationToken token)
    {
        if (_session.Snapshot is not { } snapshot || _session.Tasks.IsBusy) throw new InvalidOperationException("请先分析项目并等待任务完成。");
        if (!entireProject)
        {
            // 当前范围必须等待质量检查及其后续筛选全部完成。
            if (NeedsResultsRefresh) RebuildProjection();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var pending = PendingResults;
                await pending.WaitAsync(token);
                if (ReferenceEquals(pending, PendingResults)) break;
            }
        }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(snapshot, _session.Snapshot) || _session.Tasks.IsBusy || !entireProject && IsComputing)
            throw new InvalidOperationException("当前范围仍在变化，请等待筛选完成后重新预览。");
        var filterVersion = _filterVersion;
        var captured = CaptureCurrentTranslations(snapshot); var version = _editVersion; var operation = _operationVersion;
        var selectedUnits = VisibleItems.Select(i => i.Unit).ToHashSet(); var selectedShared = VisibleItems.Select(i => i.SharedString).ToHashSet();
        var targets = entireProject ? captured : captured.Where(r => r.Shared is not null ? selectedShared.Contains(r.Shared) : selectedUnits.Contains(r.Unit)).ToArray();
        var plan = await Task.Run(() => mode switch
        {
            "replace" => TranslationEditPlanner.Replace(targets, new TranslationSearch(find, options), replacement, token),
            "import" => TranslationEditPlanner.Import(targets, csv ?? [], snapshot.Language),
            "migrate" => TranslationEditPlanner.Migrate(targets, csv ?? [], snapshot.Language, token),
            _ => throw new ArgumentException("未知操作")
        }, token);
        token.ThrowIfCancellationRequested();
        if (version != _editVersion || operation != _operationVersion || !entireProject && filterVersion != _filterVersion || !ReferenceEquals(snapshot, _session.Snapshot)) throw new InvalidOperationException("项目或译文已变化，请重新预览。");
        _bulkSnapshot = snapshot; _bulkCapture = captured; _bulkEditVersion = version; _bulkPlan = plan;
        _importMetadata = mode == "import" ? csv : null;
        return plan;
    }
    public bool ApplyEditorial(IReadOnlyList<BulkTranslationProposal> selected)
    {
        if (selected.GroupBy(p => p.Target.Id).Any(g => g.Count() > 1)) throw new InvalidOperationException("同一目标只能选择一种译法。");
        var metadata = _importMetadata;
        var priorReviews = selected.ToDictionary(change => TranslationIdentity.Key(change.Target), change => _reviews.GetValueOrDefault(TranslationIdentity.Key(change.Target)));
        if (!ApplyBulk(selected)) return false;
        _editorialUndo = priorReviews;
        if (metadata is not null)
            foreach (var change in selected)
            {
                var key = TranslationIdentity.Key(change.Target); var incoming = metadata.Single(r => r.Key == key);
                var status = incoming.Status switch { "已校对" or "Reviewed" => ReviewStatus.Reviewed, "需讨论" or "Discussion" => ReviewStatus.Discussion, _ => ReviewStatus.Pending };
                _reviews[key] = new(change.Target.Original ?? "", change.Translation!, status, incoming.Note);
            }
        _importMetadata = null; EditorialChanged();
        return true;
    }

    private void RestoreEditorialUndo()
    {
        foreach (var (key, record) in _editorialUndo ?? [])
        {
            if (record is null) _reviews.Remove(key);
            else _reviews[key] = record;
        }
        _editorialUndo = null;
    }

    private ProjectSnapshot? _comparisonLocal;
    private ProjectSnapshot? _comparisonDisk;
    private IReadOnlyList<TranslationReadEntry>? _comparisonCapture;
    private long _comparisonVersion;
    private IReadOnlyList<ExternalMergeRow>? _comparisonRows;
    public async Task<IReadOnlyList<ExternalMergeRow>> PreviewExternalAsync(CancellationToken token)
    {
        if (_session.Snapshot is not { } local || _session.Tasks.IsBusy) throw new InvalidOperationException("请先分析项目并等待任务完成。");
        var capture = ExternalTranslationMerge.Capture(local); var version = _editVersion;
        var baseline = ExternalTranslationMerge.Baselines(local);
        var disk = await new ProjectAnalysisService(new FileSystemService()).ExecuteAsync(new(local.ProjectRoot, local.Language), _session.Tasks.CreateProgress(), token);
        if (!disk.IsSuccess || disk.Value is null) throw new InvalidOperationException("无法读取磁盘项目：" + string.Join("；", disk.Diagnostics.Select(d => d.Message)));
        if (disk.Value.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Category is DiagnosticCategory.FileSystem or DiagnosticCategory.Encoding or DiagnosticCategory.Parsing or DiagnosticCategory.Security))
            throw new InvalidOperationException("磁盘项目存在读取或解析错误，请先修复诊断后重新对比。");
        if (!ReferenceEquals(local, _session.Snapshot) || version != _editVersion) throw new InvalidOperationException("本地项目已变化，请重新对比。");
        var rows = ExternalTranslationMerge.Plan(capture, ExternalTranslationMerge.Capture(disk.Value), baseline);
        _comparisonLocal = local; _comparisonDisk = disk.Value; _comparisonCapture = capture; _comparisonVersion = version; _comparisonRows = rows;
        return rows;
    }
    public async Task ApplyExternalAsync(IReadOnlyDictionary<string, MergeChoice> choices, CancellationToken token)
    {
        if (_comparisonRows is null || _comparisonDisk is null || _comparisonCapture is null || _session.Tasks.IsBusy ||
            !ReferenceEquals(_comparisonLocal, _session.Snapshot) || _comparisonVersion != _editVersion ||
            !TranslationReadSnapshot.Matches(_comparisonCapture, ExternalTranslationMerge.Capture(_session.Snapshot!))) throw new InvalidOperationException("本地译文已变化，请重新对比。");
        foreach (var row in _comparisonRows)
        {
            if (!choices.TryGetValue(row.Key, out var choice) || choice == MergeChoice.Unresolved || !Enum.IsDefined(choice) || choice == MergeChoice.Local && !row.AllowLocal)
                throw new InvalidOperationException("请为每条差异选择可用的保留版本。");
        }
        var diskCheck = await new ProjectAnalysisService(new FileSystemService()).ExecuteAsync(new(_comparisonDisk.ProjectRoot, _comparisonDisk.Language), _session.Tasks.CreateProgress(), token);
        static string[] Manifest(ProjectSnapshot snapshot) => snapshot.Sources.Select(d => d.File).Concat(snapshot.TlDocuments.Select(d => d.File))
            .Select(file => file.FullPath.ToUpperInvariant() + ":" + file.Sha256).Order(StringComparer.Ordinal).ToArray();
        if (!diskCheck.IsSuccess || diskCheck.Value is null || diskCheck.Value.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Category is DiagnosticCategory.FileSystem or DiagnosticCategory.Encoding or DiagnosticCategory.Parsing or DiagnosticCategory.Security) || !Manifest(_comparisonDisk).SequenceEqual(Manifest(diskCheck.Value)))
            throw new InvalidOperationException("磁盘文件再次变化（包括新增或删除），请重新对比。");
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_comparisonLocal, _session.Snapshot) || _comparisonVersion != _editVersion || _session.Tasks.IsBusy ||
            !TranslationReadSnapshot.Matches(_comparisonCapture, ExternalTranslationMerge.Capture(_session.Snapshot!)))
            throw new InvalidOperationException("本地状态已变化，请重新对比。");
        foreach (var row in _comparisonRows.Where(r => choices[r.Key] == MergeChoice.Local)) SetEntryTranslation(row.Disk!, row.Local!.Translation);
        RememberWorkspaceState();
        var merged = _comparisonDisk;
        _comparisonDisk = null; _comparisonRows = null;
        _session.AdoptComparedSnapshot(merged);
        _session.Tasks.StatusMessage = "已载入磁盘版本并保留选定的本地译文，尚未写入文件。请核对后保存。";
    }
}
