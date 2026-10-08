using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed class BulkTranslationRow(BulkTranslationProposal proposal) : ObservableObject
{
    private bool _selected = proposal.CanApply;
    public BulkTranslationProposal Proposal { get; } = proposal;
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value && Proposal.CanApply); }
    public bool CanApply => Proposal.CanApply;
    public string Original => Proposal.Target.Original ?? "复杂翻译块";
    public string Translation => Proposal.Translation ?? "";
    public string Before => Proposal.Target.Translation;
    public string Location => Proposal.Target.Impact;
    public string Source => Proposal.Source;
    public string Status => Proposal.SkipReason ?? "可填入";
}

public sealed class BulkTranslationViewModel : ObservableObject, IDisposable
{
    private readonly TranslationWorkspaceViewModel _workspace;
    private bool _entireProject;
    private BulkTranslationRow? _selectedRow;
    public BulkTranslationRow? SelectedRow { get => _selectedRow; set => SetProperty(ref _selectedRow, value); }
    private string _message = "只填空译文；多种译法、已有译文及冲突会跳过。";
    public BulkTranslationViewModel(TranslationWorkspaceViewModel workspace)
    {
        _workspace = workspace;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CancelCommand = new RelayCommand(() => RefreshCommand.Cancel());
        ApplyCommand = new RelayCommand(Apply, () => !RefreshCommand.IsRunning && Rows.Any(r => r.CanApply));
        RefreshCommand.PropertyChanged += (_, _) => ApplyCommand.NotifyCanExecuteChanged();
    }
    public bool EntireProject
    {
        get => _entireProject;
        set { if (!SetProperty(ref _entireProject, value)) return; RefreshCommand.Cancel(); Rows = []; OnPropertyChanged(nameof(Rows)); ApplyCommand.NotifyCanExecuteChanged(); Message = "范围已变化，请刷新预览。"; }
    }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public IReadOnlyList<BulkTranslationRow> Rows { get; private set; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    private async Task RefreshAsync(CancellationToken token)
    {
        try
        {
            var proposals = await _workspace.PreviewBulkAsync(EntireProject, token);
            Rows = proposals.Select(p => new BulkTranslationRow(p)).ToArray();
            SelectedRow = Rows.FirstOrDefault(r => r.CanApply) ?? Rows.FirstOrDefault();
            OnPropertyChanged(nameof(Rows));
            Message = $"可填入 {Rows.Count(r => r.CanApply):N0} 条，跳过 {Rows.Count(r => !r.CanApply):N0} 条。取消勾选可排除个别条目。";
        }
        catch (OperationCanceledException) { Message = "已取消预览。"; }
        catch (Exception ex) { Message = $"预览失败：{ex.Message}"; }
    }
    private void Apply()
    {
        var selected = Rows.Where(r => r.IsSelected && r.CanApply).Select(r => r.Proposal).ToArray();
        if (selected.Length == 0) { Message = "请至少勾选一条可填入的译文。"; return; }
        if (!_workspace.ApplyBulk(selected)) { Message = "译文、项目或任务状态已变化，请刷新预览后重试。"; return; }
        Message = $"已应用 {selected.Length:N0} 条译文。关闭窗口后可保存，或撤销上次批量复用。";
        Rows = [];
        OnPropertyChanged(nameof(Rows));
        ApplyCommand.NotifyCanExecuteChanged();
    }
    public void Dispose() => RefreshCommand.Cancel();
}
