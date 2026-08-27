using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed record SavedTranslationPosition(
    string? ItemId,
    string? ViewMode,
    string? GroupingMode);

public sealed class ProjectSessionViewModel : ObservableObject
{
    private readonly IProjectAnalysisService _analysisService;
    private readonly IProjectCatalogService _catalogService;
    private readonly IProjectSaveService _writer;
    private readonly IFileDialogService _fileDialog;
    private readonly IConfirmationService _confirmation;
    private readonly AppSettingsStore _settings;
    private readonly IThemeService _theme;
    private readonly WorkspaceTaskCoordinator? _taskCoordinator;
    private string _projectPath = string.Empty;
    private string _language = string.Empty;
    private string _sdkPath = string.Empty;
    private bool _autoSaveEnabled = true;
    private string? _lastTranslationItemId;
    private string? _lastTranslationViewMode;
    private string? _lastTranslationGroupingMode;
    private string? _lastTranslationProjectPath;
    private string? _lastTranslationLanguage;
    private ProjectSnapshot? _snapshot;
    private long _languageRequestVersion;
    private long _scopeVersion;
    private long _autoSaveRevision;
    private long _persistedAutoSaveRevision;

    public ProjectSessionViewModel(
        IProjectAnalysisService analysisService,
        IProjectCatalogService catalogService,
        IProjectSaveService writer,
        IFileDialogService fileDialog,
        IConfirmationService confirmation,
        AppSettingsStore settings,
        IThemeService theme,
        TaskCenterViewModel tasks,
        WorkspaceTaskCoordinator? taskCoordinator = null)
    {
        _analysisService = analysisService;
        _catalogService = catalogService;
        _writer = writer;
        _fileDialog = fileDialog;
        _confirmation = confirmation;
        _settings = settings;
        _theme = theme;
        _taskCoordinator = taskCoordinator;
        Tasks = tasks;
        ChooseProjectCommand = new AsyncRelayCommand(ChooseProjectAsync);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(false), () => Snapshot is not null && !Tasks.IsBusy);
        SaveWithAnnotationsCommand = new AsyncRelayCommand(() => SaveAsync(true), () => Snapshot is not null && !Tasks.IsBusy);
        ForceSaveCommand = new AsyncRelayCommand(() => SaveAsync(false, forceOverwrite: true), () => Snapshot is not null && !Tasks.IsBusy);
        Tasks.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TaskCenterViewModel.IsBusy)) NotifyCommandStates();
        };
    }

    public TaskCenterViewModel Tasks { get; }
    public ObservableCollection<string> Languages { get; } = [];
    public IRelayCommand ChooseProjectCommand { get; }
    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SaveWithAnnotationsCommand { get; }
    public IAsyncRelayCommand ForceSaveCommand { get; }
    public event EventHandler<ProjectSnapshot?>? SnapshotChanged;

    /// <summary>控制译文停止输入后是否自动写入项目。</summary>
    public bool AutoSaveEnabled
    {
        get => _autoSaveEnabled;
        set
        {
            if (!SetProperty(ref _autoSaveEnabled, value)) return;
            OnPropertyChanged(nameof(AutoSaveDescription));
        }
    }

    public string AutoSaveDescription => AutoSaveEnabled ? "译文停止输入后自动保存" : "自动保存已关闭";

    public SavedTranslationPosition? LastTranslationPosition => IsLastTranslationPositionInCurrentScope()
        ? new(_lastTranslationItemId, _lastTranslationViewMode, _lastTranslationGroupingMode)
        : null;

    public string ProjectPath
    {
        get => _projectPath;
        set
        {
            if (!SetProperty(ref _projectPath, value)) return;
            Languages.Clear();
            if (_language.Length > 0)
            {
                _language = string.Empty;
                OnPropertyChanged(nameof(Language));
            }
            InvalidateSnapshot();
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(CanAnalyze));
            NotifyCommandStates();
        }
    }

    public string ProjectName => string.IsNullOrWhiteSpace(ProjectPath) ? "未选择项目" : Path.GetFileName(Path.TrimEndingDirectorySeparator(ProjectPath));

    public string Language
    {
        get => _language;
        set
        {
            if (!SetProperty(ref _language, value)) return;
            InvalidateSnapshot();
            OnPropertyChanged(nameof(CanAnalyze));
            NotifyCommandStates();
        }
    }

    public ProjectSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (!SetProperty(ref _snapshot, value)) return;
            SnapshotChanged?.Invoke(this, value);
            NotifyCommandStates();
        }
    }

    public bool CanAnalyze => !string.IsNullOrWhiteSpace(ProjectPath) && !string.IsNullOrWhiteSpace(Language) && !Tasks.IsBusy;

    public string SdkPath
    {
        get => _sdkPath;
        set => SetProperty(ref _sdkPath, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var saved = await _settings.LoadAsync(cancellationToken);
        _theme.TryApplyAccent(saved.AccentColor, out _);
        if (!string.IsNullOrWhiteSpace(saved.SdkPath)) _sdkPath = saved.SdkPath;
        AutoSaveEnabled = saved.AutoSaveEnabled;
        _lastTranslationItemId = saved.LastTranslationItemId;
        _lastTranslationViewMode = saved.LastTranslationViewMode;
        _lastTranslationGroupingMode = saved.LastTranslationGroupingMode;
        _lastTranslationProjectPath = saved.LastProject;
        _lastTranslationLanguage = saved.LastLanguage;
        if (!string.IsNullOrWhiteSpace(saved.LastProject) && Directory.Exists(saved.LastProject))
        {
            ProjectPath = saved.LastProject!;
            await LoadLanguagesAsync(saved.LastLanguage, cancellationToken);
        }
    }

    public async Task LoadLanguagesAsync(string? preferredLanguage = null, CancellationToken cancellationToken = default)
    {
        var requestVersion = Interlocked.Increment(ref _languageRequestVersion);
        var projectPath = ProjectPath;
        var result = await _catalogService.ExecuteAsync(
            new ProjectLanguageDiscoveryRequest(projectPath),
            Tasks.CreateProgress(),
            cancellationToken);
        if (requestVersion != Volatile.Read(ref _languageRequestVersion) ||
            !string.Equals(projectPath, ProjectPath, StringComparison.OrdinalIgnoreCase)) return;
        if (!result.IsSuccess || result.Value is null)
        {
            Tasks.StatusMessage = result.Diagnostics.FirstOrDefault()?.Message ?? "语言发现失败。";
            return;
        }

        Languages.Clear();
        foreach (var language in result.Value) Languages.Add(language);
        Language = preferredLanguage is not null && Languages.Contains(preferredLanguage)
            ? preferredLanguage
            : Languages.FirstOrDefault() ?? preferredLanguage ?? string.Empty;
        Tasks.StatusMessage = Languages.Count == 0 ? "未找到 game/tl/<language>。" : $"发现 {Languages.Count} 个翻译语言。";
    }

    public async Task AnalyzeAsync()
    {
        if (!CanAnalyze) return;
        await Tasks.RunAsync("正在后台分析源码和翻译文件……", AnalyzeCoreAsync);
    }

    public Task ReloadAsync(CancellationToken cancellationToken) => AnalyzeCoreAsync(cancellationToken);

    public async Task SaveAsync(bool refreshAnnotations, bool forceOverwrite = false)
    {
        if (Snapshot is null) return;
        if (!IsSnapshotForScope(Snapshot, ProjectPath, Language))
        {
            Snapshot = null;
            Tasks.StatusMessage = "项目或语言已切换，请重新分析后再保存。";
            return;
        }
        var snapshot = Snapshot;
        if (_taskCoordinator is not null)
            await _taskCoordinator.CancelAsync("translation-auto-save");
        if (forceOverwrite && !_confirmation.Confirm(
                "强制覆盖外部修改",
                "将忽略文件自上次分析后的外部修改并覆盖写入当前译文。\n\n" +
                "保存前仍会执行译文结构校验，并会保留最近一次 .rls.bak 备份。此操作无法自动合并外部修改，仍要继续吗？",
                MessageBoxImage.Warning))
        {
            return;
        }
        var warnings = _writer.Validate(snapshot).Where(x => x.Severity == DiagnosticSeverity.Warning).ToArray();
        if (warnings.Length > 0)
        {
            var message = string.Join(Environment.NewLine, warnings.Take(8).Select(x => "• " + x.Message));
            if (!_confirmation.Confirm("译文结构警告", message + Environment.NewLine + Environment.NewLine + "仍要保存吗？", MessageBoxImage.Warning)) return;
        }

        var saveRevision = Volatile.Read(ref _autoSaveRevision);
        var saved = false;
        await Tasks.RunAsync(forceOverwrite ? "正在强制覆盖保存……" : "正在安全保存……", async token =>
        {
            saved = await SaveCoreAsync(snapshot, refreshAnnotations, allowWarnings: true, reloadAfterSave: true, showDiagnostics: true, forceOverwrite, token);
        });
        if (saved) MarkAutoSaveRevisionPersisted(saveRevision);
    }

    /// <summary>更新下次启动要恢复的翻译位置；实际写入由调用方负责合并调度。</summary>
    public void UpdateLastTranslationPosition(string? itemId, string? viewMode, string? groupingMode)
    {
        _lastTranslationItemId = itemId;
        _lastTranslationViewMode = viewMode;
        _lastTranslationGroupingMode = groupingMode;
        _lastTranslationProjectPath = ProjectPath;
        _lastTranslationLanguage = Language;
    }

    /// <summary>译文发生变化时启动可取消的防抖自动保存。</summary>
    public void ScheduleAutoSave()
    {
        if (!AutoSaveEnabled || Snapshot is null || _taskCoordinator is null) return;
        var revision = Interlocked.Increment(ref _autoSaveRevision);
        _taskCoordinator.StartLatestAfterDelay(
            "translation-auto-save",
            TimeSpan.FromMilliseconds(900),
            cancellationToken => AutoSaveAsync(revision, cancellationToken));
    }

    /// <summary>窗口关闭前保存仍处于防抖等待中的最新译文。</summary>
    public async Task FlushAutoSaveAsync(CancellationToken cancellationToken)
    {
        if (!AutoSaveEnabled || Snapshot is null || _taskCoordinator is null) return;
        await _taskCoordinator.CancelAsync("translation-auto-save");
        var revision = Volatile.Read(ref _autoSaveRevision);
        if (revision <= Volatile.Read(ref _persistedAutoSaveRevision)) return;
        await AutoSaveAsync(revision, cancellationToken);
    }

    public Task PersistSettingsAsync(CancellationToken cancellationToken = default)
    {
        var positionIsCurrent = IsLastTranslationPositionInCurrentScope();
        return _settings.SaveAsync(new AppSettings(
            ProjectPath,
            Language,
            _theme.AccentColor,
            _sdkPath,
            AutoSaveEnabled,
            positionIsCurrent ? _lastTranslationItemId : null,
            positionIsCurrent ? _lastTranslationViewMode : null,
            positionIsCurrent ? _lastTranslationGroupingMode : null), cancellationToken);
    }

    private async Task AutoSaveAsync(long revision, CancellationToken cancellationToken)
    {
        if (!AutoSaveEnabled || Snapshot is null) return;
        while (Tasks.IsBusy)
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);

        var snapshot = Snapshot;
        var warnings = _writer.Validate(snapshot).Where(x => x.Severity == DiagnosticSeverity.Warning).ToArray();
        if (warnings.Length > 0)
        {
            Tasks.StatusMessage = "自动保存已跳过：存在译文结构警告，请使用手动保存确认。";
            return;
        }

        var saved = false;
        await Tasks.RunAsync("正在自动保存译文……", async taskToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, taskToken);
            saved = await SaveCoreAsync(snapshot, refreshAnnotations: false, allowWarnings: false, reloadAfterSave: false, showDiagnostics: false, forceOverwrite: false, linked.Token);
        });
        if (saved && revision == Volatile.Read(ref _autoSaveRevision))
            MarkAutoSaveRevisionPersisted(revision);
    }

    private async Task<bool> SaveCoreAsync(
        ProjectSnapshot snapshot,
        bool refreshAnnotations,
        bool allowWarnings,
        bool reloadAfterSave,
        bool showDiagnostics,
        bool forceOverwrite,
        CancellationToken cancellationToken)
    {
        var result = await _writer.ExecuteAsync(
            new ProjectSaveRequest(snapshot, refreshAnnotations, allowWarnings, forceOverwrite),
            Tasks.CreateProgress(),
            cancellationToken);
        var errors = result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        if (!result.IsSuccess || errors.Length > 0)
        {
            if (result.Status != OperationStatus.Cancelled && showDiagnostics)
            {
                var diagnostics = result.Diagnostics.Count > 0
                    ? result.Diagnostics
                    : [new Diagnostic(DiagnosticSeverity.Error, "SAVE_FAILED", "保存未完成。")];
                _confirmation.ShowDiagnostics("保存失败", diagnostics);
            }
            else if (result.Status != OperationStatus.Cancelled)
            {
                var first = result.Diagnostics.FirstOrDefault()?.Message ?? "文件可能已被外部修改。";
                Tasks.StatusMessage = $"自动保存失败：{first}";
                foreach (var diagnostic in result.Diagnostics.Take(3))
                    Tasks.Logs.Add(new ToolLogEntry(diagnostic.Message, diagnostic.Severity == DiagnosticSeverity.Error ? "Error" : "Warning"));
            }
            else
            {
                Tasks.StatusMessage = "保存已取消。";
            }
            return false;
        }

        Tasks.StatusMessage = reloadAfterSave
            ? $"已{(forceOverwrite ? "强制覆盖" : "安全")}保存 {result.Value?.SavedFiles ?? 0} 个文件；正在重新加载。"
            : $"自动保存完成：{result.Value?.SavedFiles ?? 0} 个文件。";
        if (reloadAfterSave) await AnalyzeCoreAsync(cancellationToken);
        return true;
    }

    private bool IsLastTranslationPositionInCurrentScope() =>
        string.Equals(_lastTranslationProjectPath, ProjectPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(_lastTranslationLanguage, Language, StringComparison.OrdinalIgnoreCase);

    private void MarkAutoSaveRevisionPersisted(long revision)
    {
        long persisted;
        do
        {
            persisted = Volatile.Read(ref _persistedAutoSaveRevision);
            if (revision <= persisted) return;
        }
        while (Interlocked.CompareExchange(ref _persistedAutoSaveRevision, revision, persisted) != persisted);
    }

    private async Task ChooseProjectAsync()
    {
        var selected = _fileDialog.SelectProjectFolder(ProjectPath);
        if (selected is null) return;
        ProjectPath = selected;
        await LoadLanguagesAsync();
    }

    private async Task AnalyzeCoreAsync(CancellationToken token)
    {
        var scopeVersion = Volatile.Read(ref _scopeVersion);
        var projectPath = ProjectPath;
        var language = Language;
        var result = await _analysisService.ExecuteAsync(
            new ProjectAnalysisRequest(projectPath, language), Tasks.CreateProgress(), token);
        if (scopeVersion != Volatile.Read(ref _scopeVersion) ||
            !string.Equals(projectPath, ProjectPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(language, Language, StringComparison.OrdinalIgnoreCase))
        {
            Tasks.StatusMessage = "项目或语言已切换，已丢弃旧分析结果。";
            return;
        }
        if (!result.IsSuccess || result.Value is null)
        {
            _confirmation.ShowDiagnostics("分析失败", result.Diagnostics);
            return;
        }
        Snapshot = result.Value;
        await PersistSettingsAsync(token);
        Tasks.StatusMessage = $"完成：{Snapshot.Graph.Nodes.Count:N0} 个流程节点，{Snapshot.TranslationUnits.Count():N0} 个翻译条目，{Snapshot.Diagnostics.Count:N0} 条诊断。";
    }

    internal static bool IsSnapshotForScope(ProjectSnapshot snapshot, string projectPath, string language)
    {
        try
        {
            return string.Equals(
                       Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshot.ProjectRoot)),
                       Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath)),
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(snapshot.Language, language, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void InvalidateSnapshot()
    {
        Interlocked.Increment(ref _scopeVersion);
        if (Tasks.CancelCommand.CanExecute(null)) Tasks.CancelCommand.Execute(null);
        Snapshot = null;
        Interlocked.Exchange(ref _autoSaveRevision, 0);
        Interlocked.Exchange(ref _persistedAutoSaveRevision, 0);
        _taskCoordinator?.StartLatest("translation-auto-save", _ => Task.CompletedTask);
    }

    private void NotifyCommandStates()
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveWithAnnotationsCommand.NotifyCanExecuteChanged();
        ForceSaveCommand.NotifyCanExecuteChanged();
    }
}
