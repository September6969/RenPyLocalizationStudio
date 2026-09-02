using System.Windows;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class ProjectSessionViewModelTests
{
    [Fact]
    public async Task Settings_损坏Json返回明确诊断和默认值()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rls-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, "{ invalid json");
        try
        {
            var result = await new AppSettingsStore(path).LoadAsync(CancellationToken.None);

            Assert.Equal(OperationStatus.Failed, result.Status);
            Assert.Equal("#D16BA5", result.Value?.AccentColor);
            Assert.False(result.Value?.AutoSaveEnabled);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SETTINGS_JSON_INVALID");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task 切换语言立即清除旧快照并禁用保存()
    {
        await using var project = await TestFiles.CreateProjectAsync(
            "label start:\n    \"Hello\"\n",
            "translate schinese strings:\n\n    old \"Start\"\n    new \"开始\"\n");
        var settingsPath = Path.Combine(project.Root, "settings.json");
        var fileSystem = new FileSystemService();
        var session = new ProjectSessionViewModel(
            new ProjectAnalysisService(fileSystem),
            new ProjectCatalogService(fileSystem),
            new ProjectWriter(fileSystem),
            new NullFileDialogService(),
            new AcceptConfirmationService(),
            new AppSettingsStore(settingsPath),
            new FixedThemeService(),
            new TaskCenterViewModel());
        session.ProjectPath = project.Root;
        session.Language = "schinese";
        await session.AnalyzeAsync();
        Assert.NotNull(session.Snapshot);

        session.Language = "english";

        Assert.Null(session.Snapshot);
        Assert.False(session.SaveCommand.CanExecute(null));
    }

    private sealed class NullFileDialogService : IFileDialogService
    {
        public string? SelectProjectFolder(string? initialDirectory) => null;
        public string? SelectSdkExecutable(string? initialPath) => null;
    }

    private sealed class AcceptConfirmationService : IConfirmationService
    {
        public bool Confirm(string title, string message, MessageBoxImage image = MessageBoxImage.Question) => true;
        public void ShowDiagnostics(string title, IEnumerable<Diagnostic> diagnostics) { }
        public void ShowMessage(string title, string message, MessageBoxImage image = MessageBoxImage.Information) { }
    }

    private sealed class FixedThemeService : IThemeService
    {
        public string AccentColor => "#D16BA5";
        public bool TryApplyAccent(string color, out string? error)
        {
            error = null;
            return true;
        }
    }
}
