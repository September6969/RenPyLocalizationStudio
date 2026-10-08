using System.Windows;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed partial class ProjectSessionViewModelTests
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

    [Fact]
    public async Task 待译导航跳过完成项并回绕且编辑后更新按钮状态()
    {
        await using var project = await TestFiles.CreateProjectAsync(
            "label start:\n    return\n",
            "translate schinese strings:\n    old \"First\"\n    new \"\"\n    old \"Done\"\n    new \"完成\"\n    old \"Last\"\n    new \"\"\n");
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.ViewMode = TranslationViewMode.Strings;
        var first = workspace.VisibleItems.Single(item => item.SharedString?.OldText == "First");
        var last = workspace.VisibleItems.Single(item => item.SharedString?.OldText == "Last");
        workspace.SelectedItem = first;

        workspace.MoveNextPendingCommand.Execute(null);
        Assert.Same(last, workspace.SelectedItem);
        Assert.True(workspace.IsInspectorOpen);
        workspace.MoveNextPendingCommand.Execute(null);
        Assert.Same(first, workspace.SelectedItem);
        workspace.Inspector.TranslationText = "第一条";
        workspace.MoveNextPendingCommand.Execute(null);
        Assert.Same(last, workspace.SelectedItem);
        workspace.Inspector.TranslationText = "最后一条";
        Assert.False(workspace.MoveNextPendingCommand.CanExecute(null));
    }

    [Fact]
    public async Task 搜索与空状态区分未分析书签和无结果且待译导航不越过筛选()
    {
        await using var project = await TestFiles.CreateProjectAsync(
            "label start:\n    return\n",
            "translate schinese strings:\n    old \"First\"\n    new \"\"\n    old \"Done\"\n    new \"完成\"\n");
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        Assert.Equal("还没有可浏览的项目", workspace.EmptyStateTitle);
        Assert.False(workspace.MoveNextPendingCommand.CanExecute(null));
        await session.AnalyzeAsync();
        workspace.ViewMode = TranslationViewMode.Bookmarks;
        Assert.True(workspace.IsListEmpty);
        Assert.Equal("还没有书签", workspace.EmptyStateTitle);
        workspace.ViewMode = TranslationViewMode.Strings;
        workspace.SearchText = "Done";
        Assert.False(workspace.MoveNextPendingCommand.CanExecute(null));
        Assert.Equal("1 / 2 项", workspace.ItemCount);
        workspace.SearchText = "不存在的关键词";
        Assert.True(workspace.IsListEmpty);
        Assert.Equal("没有匹配的条目", workspace.EmptyStateTitle);
        workspace.ClearSearchCommand.Execute(null);
        Assert.False(workspace.IsListEmpty);
        Assert.True(workspace.MoveNextPendingCommand.CanExecute(null));
    }

    [Fact]
    public async Task 复用候选跟随条目切换更新且切换语言后清空()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        var donor = workspace.VisibleItems.Single(item => item.Unit?.Identifier == "start_1");
        var target = workspace.VisibleItems.Single(item => item.Unit?.Identifier == "start_4");
        workspace.SelectedItem = donor;
        workspace.Inspector.TranslationText = "新译法 [name]";
        workspace.SelectedItem = target;
        Assert.Contains(workspace.Inspector.Suggestions, suggestion => suggestion.Translation == "新译法 [name]");
        var previousCount = workspace.TranslatedCount;
        workspace.Inspector.TranslationText = workspace.Inspector.Suggestions[0].Translation;
        Assert.Equal(previousCount + 1, workspace.TranslatedCount);
        session.Language = "english";
        Assert.Empty(workspace.Inspector.Suggestions);
        Assert.False(workspace.Inspector.HasSuggestions);
    }

    [Fact]
    public async Task 状态筛选和搜索组合生效且编辑中保留条目直到主动刷新()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.StatusFilter = TranslationStatusFilter.Pending;
        var pending = Assert.Single(workspace.VisibleItems);
        workspace.SelectedItem = pending;
        workspace.Inspector.TranslationText = "新译文 [name]";
        Assert.Same(pending, workspace.SelectedItem);
        Assert.Contains(pending, workspace.VisibleItems);
        Assert.True(workspace.NeedsResultsRefresh);
        workspace.RefreshResultsCommand.Execute(null);
        Assert.Empty(workspace.VisibleItems);
        workspace.StatusFilter = TranslationStatusFilter.Modified;
        var modified = Assert.Single(workspace.VisibleItems);
        Assert.Same(pending.Unit, modified.Unit);
        workspace.SearchText = "没有匹配";
        Assert.Empty(workspace.VisibleItems);
        workspace.ClearSearchCommand.Execute(null);
        Assert.Equal(TranslationStatusFilter.All, workspace.StatusFilter);
        Assert.False(workspace.HasActiveFilters);
        workspace.StatusFilter = TranslationStatusFilter.Modified;
        await session.SaveAsync(false);
        Assert.Empty(workspace.VisibleItems);
    }

    [Fact]
    public async Task 质量检查可直接编辑共享字符串且刷新后清除已修复项()
    {
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n",
            "translate schinese strings:\n    old \"Hello [name]\"\n    new \"\"\n");
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.ViewMode = TranslationViewMode.Quality;
        var issue = Assert.Single(workspace.VisibleItems);
        workspace.IsInspectorOpen = false;
        workspace.SelectedItem = issue;
        Assert.True(workspace.IsInspectorOpen);
        Assert.Equal("Hello [name]", workspace.Inspector.SourceText);
        var focus = workspace.Inspector.FocusRequest;
        workspace.OpenSelectedCommand.Execute(null);
        Assert.True(workspace.Inspector.FocusRequest > focus);
        workspace.Inspector.TranslationText = "你好 [name]";
        Assert.Equal("你好 [name]", session.Snapshot!.SharedStrings[0].Translation);
        Assert.True(workspace.NeedsResultsRefresh);
        workspace.RefreshResultsCommand.Execute(null);
        Assert.Empty(workspace.VisibleItems);
        Assert.False(workspace.NeedsResultsRefresh);
        Assert.Equal("本轮检查未发现译文问题", workspace.EmptyStateTitle);
    }

    [Fact]
    public async Task 文件和Label限定进度且导航会解除遮挡目标的筛选()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        await File.WriteAllTextAsync(Path.Combine(project.Root, "game", "other.rpy"), "label other:\n    \"Other\"\n", new System.Text.UTF8Encoding(false));
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        var scope = workspace.ProgressScopes.Single(option => option.Label == "Label · start");
        workspace.SelectedScope = scope.Id;
        Assert.Equal(4, workspace.EditableCount);
        Assert.Equal(3, workspace.TranslatedCount);
        workspace.StatusFilter = TranslationStatusFilter.Pending;
        Assert.Single(workspace.VisibleItems);
        Assert.Equal(4, workspace.EditableCount);
        Assert.DoesNotContain(workspace.VisibleItems, item => item.Node?.Region.RelativePath == "game/other.rpy");
        var target = session.Snapshot!.Graph.Labels["other"];
        Assert.True(workspace.Navigate(new NavigateToSourceRequest(target.Id, null, null, "other")));
        Assert.Same(target, workspace.SelectedItem!.Node);
        Assert.Equal("all", workspace.SelectedScope);
        Assert.Equal(TranslationStatusFilter.All, workspace.StatusFilter);
    }

    [Fact]
    public async Task 质量检查修复完文件后保留文件范围且共享冲突与待译分开筛选()
    {
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n",
            "translate schinese strings:\n    old \"A\"\n    new \"甲\"\n    old \"A\"\n    new \"乙\"\n    old \"B\"\n    new \"\"\n");
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.ViewMode = TranslationViewMode.Strings;
        workspace.StatusFilter = TranslationStatusFilter.Conflict;
        Assert.Equal("A", Assert.Single(workspace.VisibleItems).SharedString!.OldText);
        workspace.StatusFilter = TranslationStatusFilter.Pending;
        Assert.Equal("B", Assert.Single(workspace.VisibleItems).SharedString!.OldText);
        workspace.ViewMode = TranslationViewMode.Quality;
        workspace.SelectedScope = "file:tl/schinese/script.rpy";
        Assert.Equal(2, workspace.VisibleItems.Count);
        foreach (var shared in session.Snapshot!.SharedStrings) shared.Unify("已处理");
        workspace.RefreshResultsCommand.Execute(null);
        Assert.Empty(workspace.VisibleItems);
        Assert.Equal("file:tl/schinese/script.rpy", workspace.SelectedScope);
    }

    [Fact]
    public async Task Screen文本不会被归入同文件前面的剧情Label()
    {
        await using var project = await TestFiles.CreateProjectAsync(
            "label start:\n    menu:\n        \"Choice\":\n            return\nscreen panel():\n    text _(\"UI\")\n",
            "translate schinese strings:\n    # game/script.rpy:3\n    old \"Choice\"\n    new \"选项\"\n    # game/script.rpy:6\n    old \"UI\"\n    new \"界面\"\n");
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.ViewMode = TranslationViewMode.Strings;
        workspace.SelectedScope = workspace.ProgressScopes.Single(option => option.Label == "Label · start").Id;
        Assert.Equal("Choice", Assert.Single(workspace.VisibleItems).SharedString!.OldText);
        workspace.SelectedLabel = workspace.Labels[0];
        workspace.NavigateLabelCommand.Execute(null);
        Assert.Equal(TranslationViewMode.Flow, workspace.ViewMode);
        Assert.NotNull(workspace.SelectedItem);
    }

    [Fact]
    public async Task 部分保存只清除已提交文件的修改状态并提示刷新()
    {
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n",
            "translate schinese strings:\n    old \"A\"\n    new \"\"\n");
        var other = Path.Combine(project.Root, "game", "tl", "schinese", "z_other.rpy");
        await File.WriteAllTextAsync(other, "translate schinese strings:\n    old \"B\"\n    new \"\"\n", new System.Text.UTF8Encoding(false));
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.ViewMode = TranslationViewMode.Strings;
        foreach (var shared in session.Snapshot!.SharedStrings) shared.Translation = "已编辑";
        workspace.StatusFilter = TranslationStatusFilter.Modified;
        Assert.Equal(2, workspace.VisibleItems.Count);
        await File.AppendAllTextAsync(other, "# 外部修改\n", new System.Text.UTF8Encoding(false));
        await session.SaveAsync(false);
        Assert.True(workspace.NeedsResultsRefresh);
        workspace.RefreshResultsCommand.Execute(null);
        Assert.Equal("B", Assert.Single(workspace.VisibleItems).SharedString!.OldText);
        Assert.True(session.HasUnsavedChanges);
    }

    private static ProjectSessionViewModel CreateTranslationSession(string root)
    {
        var fileSystem = new FileSystemService();
        return new ProjectSessionViewModel(new ProjectAnalysisService(fileSystem), new ProjectCatalogService(fileSystem),
            new ProjectWriter(fileSystem), new NullFileDialogService(), new AcceptConfirmationService(),
            new AppSettingsStore(Path.Combine(root, "settings.json")), new FixedThemeService(), new TaskCenterViewModel())
        { ProjectPath = root, Language = "schinese" };
    }

    private sealed class EmptyPreviewService : IRenPyImagePreviewService
    {
        public Task<RenPySceneContext> ResolveSceneContextAsync(RenPyImagePreviewRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RenPySceneContext(null, null, []));
        public void InvalidateProject(string projectPath) { }
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
