using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed record SidebarOption(string Id, string Title, string Glyph = "");

public sealed class SimpleSidebarViewModel : WorkspaceSidebarViewModelBase
{
    private SidebarOption? _selectedItem;
    private string _searchText = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public ObservableCollection<SidebarOption> Items { get; } = [];
    public SidebarOption? SelectedItem { get => _selectedItem; set => SetProperty(ref _selectedItem, value); }
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
}

public class ToolMainContentViewModel : WorkspaceMainContentViewModelBase
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public ObservableCollection<ToolLogEntry> Logs { get; } = [];
}

public sealed class TlMainContentViewModel : ToolMainContentViewModel
{
    public TlMainContentViewModel(TlWorkspaceViewModel workspace) => Workspace = workspace;
    public TlWorkspaceViewModel Workspace { get; }
}

public sealed class ArchiveMainContentViewModel : ToolMainContentViewModel
{
    public ArchiveMainContentViewModel(ArchiveWorkspaceViewModel workspace) => Workspace = workspace;
    public ArchiveWorkspaceViewModel Workspace { get; }
    private ProjectToolPanelViewModel? _currentTool;
    public ProjectToolPanelViewModel? CurrentTool { get => _currentTool; set => SetProperty(ref _currentTool, value); }
}

public sealed class TlWorkspaceViewModel : WorkspaceViewModelBase
{
    private readonly ProjectSessionViewModel _session;
    private readonly IRenPySdkService _sdk;
    private readonly IConfirmationService _confirmation;
    private readonly IFileDialogService _fileDialog;
    private bool _empty = true;
    private bool _stringsOnly;
    private bool _noTodo = true;
    private string _sdkSummary = "尚未检测 SDK。";
    private string _targetLanguage = string.Empty;

