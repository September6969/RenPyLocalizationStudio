using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.ViewModels;

public abstract class ProjectToolPanelViewModel(string id, string title, string description) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Description { get; } = description;
}

public enum UnrenOperationKind { ExtractRpa, DecompileRpyc }
public sealed record UnrenPlanRow(string Source, string Output, string Status, bool IsError);

public sealed class UnrenToolViewModel : ProjectToolPanelViewModel
{
    private UnrenOperationKind _operation;

    public UnrenToolViewModel(ArchiveWorkspaceViewModel workspace)
        : base("unren", "UnRen 工具", "安全处理 Ren’Py 归档和编译脚本；源文件始终保留。") => Workspace = workspace;

    public ArchiveWorkspaceViewModel Workspace { get; }
    public ObservableCollection<UnrenPlanRow> PlanItems { get; } = [];
    private string _planSummary = "尚未生成逐项计划。";
    public string PlanSummary { get => _planSummary; set => SetProperty(ref _planSummary, value); }
    public UnrenOperationKind Operation
    {
        get => _operation;
        private set
        {
            if (!SetProperty(ref _operation, value)) return;
            OnPropertyChanged(nameof(OperationTitle));
            OnPropertyChanged(nameof(OperationDescription));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(ActionCommand));
            OnPropertyChanged(nameof(SafetyDescription));
        }
    }
    public string OperationTitle => Operation == UnrenOperationKind.ExtractRpa ? "RPA 解包" : "RPYC 反编译";
    public string OperationDescription => Operation == UnrenOperationKind.ExtractRpa
        ? "安全检查并解包项目中的 RPA 归档，已有文件默认跳过。"
        : "将 RPYC 与 RPYMC 还原为可读脚本，已有 RPY 文件默认跳过。";
    public string ActionLabel => Operation == UnrenOperationKind.ExtractRpa ? "生成解包计划" : "生成反编译计划";
    public IAsyncRelayCommand ActionCommand => Operation == UnrenOperationKind.ExtractRpa ? Workspace.ExtractCommand : Workspace.DecompileCommand;
    public string SafetyDescription => Operation == UnrenOperationKind.ExtractRpa
        ? "归档条目会先执行路径越界检查；不会运行游戏 EXE，不会删除源 RPA，也不会覆盖已有文件。"
        : "反编译只读取 RPYC/RPYMC；不会运行游戏脚本，不会删除编译文件，也不会覆盖已有 RPY。";

    public void SelectOperation(UnrenOperationKind operation)
    {
        Operation = operation;
        PlanItems.Clear();
        PlanSummary = "尚未生成逐项计划。";
    }
}

public sealed record PrefixRenameRow(string Source, string Target, string Status, bool HasConflict);

public sealed class PrefixRenameToolViewModel : ProjectToolPanelViewModel
{
    private readonly ProjectSessionViewModel _session;
    private readonly IFileSystemService _fileSystem;
    private readonly IPrefixRenameService _service;
    private readonly IConfirmationService _confirmation;
    private string _relativeDirectory = "game";
    private string _prefix = "x-";
    private bool _recursive = true;
    private PrefixRenamePlan? _plan;
    private string _summary = "填写项目内目录和前缀，然后生成预览。";

