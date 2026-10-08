using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed class GlossaryRow : ObservableObject
{
    private string _source = "", _preferred = "", _forbidden = "";
    private bool _caseSensitive;
    public string Source { get => _source; set => SetProperty(ref _source, value); }
    public string Preferred { get => _preferred; set => SetProperty(ref _preferred, value); }
    public string Forbidden { get => _forbidden; set => SetProperty(ref _forbidden, value); }
    public bool CaseSensitive { get => _caseSensitive; set => SetProperty(ref _caseSensitive, value); }
}
public sealed record MergeChoiceOption(MergeChoice Value, string Label);
public sealed class MergeRowViewModel(ExternalMergeRow row) : ObservableObject
{
    private MergeChoice _choice = row.DefaultChoice;
    public ExternalMergeRow Row { get; } = row;
    public MergeChoice Choice { get => _choice; set => SetProperty(ref _choice, value); }
    public IReadOnlyList<MergeChoiceOption> Options => Row.AllowLocal ? [new(MergeChoice.Unresolved, "请选择"), new(MergeChoice.Disk, "采用磁盘"), new(MergeChoice.Local, "保留本地")]
        : [new(MergeChoice.Unresolved, "请选择"), new(MergeChoice.Disk, "采用磁盘")];
    public string Original => Row.Disk?.Original ?? Row.Local?.Original ?? "复杂块";
    public string Local => Row.Local?.Translation ?? "（不存在）";
    public string Disk => Row.Disk?.Translation ?? "（已删除）";
    public string Baseline => Row.Baseline;
    public string Reason => Row.Reason;
    public string Location => Row.Disk?.Impact ?? Row.Local?.Impact ?? "";
}