    public TlWorkspaceViewModel(ProjectSessionViewModel session, IRenPySdkService sdk, IConfirmationService confirmation, IFileDialogService fileDialog)
        : base("tl", "生成 TL", "\uE943")
    {
        _session = session;
        _sdk = sdk;
        _confirmation = confirmation;
        _fileDialog = fileDialog;
        Sidebar = new SimpleSidebarViewModel { Title = "TL 生成", Subtitle = "官方 SDK 提取与增量更新" };
        Sidebar.Items.Add(new SidebarOption("options", "生成选项", "\uE713"));
        Sidebar.Items.Add(new SidebarOption("history", "执行日志", "\uE81C"));
        Sidebar.SelectedItem = Sidebar.Items[0];
        Main = new TlMainContentViewModel(this) { Title = "生成翻译文件", Description = "沿用 Ren'Py Launcher 的语言输入和生成选项，并在执行前展示完整计划。" };
        PreflightCommand = new AsyncRelayCommand(() => RunAsync(true));
        GenerateCommand = new AsyncRelayCommand(() => RunAsync(false));
        ClearLogsCommand = new RelayCommand(Main.Logs.Clear, () => Main.Logs.Count > 0);
        ChooseSdkCommand = new AsyncRelayCommand(ChooseSdkAsync);
        ClearSdkCommand = new AsyncRelayCommand(ClearSdkAsync, () => !string.IsNullOrWhiteSpace(_session.SdkPath));
        Main.Logs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasLogs));
            OnPropertyChanged(nameof(LogCountText));
            ClearLogsCommand.NotifyCanExecuteChanged();
        };
        Sidebar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SimpleSidebarViewModel.SelectedItem)) return;
            OnPropertyChanged(nameof(IsOptionsPage));
            OnPropertyChanged(nameof(IsHistoryPage));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageDescription));
        };
        _session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ProjectSessionViewModel.Language) or nameof(ProjectSessionViewModel.ProjectPath))
            {
                OnPropertyChanged(nameof(TargetLanguage));
                OnPropertyChanged(nameof(IsLanguageValid));
                OnPropertyChanged(nameof(LanguageHint));
                NotifyPreview();
            }
            if (args.PropertyName == nameof(ProjectSessionViewModel.SdkPath))
            {
                OnPropertyChanged(nameof(SdkPathDisplay));
                ClearSdkCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public SimpleSidebarViewModel Sidebar { get; }
    public TlMainContentViewModel Main { get; }
    public override WorkspaceSidebarViewModelBase SidebarContent => Sidebar;
    public override WorkspaceMainContentViewModelBase MainContent => Main;
    public IAsyncRelayCommand PreflightCommand { get; }
    public IAsyncRelayCommand GenerateCommand { get; }
    public IRelayCommand ClearLogsCommand { get; }
    public IAsyncRelayCommand ChooseSdkCommand { get; }
    public IAsyncRelayCommand ClearSdkCommand { get; }
    public bool IsOptionsPage => Sidebar.SelectedItem?.Id != "history";
    public bool IsHistoryPage => !IsOptionsPage;
    public bool HasLogs => Main.Logs.Count > 0;
    public string LogCountText => $"{Main.Logs.Count:N0} 条记录";
    public string PageTitle => IsHistoryPage ? "执行日志" : "生成翻译文件";
    public string PageDescription => IsHistoryPage ? "查看 SDK 命令、缺失统计、标准输出与错误信息。" : "沿用 Ren'Py Launcher 的语言输入和生成选项，并在执行前展示完整计划。";
    public bool Empty { get => _empty; set { if (SetProperty(ref _empty, value)) NotifyPreview(); } }
    public bool StringsOnly { get => _stringsOnly; set { if (SetProperty(ref _stringsOnly, value)) NotifyPreview(); } }
    public bool NoTodo { get => _noTodo; set { if (SetProperty(ref _noTodo, value)) NotifyPreview(); } }
    public string SdkSummary { get => _sdkSummary; private set => SetProperty(ref _sdkSummary, value); }
    public string SdkPathDisplay => string.IsNullOrWhiteSpace(_session.SdkPath) ? "自动检测（未指定）" : _session.SdkPath;
    public string TargetLanguage
    {
        get => string.IsNullOrWhiteSpace(_targetLanguage) ? _session.Language : _targetLanguage;
        set
        {
            if (!SetProperty(ref _targetLanguage, value)) return;
            OnPropertyChanged(nameof(IsLanguageValid));
            OnPropertyChanged(nameof(LanguageHint));
            NotifyPreview();
        }
    }
    public bool IsLanguageValid => Regex.IsMatch(TargetLanguage.Trim(), "^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
    public string LanguageHint => IsLanguageValid
        ? $"将生成或更新 game/tl/{TargetLanguage.Trim()}/"
        : "语言名必须以小写字母开头，只能包含小写 ASCII、数字和下划线。";
    public string CommandPreview => $"translate {TargetLanguage.Trim()}" +
        (Empty ? " --empty" : string.Empty) +
        (StringsOnly ? " --strings-only" : string.Empty) +
        (NoTodo ? " --no-todo" : string.Empty);
    public ProjectSessionViewModel Session => _session;

    private async Task RunAsync(bool countOnly)
    {
        var language = TargetLanguage.Trim();
        if (string.IsNullOrWhiteSpace(_session.ProjectPath) || !IsLanguageValid)
        {
            _confirmation.ShowMessage("无法执行", string.IsNullOrWhiteSpace(_session.ProjectPath) ? "请先选择 Ren’Py 项目。" : LanguageHint, MessageBoxImage.Warning);
            return;
        }
        if (!countOnly && !_confirmation.Confirm("生成 TL", $"将使用 Ren’Py SDK 更新 {language} 翻译文件，确认继续？")) return;

        await _session.Tasks.RunAsync(countOnly ? "正在预检缺失翻译……" : "正在生成 TL……", async token =>
        {
            var preferredPath = !string.IsNullOrWhiteSpace(_session.SdkPath) ? Path.GetDirectoryName(_session.SdkPath) : null;
            var discovery = await _sdk.DiscoverAsync(new SdkDiscoveryRequest(preferredPath), _session.Tasks.CreateProgress(), token);
            var installation = discovery.Value?.FirstOrDefault();
            if (installation is null)
            {
                _confirmation.ShowDiagnostics("SDK 不可用", discovery.Diagnostics.Append(new Diagnostic(DiagnosticSeverity.Error, "SDK_NOT_FOUND", "未检测到 Ren’Py SDK。")));
                return;
            }
            SdkSummary = $"{installation.DisplayVersion} · {installation.ExecutablePath}";
            Main.Logs.Add(new ToolLogEntry($"> {installation.ExecutablePath} {_session.ProjectPath} {CommandPreview}"));
            var result = await _sdk.ExecuteAsync(new SdkTranslationRequest(
                _session.ProjectPath, language, installation, countOnly, Empty, StringsOnly, NoTodo),
                _session.Tasks.CreateProgress(), token);
            foreach (var line in result.Value?.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? []) Main.Logs.Add(new ToolLogEntry(line.Trim()));
            if (!result.IsSuccess)
            {
                _confirmation.ShowDiagnostics("SDK 操作失败", result.Diagnostics);
                return;
            }
            _session.Tasks.StatusMessage = countOnly ? "TL 缺失数量预检完成。" : "TL 已生成，正在重新分析项目。";
            if (!countOnly)
            {
                await _session.LoadLanguagesAsync(language, token);
                await _session.ReloadAsync(token);
            }
        });
    }

    private void NotifyPreview()
    {
        OnPropertyChanged(nameof(CommandPreview));
        OnPropertyChanged(nameof(LanguageHint));
    }

    private async Task ChooseSdkAsync(CancellationToken cancellationToken)
    {
        var path = _fileDialog.SelectSdkExecutable(_session.SdkPath);
        if (path is null) return;
        _session.SdkPath = path;
        await _session.PersistSettingsAsync(cancellationToken);
    }

    private async Task ClearSdkAsync(CancellationToken cancellationToken)
    {
        _session.SdkPath = string.Empty;
        await _session.PersistSettingsAsync(cancellationToken);
    }
}

public sealed class ExtraTextRowViewModel : ObservableObject
{
    private bool _isSelected;
    private string _translation = string.Empty;
    public ExtraTextRowViewModel(ExtraTextCandidate candidate) => Candidate = candidate;
    public ExtraTextCandidate Candidate { get; }
    public string Id => Candidate.Id;
    public string Text => Candidate.Text;
    public string Location => $"{Candidate.RelativePath}:{Candidate.Line}";
    public string Kind => Candidate.Kind.ToString();
    public string Confidence => Candidate.Confidence.ToString();
    public bool AlreadyTranslated => Candidate.AlreadyTranslated;
    public bool CanSelect => !AlreadyTranslated;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string Translation { get => _translation; set => SetProperty(ref _translation, value); }
}

public sealed class ExtraTextMainContentViewModel : WorkspaceMainContentViewModelBase
{
    public ExtraTextMainContentViewModel(ExtraTextWorkspaceViewModel workspace) => Workspace = workspace;
    public ExtraTextWorkspaceViewModel Workspace { get; }
}

public sealed class ExtraTextInspectorViewModel : WorkspaceInspectorViewModelBase
{
    private ExtraTextRowViewModel? _selected;
    public ExtraTextRowViewModel? Selected { get => _selected; set => SetProperty(ref _selected, value); }
}

public sealed class ExtraTextWorkspaceViewModel : WorkspaceViewModelBase
{
    private readonly ProjectSessionViewModel _session;
    private readonly IFileSystemService _fileSystem;
    private readonly IExtraTextScanService _scanner;
    private readonly IManagedPatchService _patch;
    private readonly IConfirmationService _confirmation;
    private readonly List<ExtraTextRowViewModel> _allCandidates = [];
    private ExtraTextRowViewModel? _selectedItem;

    public ExtraTextWorkspaceViewModel(ProjectSessionViewModel session, IFileSystemService fileSystem, IExtraTextScanService scanner, IManagedPatchService patch, IConfirmationService confirmation)
        : base("extra", "额外文本", "\uE8D2")
    {
        _session = session;
        _fileSystem = fileSystem;
        _scanner = scanner;
        _patch = patch;
        _confirmation = confirmation;
        Sidebar = new SimpleSidebarViewModel { Title = "额外文本", Subtitle = "静态字符串候选与人工确认" };
        Sidebar.Items.Add(new SidebarOption("all", "全部候选"));
        foreach (var kind in Enum.GetNames<ExtraTextKind>()) Sidebar.Items.Add(new SidebarOption(kind, kind));
        Sidebar.SelectedItem = Sidebar.Items[0];
        Main = new ExtraTextMainContentViewModel(this);
        Inspector = new ExtraTextInspectorViewModel();
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        WriteSelectedCommand = new AsyncRelayCommand(WriteSelectedAsync);
        SelectAllVisibleCommand = new RelayCommand(SelectAllVisible);
        UnselectAllVisibleCommand = new RelayCommand(UnselectAllVisible);

        Sidebar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SimpleSidebarViewModel.SelectedItem) or nameof(SimpleSidebarViewModel.SearchText))
            {
                RebuildVisibleCandidates();
            }
        };
    }

    public SimpleSidebarViewModel Sidebar { get; }
    public ExtraTextMainContentViewModel Main { get; }
    public ExtraTextInspectorViewModel Inspector { get; }
    public override WorkspaceSidebarViewModelBase SidebarContent => Sidebar;
    public override WorkspaceMainContentViewModelBase MainContent => Main;
    public override WorkspaceInspectorViewModelBase InspectorContent => Inspector;
    public ObservableCollection<ExtraTextRowViewModel> Candidates => VisibleCandidates;
    public ObservableCollection<ExtraTextRowViewModel> VisibleCandidates { get; } = [];
    public IReadOnlyList<ExtraTextRowViewModel> AllCandidates => _allCandidates;
    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand WriteSelectedCommand { get; }
    public IRelayCommand SelectAllVisibleCommand { get; }
    public IRelayCommand UnselectAllVisibleCommand { get; }

    public ExtraTextRowViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value)) Inspector.Selected = value;
        }
    }

    public string CandidateCountText => _allCandidates.Count > 0
        ? $"{VisibleCandidates.Count:N0} / {_allCandidates.Count:N0} 项"
        : "尚未扫描";

    public void RebuildVisibleCandidates()
    {
        VisibleCandidates.Clear();
        var selectedKind = Sidebar.SelectedItem?.Id;
        var search = Sidebar.SearchText?.Trim() ?? string.Empty;

        var query = _allCandidates.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(selectedKind) && !selectedKind.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => x.Kind.Equals(selectedKind, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x => x.Text.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                     x.Location.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                     x.Kind.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var candidate in query)
        {
            VisibleCandidates.Add(candidate);
        }

        SelectedItem = VisibleCandidates.FirstOrDefault();
        OnPropertyChanged(nameof(CandidateCountText));
    }

    private void SelectAllVisible()
    {
        foreach (var candidate in VisibleCandidates)
        {
            if (candidate.CanSelect) candidate.IsSelected = true;
        }
    }

    private void UnselectAllVisible()
    {
        foreach (var candidate in VisibleCandidates)
        {
            candidate.IsSelected = false;
        }
    }

    private async Task ScanAsync()
    {
        var root = _fileSystem.ValidateProjectRoot(_session.ProjectPath);
        if (!root.IsSuccess || root.Value is null)
        {
            _confirmation.ShowDiagnostics("项目路径无效", root.Diagnostics);
            return;
        }
        await _session.Tasks.RunAsync("正在扫描额外文本……", async token =>
        {
            var result = await _scanner.ExecuteAsync(new ExtraTextScanRequest(root.Value, _session.Language), _session.Tasks.CreateProgress(), token);
            if (!result.IsSuccess || result.Value is null)
            {
                _confirmation.ShowDiagnostics("扫描失败", result.Diagnostics);
                return;
            }
            _allCandidates.Clear();
            foreach (var candidate in result.Value.Candidates) _allCandidates.Add(new ExtraTextRowViewModel(candidate));
            Badge = _allCandidates.Count(x => !x.AlreadyTranslated).ToString();
            RebuildVisibleCandidates();
            _session.Tasks.StatusMessage = $"扫描完成：发现 {_allCandidates.Count:N0} 个候选。";
        });
    }

    private async Task WriteSelectedAsync()
    {
        var selected = _allCandidates.Where(x => x.IsSelected && !x.AlreadyTranslated).ToArray();
        if (selected.Length == 0)
        {
            _session.Tasks.StatusMessage = "请勾选至少一个未写入候选。";
            return;
        }
        if (!_confirmation.Confirm("写入额外文本", $"将 {selected.Length} 条候选写入受管补丁，确认继续？")) return;
        var root = _fileSystem.ValidateProjectRoot(_session.ProjectPath);
        if (!root.IsSuccess || root.Value is null) { _confirmation.ShowDiagnostics("项目路径无效", root.Diagnostics); return; }
        await _session.Tasks.RunAsync("正在写入额外文本补丁……", async token =>
        {
            var relative = $"game/tl/{_session.Language}/rls_extra_strings.rpy";
            var module = _patch.CreateExtraStringsModule(_session.Language, selected.Select(x => (x.Text, x.Translation)));
            var request = new ManagedPatchRequest(root.Value, relative, [module]);
            var result = await _patch.ExecuteAsync(new ManagedPatchWriteRequest(request, true), _session.Tasks.CreateProgress(), token);
            if (!result.IsSuccess) _confirmation.ShowDiagnostics("额外文本写入失败", result.Diagnostics);
            else _session.Tasks.StatusMessage = $"已写入 {selected.Length} 条额外文本。";
        });
    }
}