    public PrefixRenameToolViewModel(ProjectSessionViewModel session, IFileSystemService fileSystem, IPrefixRenameService service, IConfirmationService confirmation)
        : base("rename", "批量移除文件名前缀", "兼容“去除 x- 前缀”工具；只改文件名，不修改目录和文件内容。")
    {
        _session = session;
        _fileSystem = fileSystem;
        _service = service;
        _confirmation = confirmation;
        PlanCommand = new AsyncRelayCommand(PlanAsync);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => _plan?.ReadyCount > 0);
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    public ObservableCollection<PrefixRenameRow> Items { get; } = [];
    public IAsyncRelayCommand PlanCommand { get; }
    public IAsyncRelayCommand ExecuteCommand { get; }
    public string RelativeDirectory { get => _relativeDirectory; set { if (SetProperty(ref _relativeDirectory, value)) InvalidatePlan(); } }
    public string Prefix { get => _prefix; set { if (SetProperty(ref _prefix, value)) InvalidatePlan(); } }
    public bool Recursive { get => _recursive; set { if (SetProperty(ref _recursive, value)) InvalidatePlan(); } }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ProjectSessionViewModel.ProjectPath) or nameof(ProjectSessionViewModel.Language))
            InvalidatePlan();
    }

    private async Task PlanAsync()
    {
        var root = _fileSystem.ValidateProjectRoot(_session.ProjectPath);
        if (!root.IsSuccess || root.Value is null) { _confirmation.ShowDiagnostics("项目路径无效", root.Diagnostics); return; }
        await _session.Tasks.RunAsync("正在生成重命名预览……", async token =>
        {
            var projectPath = _session.ProjectPath;
            var relativeDirectory = ProjectLayout.ResolveProjectRelativePath(root.Value, RelativeDirectory);
            var result = await _service.PlanAsync(new PrefixRenamePlanRequest(root.Value, relativeDirectory, Prefix, Recursive),
                _session.Tasks.CreateProgress(), token);
            if (!result.IsSuccess || result.Value is null)
            {
                if (result.Status == OperationStatus.Cancelled) _session.Tasks.StatusMessage = "重命名预览已取消。";
                else _confirmation.ShowDiagnostics("重命名预览失败", result.Diagnostics);
                return;
            }
            if (!string.Equals(projectPath, _session.ProjectPath, StringComparison.OrdinalIgnoreCase)) return;
            _plan = result.Value;
            Items.Clear();
            foreach (var item in _plan.Items)
                Items.Add(new PrefixRenameRow(item.SourceRelativePath, item.TargetRelativePath, item.HasConflict ? "冲突" : "就绪", item.HasConflict));
            Summary = $"可重命名 {_plan.ReadyCount:N0} 项，冲突 {_plan.ConflictCount:N0} 项。";
            ExecuteCommand.NotifyCanExecuteChanged();
            _session.Tasks.StatusMessage = _plan.Items.Count == 0 ? "没有找到匹配此前缀的文件。" : "重命名预览已生成，尚未修改文件。";
        });
    }

    private async Task ExecuteAsync()
    {
        if (_plan is null || _plan.ReadyCount == 0) return;
        if (!_confirmation.Confirm("执行批量重命名", $"将移除 {_plan.ReadyCount:N0} 个文件名的“{_plan.Request.Prefix}”前缀。该操作会改变游戏资源路径，确认继续？", MessageBoxImage.Warning)) return;
        await _session.Tasks.RunAsync("正在批量重命名……", async token =>
        {
            var result = await _service.ExecuteAsync(new PrefixRenameExecutionRequest(_plan), _session.Tasks.CreateProgress(), token);
            if (!result.IsSuccess || result.Value is null)
            {
                if (result.Status == OperationStatus.Cancelled) _session.Tasks.StatusMessage = "批量重命名已取消。";
                else _confirmation.ShowDiagnostics("批量重命名失败", result.Diagnostics);
                return;
            }
            Summary = $"已重命名 {result.Value.RenamedFiles:N0} 项，跳过 {result.Value.SkippedFiles:N0} 项。";
            _session.Tasks.StatusMessage = Summary;
            InvalidatePlan(false);
        });
    }

    private void InvalidatePlan(bool clearRows = true)
    {
        _plan = null;
        if (clearRows) { Items.Clear(); Summary = "选项已改变，请重新生成预览。"; }
        ExecuteCommand.NotifyCanExecuteChanged();
    }
}

public sealed record ImageCompressionRow(
    string Source,
    string Target,
    string OriginalSize,
    string CompressedSize,
    string Saving,
    string Status,
    ImageCompressionDisposition Disposition);

public sealed class ImageCompressionToolViewModel : ProjectToolPanelViewModel
{
    private readonly ProjectSessionViewModel _session;
    private readonly IFileSystemService _fileSystem;
    private readonly IImageCompressionService _service;
    private readonly IConfirmationService _confirmation;
    private string _relativeDirectory = "game/images";
    private string _outputDirectory = "game/rls-compressed";
    private int _quality = 85;
    private double _minimumSavingPercent = 1;
    private bool _replaceOriginals;
    private bool _recursive = true;
    private ImageCompressionPlan? _plan;
    private string _summary = "默认输出到独立目录，不修改原图。";

