using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed class ProjectSessionViewModel : ObservableObject
{
    private readonly IProjectAnalysisService _analysisService;
    private readonly IProjectCatalogService _catalogService;
    private readonly IProjectSaveService _writer;
    private readonly IFileDialogService _fileDialog;
    private readonly IConfirmationService _confirmation;
    private readonly AppSettingsStore _settings;
    private readonly IThemeService _theme;
    private string _projectPath = string.Empty;
    private string _language = string.Empty;
    private string _sdkPath = string.Empty;
    private ProjectSnapshot? _snapshot;

    public ProjectSessionViewModel(
        IProjectAnalysisService analysisService,
        IProjectCatalogService catalogService,
        IProjectSaveService writer,
        IFileDialogService fileDialog,
        IConfirmationService confirmation,
        AppSettingsStore settings,
        IThemeService theme,
        TaskCenterViewModel tasks)
    {
        _analysisService = analysisService;
        _catalogService = catalogService;
        _writer = writer;
        _fileDialog = fileDialog;
        _confirmation = confirmation;
        _settings = settings;
        _theme = theme;
        Tasks = tasks;
        ChooseProjectCommand = new AsyncRelayCommand(ChooseProjectAsync);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(false), () => Snapshot is not null && !Tasks.IsBusy);
        SaveWithAnnotationsCommand = new AsyncRelayCommand(() => SaveAsync(true), () => Snapshot is not null && !Tasks.IsBusy);
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
    public event EventHandler<ProjectSnapshot?>? SnapshotChanged;

    public string ProjectPath
    {
        get => _projectPath;
        set
        {
            if (!SetProperty(ref _projectPath, value)) return;
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

    public async Task InitializeAsync()
    {
        var saved = await _settings.LoadAsync();
        _theme.TryApplyAccent(saved.AccentColor, out _);
        if (!string.IsNullOrWhiteSpace(saved.SdkPath)) _sdkPath = saved.SdkPath;
        if (!string.IsNullOrWhiteSpace(saved.LastProject) && Directory.Exists(saved.LastProject))
        {
            ProjectPath = saved.LastProject!;
            await LoadLanguagesAsync(saved.LastLanguage);
        }
    }

    public async Task LoadLanguagesAsync(string? preferredLanguage = null, CancellationToken cancellationToken = default)
    {
        Languages.Clear();
        var result = await _catalogService.ExecuteAsync(
            new ProjectLanguageDiscoveryRequest(ProjectPath),
            Tasks.CreateProgress(),
            cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            Tasks.StatusMessage = result.Diagnostics.FirstOrDefault()?.Message ?? "语言发现失败。";
            return;
        }

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

    public async Task SaveAsync(bool refreshAnnotations)
    {
        if (Snapshot is null) return;
        var warnings = _writer.Validate(Snapshot).Where(x => x.Severity == DiagnosticSeverity.Warning).ToArray();
        if (warnings.Length > 0)
        {
            var message = string.Join(Environment.NewLine, warnings.Take(8).Select(x => "• " + x.Message));
            if (!_confirmation.Confirm("译文结构警告", message + Environment.NewLine + Environment.NewLine + "仍要保存吗？", MessageBoxImage.Warning)) return;
        }

        await Tasks.RunAsync("正在安全保存……", async token =>
        {
            var result = await _writer.ExecuteAsync(
                new ProjectSaveRequest(Snapshot, refreshAnnotations, true),
                Tasks.CreateProgress(),
                token);
            var errors = result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
            {
                _confirmation.ShowDiagnostics("保存失败", errors);
                return;
            }
            Tasks.StatusMessage = $"已安全保存 {result.Value?.SavedFiles ?? 0} 个文件；正在重新加载。";
            await AnalyzeCoreAsync(token);
        });
    }

    public Task PersistSettingsAsync() => _settings.SaveAsync(new AppSettings(ProjectPath, Language, _theme.AccentColor, _sdkPath));

    private async Task ChooseProjectAsync()
    {
        var selected = _fileDialog.SelectProjectFolder(ProjectPath);
        if (selected is null) return;
        ProjectPath = selected;
        await LoadLanguagesAsync();
    }

    private async Task AnalyzeCoreAsync(CancellationToken token)
    {
        var result = await _analysisService.ExecuteAsync(
            new ProjectAnalysisRequest(ProjectPath, Language), Tasks.CreateProgress(), token);
        if (!result.IsSuccess || result.Value is null)
        {
            _confirmation.ShowDiagnostics("分析失败", result.Diagnostics);
            return;
        }
        Snapshot = result.Value;
        await PersistSettingsAsync();
        Tasks.StatusMessage = $"完成：{Snapshot.Graph.Nodes.Count:N0} 个流程节点，{Snapshot.TranslationUnits.Count():N0} 个翻译条目，{Snapshot.Diagnostics.Count:N0} 条诊断。";
    }

    private void NotifyCommandStates()
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveWithAnnotationsCommand.NotifyCanExecuteChanged();
    }
}