public sealed class DiagnosticInspectorViewModel : WorkspaceInspectorViewModelBase
{
    private Diagnostic? _diagnostic;
    public Diagnostic? Diagnostic { get => _diagnostic; set => SetProperty(ref _diagnostic, value); }
}

public sealed class DiagnosticsMainContentViewModel : WorkspaceMainContentViewModelBase
{
    public DiagnosticsMainContentViewModel(DiagnosticsWorkspaceViewModel workspace) => Workspace = workspace;
    public DiagnosticsWorkspaceViewModel Workspace { get; }
}

public sealed class DiagnosticsWorkspaceViewModel : WorkspaceViewModelBase
{
    private readonly ProjectSessionViewModel _session;
    private readonly IMessenger _messenger;
    private ContentItem? _selectedItem;

    public DiagnosticsWorkspaceViewModel(ProjectSessionViewModel session, IMessenger messenger)
        : base("diagnostics", "诊断", "\uE814")
    {
        _session = session;
        _messenger = messenger;
        Sidebar = new SimpleSidebarViewModel { Title = "诊断", Subtitle = "解析、绑定、文件与工具问题" };
        foreach (var severity in Enum.GetNames<DiagnosticSeverity>()) Sidebar.Items.Add(new SidebarOption(severity, severity));
        Main = new DiagnosticsMainContentViewModel(this);
        Inspector = new DiagnosticInspectorViewModel();
        OpenSourceCommand = new RelayCommand(OpenSource, () => SelectedItem?.Diagnostic is not null);
        session.SnapshotChanged += (_, _) => RefreshItems();
    }