    public ImageCompressionToolViewModel(ProjectSessionViewModel session, IFileSystemService fileSystem, IImageCompressionService service, IConfirmationService confirmation)
        : base("image", "YAC 图片压缩", "本地压缩 PNG、JPEG 与静态 WebP；保持原扩展名，动画图片自动跳过。")
    {
        _session = session;
        _fileSystem = fileSystem;
        _service = service;
        _confirmation = confirmation;
        PlanCommand = new AsyncRelayCommand(PlanAsync);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => _plan?.ReadyCount > 0);
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    public ObservableCollection<ImageCompressionRow> Items { get; } = [];
    public IAsyncRelayCommand PlanCommand { get; }
    public IAsyncRelayCommand ExecuteCommand { get; }
    public string RelativeDirectory { get => _relativeDirectory; set { if (SetProperty(ref _relativeDirectory, value)) InvalidatePlan(); } }
    public string OutputDirectory { get => _outputDirectory; set { if (SetProperty(ref _outputDirectory, value)) InvalidatePlan(); } }
    public int Quality { get => _quality; set { if (SetProperty(ref _quality, value)) InvalidatePlan(); } }
    public double MinimumSavingPercent { get => _minimumSavingPercent; set { if (SetProperty(ref _minimumSavingPercent, value)) InvalidatePlan(); } }
    public bool ReplaceOriginals { get => _replaceOriginals; set { if (SetProperty(ref _replaceOriginals, value)) InvalidatePlan(); } }
    public bool Recursive { get => _recursive; set { if (SetProperty(ref _recursive, value)) InvalidatePlan(); } }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ProjectSessionViewModel.ProjectPath) or nameof(ProjectSessionViewModel.Language))
            InvalidatePlan();
    }

    private async Task PlanAsync()
    {
        var root = _fileSystem.ValidateProjectRoot(_session.ProjectPath);
        if (!root.IsSuccess || root.Value is null) { _confirmation.ShowDiagnostics("项目路径无效", root.Diagnostics); return; }
        await _session.Tasks.RunAsync("正在分析图片与压缩收益……", async token =>
        {
            var projectPath = _session.ProjectPath;
            var relativeDirectory = ProjectLayout.ResolveProjectRelativePath(root.Value, RelativeDirectory);
            var outputDirectory = ProjectLayout.ResolveProjectRelativePath(root.Value, OutputDirectory);
            var result = await _service.PlanAsync(new ImageCompressionPlanRequest(root.Value, relativeDirectory, Quality,
                MinimumSavingPercent, ReplaceOriginals, outputDirectory, Recursive), _session.Tasks.CreateProgress(), token);
            if (!result.IsSuccess || result.Value is null)
            {
                if (result.Status == OperationStatus.Cancelled) _session.Tasks.StatusMessage = "图片压缩预览已取消。";
                else _confirmation.ShowDiagnostics("图片压缩预览失败", result.Diagnostics);
                return;
            }
            if (!string.Equals(projectPath, _session.ProjectPath, StringComparison.OrdinalIgnoreCase)) return;
            _plan = result.Value;
            Items.Clear();
            foreach (var item in _plan.Items)
                Items.Add(new ImageCompressionRow(item.SourceRelativePath, item.TargetRelativePath, FormatBytes(item.OriginalBytes),
                    FormatBytes(item.CompressedBytes), $"{item.SavingPercent:F1}%", item.Message, item.Disposition));
            Summary = $"可压缩 {_plan.ReadyCount:N0} 项，预计节省 {FormatBytes(_plan.TotalSavedBytes)}。";
            ExecuteCommand.NotifyCanExecuteChanged();
            _session.Tasks.StatusMessage = "图片压缩预览已生成，尚未写入文件。";
        });
    }

    private async Task ExecuteAsync()
    {
        if (_plan is null || _plan.ReadyCount == 0) return;
        var destination = _plan.Request.ReplaceOriginals ? "原图将被替换，并为每个文件创建 .rls.bak 备份。" : $"输出到 {_plan.Request.OutputDirectory}，原图不变。";
        if (!_confirmation.Confirm("执行图片压缩", $"将压缩 {_plan.ReadyCount:N0} 张图片。{destination}\n确认继续？", MessageBoxImage.Warning)) return;
        await _session.Tasks.RunAsync("正在压缩图片……", async token =>
        {
            var result = await _service.ExecuteAsync(new ImageCompressionExecutionRequest(_plan), _session.Tasks.CreateProgress(), token);
            if (!result.IsSuccess || result.Value is null)
            {
                if (result.Status == OperationStatus.Cancelled) _session.Tasks.StatusMessage = "图片压缩已取消。";
                else _confirmation.ShowDiagnostics("图片压缩失败", result.Diagnostics);
                return;
            }
            Summary = $"已压缩 {result.Value.CompressedFiles:N0} 项，跳过 {result.Value.SkippedFiles:N0} 项，节省 {FormatBytes(result.Value.SavedBytes)}。";
            _session.Tasks.StatusMessage = Summary;
            InvalidatePlan(false);
        });
    }

    private void InvalidatePlan(bool clearRows = true)
    {
        _plan = null;
        if (clearRows) { Items.Clear(); Summary = "选项已改变，请重新生成预览。"; }
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MiB",
        >= 1L << 10 => $"{bytes / 1024d:F1} KiB",
        _ => $"{bytes} B"
    };
}