public sealed class TranslationToolsViewModel : ObservableObject, IDisposable
{
    public TranslationWorkspaceViewModel Workspace { get; }
    public ObservableCollection<GlossaryRow> Terms { get; } = [];
    public IReadOnlyList<string> Modes { get; } = ["查找替换", "校对表回填", "旧版译文迁移"];
    private string _mode = "查找替换", _find = "", _replacement = "", _message = "", _csvName = "尚未选择校对表", _note = "";
    private bool _wholeProject, _regex, _caseSensitive;
    private IReadOnlyList<ReviewExchangeRow>? _csv;
    private BulkTranslationRow? _selectedPreview;
    private MergeRowViewModel? _selectedMerge;
    private long _request;
    public TranslationToolsViewModel(TranslationWorkspaceViewModel workspace)
    {
        Workspace = workspace; _note = workspace.SelectedReviewNote;
        foreach (var term in workspace.Glossary) Terms.Add(new() { Source = term.Source, Preferred = term.Preferred, Forbidden = term.Forbidden, CaseSensitive = term.CaseSensitive });
        SaveTermsCommand = new AsyncRelayCommand(() => Guard(async () => { await workspace.SaveGlossaryAsync(Terms.Where(t => t.Source.Length > 0 || t.Preferred.Length > 0).Select(t => new GlossaryTerm(t.Source, t.Preferred, t.Forbidden, t.CaseSensitive))); Message = "术语已保存；质量检查刷新后生效。"; }));
        SaveNoteCommand = new AsyncRelayCommand(() => Guard(async () => { workspace.SetReview(SelectedReviewStatus, Note); if (!await workspace.SaveEditorialSettingsAsync()) throw new InvalidOperationException("校对记录保存失败，请重试。"); Message = "校对状态与备注已保存。"; }));
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        ApplyCommand = new AsyncRelayCommand(() => Guard(async () =>
        {
            var selected = PreviewRows.Where(r => r.IsSelected && r.CanApply).Select(r => r.Proposal).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("请勾选要应用的条目。");
            if (!workspace.ApplyEditorial(selected)) throw new InvalidOperationException("预览已过期或当前正在执行任务，请重新预览。");
            PreviewRows = []; OnPropertyChanged(nameof(PreviewRows)); SelectedPreview = null;
            if (!await workspace.SaveEditorialSettingsAsync()) throw new InvalidOperationException("译文已应用，校对备注尚未写入设置，请重试保存。");
            Message = $"已应用 {selected.Length} 条，可在翻译工作区撤销批量编辑。译文遵循原有保存设置。";
        }), () => !PreviewCommand.IsRunning);
        CompareCommand = new AsyncRelayCommand(token => Guard(async () =>
        {
            MergeRows = (await workspace.PreviewExternalAsync(token)).Select(row => new MergeRowViewModel(row)).ToArray();
            OnPropertyChanged(nameof(MergeRows)); SelectedMerge = MergeRows.FirstOrDefault();
            Message = $"发现 {MergeRows.Count} 条差异；双方修改需明确取舍。采用后仍需保存译文。";
        }));
        ApplyMergeCommand = new AsyncRelayCommand(token => Guard(async () =>
        {
            await workspace.ApplyExternalAsync(MergeRows.ToDictionary(row => row.Row.Key, row => row.Choice), token);
            MergeRows = []; OnPropertyChanged(nameof(MergeRows)); SelectedMerge = null;
            Message = "已采用对比结果并更新磁盘基线；尚未写入翻译文件，请关闭窗口后核对并保存。";
        }), () => !CompareCommand.IsRunning);
        CancelCommand = new RelayCommand(() => { PreviewCommand.Cancel(); CompareCommand.Cancel(); });
        PreviewCommand.PropertyChanged += (_, _) => ApplyCommand.NotifyCanExecuteChanged();
        CompareCommand.PropertyChanged += (_, _) => ApplyMergeCommand.NotifyCanExecuteChanged();
        workspace.PropertyChanged += WorkspaceChanged;
    }
    private void WorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Workspace.SelectedItem)) { _reviewSelection = null; Note = Workspace.SelectedReviewNote; OnPropertyChanged(nameof(SelectedReviewStatus)); }
    }
    public string Mode { get => _mode; set { if (SetProperty(ref _mode, value)) Invalidate(); } }
    public string Find { get => _find; set { if (SetProperty(ref _find, value)) Invalidate(); } }
    public string Replacement { get => _replacement; set { if (SetProperty(ref _replacement, value)) Invalidate(); } }
    public bool WholeProject { get => _wholeProject; set { if (SetProperty(ref _wholeProject, value)) Invalidate(); } }
    public bool Regex { get => _regex; set { if (SetProperty(ref _regex, value)) Invalidate(); } }
    public bool CaseSensitive { get => _caseSensitive; set { if (SetProperty(ref _caseSensitive, value)) Invalidate(); } }
    public string Note { get => _note; set => SetProperty(ref _note, value); }
    private ReviewStatus? _reviewSelection;
    public ReviewStatus SelectedReviewStatus
    {
        get => _reviewSelection ?? Workspace.SelectedReviewText switch { "已校对" => ReviewStatus.Reviewed, "需讨论" => ReviewStatus.Discussion, _ => ReviewStatus.Pending };
        set => SetProperty(ref _reviewSelection, value);
    }
    public IReadOnlyList<ReviewFilterOption> ReviewStatuses => Workspace.ReviewFilters.Where(option => option.Value is not null).ToArray();
    public string Message { get => _message; set => SetProperty(ref _message, value); }
    public string CsvName { get => _csvName; private set => SetProperty(ref _csvName, value); }
    public IReadOnlyList<BulkTranslationRow> PreviewRows { get; private set; } = [];
    public IReadOnlyList<MergeRowViewModel> MergeRows { get; private set; } = [];
    public BulkTranslationRow? SelectedPreview { get => _selectedPreview; set => SetProperty(ref _selectedPreview, value); }
    public MergeRowViewModel? SelectedMerge { get => _selectedMerge; set => SetProperty(ref _selectedMerge, value); }
    public IAsyncRelayCommand SaveTermsCommand { get; }
    public IAsyncRelayCommand SaveNoteCommand { get; }
    public IAsyncRelayCommand PreviewCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand CompareCommand { get; }
    public IAsyncRelayCommand ApplyMergeCommand { get; }
    public IRelayCommand CancelCommand { get; }
    private void Invalidate()
    {
        _request++; PreviewCommand?.Cancel(); PreviewRows = []; OnPropertyChanged(nameof(PreviewRows)); SelectedPreview = null;
    }
    public void LoadCsv(string name, string text) { var parsed = ReviewCsv.Read(text); Invalidate(); _csv = parsed; CsvName = name; Message = $"已载入 {parsed.Count} 行；回填核对原文与基线，迁移只提供人工候选。"; }
    private Task PreviewAsync(CancellationToken token) => Guard(async () =>
    {
        if (Mode != "查找替换" && _csv is null) throw new InvalidOperationException("请先选择校对表 CSV。");
        var request = ++_request; var migrate = Mode == "旧版译文迁移";
        var mode = Mode == "查找替换" ? "replace" : migrate ? "migrate" : "import";
        var rows = await Workspace.PreviewEditorialAsync(mode, Find, Replacement, new(TranslationSearchField.Translation, CaseSensitive, Regex), _csv, WholeProject, token);
        if (request != _request || token.IsCancellationRequested) return;
        PreviewRows = rows.Select(row => new BulkTranslationRow(row) { IsSelected = !migrate && row.CanApply }).ToArray();
        OnPropertyChanged(nameof(PreviewRows)); SelectedPreview = PreviewRows.FirstOrDefault(row => row.CanApply) ?? PreviewRows.FirstOrDefault();
        Message = $"可应用 {PreviewRows.Count(row => row.CanApply)} 项，跳过 {PreviewRows.Count(row => !row.CanApply)} 项。" + (migrate ? "迁移默认不勾选，每个目标最多选一种译法。" : "请核对原文和前后译文再应用。");
    });
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Message = "操作已取消。"; }
        catch (Exception ex) { Message = ex.Message; }
    }
    public void Dispose() { PreviewCommand.Cancel(); CompareCommand.Cancel(); Workspace.PropertyChanged -= WorkspaceChanged; }
}