    public SimpleSidebarViewModel Sidebar { get; }
    public DiagnosticsMainContentViewModel Main { get; }
    public DiagnosticInspectorViewModel Inspector { get; }
    public override WorkspaceSidebarViewModelBase SidebarContent => Sidebar;
    public override WorkspaceMainContentViewModelBase MainContent => Main;
    public override WorkspaceInspectorViewModelBase InspectorContent => Inspector;
    public ObservableCollection<ContentItem> Items { get; } = [];
    public IRelayCommand OpenSourceCommand { get; }
    public ContentItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            Inspector.Diagnostic = value?.Diagnostic;
            OpenSourceCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshItems()
    {
        Items.Clear();
        foreach (var diagnostic in _session.Snapshot?.Diagnostics ?? []) Items.Add(ContentItem.FromDiagnostic(diagnostic));
        Badge = Items.Count(x => x.Diagnostic?.Severity == DiagnosticSeverity.Error).ToString();
    }

    private void OpenSource()
    {
        var diagnostic = SelectedItem?.Diagnostic;
        if (diagnostic is null) return;
        _messenger.Send(new NavigateToSourceRequestMessage(new NavigateToSourceRequest(
            null, diagnostic.RelativePath, diagnostic.Line, null, FocusTarget: NavigationFocusTarget.Inspector)));
    }
}

