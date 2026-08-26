using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App;

/// <summary>应用程序组合根。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var fileSystem = new FileSystemService();
        var processRunner = new ProcessRunnerService();
        var confirmation = new ConfirmationService();
        var dialogs = new FileDialogService();
        var theme = new ThemeService();
        var settings = new AppSettingsStore();
        var tasks = new TaskCenterViewModel();
        var messenger = WeakReferenceMessenger.Default;

        var session = new ProjectSessionViewModel(
            new ProjectAnalysisService(fileSystem),
            new ProjectCatalogService(fileSystem),
            new ProjectWriter(fileSystem),
            dialogs,
            confirmation,
            settings,
            theme,
            tasks);
        var patchService = new ManagedPatchService(fileSystem);
        var translation = new TranslationWorkspaceViewModel(session);
        var tl = new TlWorkspaceViewModel(session, new RenPySdkService(fileSystem, processRunner), confirmation, dialogs);
        var extra = new ExtraTextWorkspaceViewModel(session, fileSystem, new ExtraTextScanService(fileSystem), patchService, confirmation);
        var patch = new PatchWorkspaceViewModel(session, fileSystem, patchService, new ReplacementRuleService(), confirmation);
        var archive = new ArchiveWorkspaceViewModel(session, fileSystem,
            new ArchiveExtractionService(fileSystem, processRunner), new ScriptDecompilerService(fileSystem, processRunner),
            new PrefixRenameService(fileSystem), new ImageCompressionService(fileSystem), confirmation);
        var diagnostics = new DiagnosticsWorkspaceViewModel(session, messenger);
        var main = new MainViewModel(session, tasks, theme, messenger, translation, tl, extra, patch, archive, diagnostics);

        MainWindow = new MainWindow(main);
        MainWindow.Show();
    }
}