public sealed class ArchiveWorkspaceViewModel : WorkspaceViewModelBase
{
    private readonly ProjectSessionViewModel _session;
    private readonly IFileSystemService _fileSystem;
    private readonly IArchiveExtractionService _archive;
    private readonly IScriptDecompilerService _decompiler;
    private readonly IConfirmationService _confirmation;

    public ArchiveWorkspaceViewModel(ProjectSessionViewModel session, IFileSystemService fileSystem, IArchiveExtractionService archive,
        IScriptDecompilerService decompiler, IPrefixRenameService rename, IImageCompressionService imageCompression,
        IConfirmationService confirmation)
        : base("archive", "项目工具", "\uE7B8")
    {
        _session = session;
        _fileSystem = fileSystem;
        _archive = archive;
        _decompiler = decompiler;
        _confirmation = confirmation;
        Sidebar = new SimpleSidebarViewModel { Title = "项目工具", Subtitle = "解包、反编译、重命名与图片优化" };
        Sidebar.Items.Add(new SidebarOption("rpa", "RPA 解包", "\uE7B8"));
        Sidebar.Items.Add(new SidebarOption("rpyc", "RPYC 反编译", "\uE8A5"));
        Sidebar.Items.Add(new SidebarOption("rename", "移除文件名前缀", "\uE8AC"));
        Sidebar.Items.Add(new SidebarOption("image", "YAC 图片压缩", "\uEB9F"));
        Sidebar.SelectedItem = Sidebar.Items[0];
        Main = new ArchiveMainContentViewModel(this) { Title = "项目工具", Description = "全部操作先预览，写入限制在项目目录内。" };
        ExtractCommand = new AsyncRelayCommand(ExtractAsync);
        DecompileCommand = new AsyncRelayCommand(DecompileAsync);
        UnrenTool = new UnrenToolViewModel(this);
        UnrenTool.SelectOperation(UnrenOperationKind.ExtractRpa);
        RenameTool = new PrefixRenameToolViewModel(session, fileSystem, rename, confirmation);
        ImageTool = new ImageCompressionToolViewModel(session, fileSystem, imageCompression, confirmation);
        Main.CurrentTool = UnrenTool;
        Sidebar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SimpleSidebarViewModel.SelectedItem)) return;
            if (Sidebar.SelectedItem?.Id == "rpa") UnrenTool.SelectOperation(UnrenOperationKind.ExtractRpa);
            else if (Sidebar.SelectedItem?.Id == "rpyc") UnrenTool.SelectOperation(UnrenOperationKind.DecompileRpyc);
            Main.CurrentTool = Sidebar.SelectedItem?.Id switch
            {
                "rename" => RenameTool,
                "image" => ImageTool,
                _ => UnrenTool
            };
        };
    }

    public SimpleSidebarViewModel Sidebar { get; }
    public ArchiveMainContentViewModel Main { get; }
    public UnrenToolViewModel UnrenTool { get; }
    public PrefixRenameToolViewModel RenameTool { get; }
    public ImageCompressionToolViewModel ImageTool { get; }
    public override WorkspaceSidebarViewModelBase SidebarContent => Sidebar;
    public override WorkspaceMainContentViewModelBase MainContent => Main;
    public IAsyncRelayCommand ExtractCommand { get; }
    public IAsyncRelayCommand DecompileCommand { get; }

    private async Task ExtractAsync()
    {
        var runtime = ResolveRuntime();
        var root = _fileSystem.ValidateProjectRoot(_session.ProjectPath);
        if (runtime is null || !root.IsSuccess || root.Value is null) { if (!root.IsSuccess) _confirmation.ShowDiagnostics("项目路径无效", root.Diagnostics); return; }
        await _session.Tasks.RunAsync("正在安全解包 RPA……", async token =>
        {
            var files = await _fileSystem.EnumerateFilesAsync(root.Value, "game", "*.rpa", token);
            var archives = files.Value?.Select(x => x.RelativePath).ToArray() ?? [];
            if (archives.Length == 0) { _session.Tasks.StatusMessage = "项目中未找到 RPA。"; return; }
            if (!_confirmation.Confirm("RPA 解包计划", $"将安全检查并解包 {archives.Length} 个 RPA；已有文件跳过，源 RPA 永不删除。确认继续？", MessageBoxImage.Warning)) return;
            var result = await _archive.ExecuteAsync(new ArchiveExtractionRequest(root.Value, new ValidatedToolPath(runtime.Value.Python), new ValidatedToolPath(runtime.Value.RpaTool), archives, "game", true), _session.Tasks.CreateProgress(), token);
            if (!result.IsSuccess) _confirmation.ShowDiagnostics("RPA 解包未完全成功", result.Diagnostics);
            else _session.Tasks.StatusMessage = $"已处理 {result.Value!.ProcessedArchives} 个 RPA；源文件全部保留。";
        });
    }

    private async Task DecompileAsync()
    {
        var runtime = ResolveRuntime();
        var root = _fileSystem.ValidateProjectRoot(_session.ProjectPath);
        if (runtime is null || !root.IsSuccess || root.Value is null) { if (!root.IsSuccess) _confirmation.ShowDiagnostics("项目路径无效", root.Diagnostics); return; }
        await _session.Tasks.RunAsync("正在反编译脚本……", async token =>
        {
            var rpyc = await _fileSystem.EnumerateFilesAsync(root.Value, "game", "*.rpyc", token);
            var rpymc = await _fileSystem.EnumerateFilesAsync(root.Value, "game", "*.rpymc", token);
            var scripts = (rpyc.Value ?? []).Concat(rpymc.Value ?? []).Select(x => x.RelativePath).ToArray();
            if (scripts.Length == 0) { _session.Tasks.StatusMessage = "项目中未找到 RPYC/RPYMC。"; return; }
            if (!_confirmation.Confirm("RPYC 反编译计划", $"将反编译 {scripts.Length} 个脚本；已有 .rpy 跳过，编译文件永不删除。确认继续？", MessageBoxImage.Warning)) return;
            var result = await _decompiler.ExecuteAsync(new ScriptDecompileRequest(root.Value, new ValidatedToolPath(runtime.Value.Python), new ValidatedToolPath(runtime.Value.Unrpyc), scripts, true), _session.Tasks.CreateProgress(), token);
            if (!result.IsSuccess) _confirmation.ShowDiagnostics("反编译未完全成功", result.Diagnostics);
            else _session.Tasks.StatusMessage = $"已处理 {result.Value!.ProcessedScripts} 个脚本；源文件全部保留。";
        });
    }

    private (string Python, string RpaTool, string Unrpyc)? ResolveRuntime()
    {
        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        var value = (Python: Path.Combine(tools, "python", "python.exe"), RpaTool: Path.Combine(tools, "rpatool", "rpatool.py"), Unrpyc: Path.Combine(tools, "unrpyc", "unrpyc.py"));
        if (File.Exists(value.Python) && File.Exists(value.RpaTool) && File.Exists(value.Unrpyc)) return value;
        _confirmation.ShowDiagnostics("工具运行时不可用", [new Diagnostic(DiagnosticSeverity.Error, "TOOL_RUNTIME_MISSING", "发布包缺少隔离工具运行时。请使用 scripts/publish.ps1 生成完整工具目录。", Category: DiagnosticCategory.Process)]);
        return null;
    }
}
