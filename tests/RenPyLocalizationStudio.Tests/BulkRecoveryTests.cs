using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed partial class ProjectSessionViewModelTests
{
    private static void UniqueDonors(ProjectSnapshot snapshot)
    {
        foreach (var unit in snapshot.TranslationUnits.Where(u => !string.IsNullOrWhiteSpace(u.TranslationText)))
            unit.TranslationText = "你好 [name]";
    }

    [Fact]
    public async Task 批量仅填当前范围空译文且保存后仍可整体撤销()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        UniqueDonors(session.Snapshot!);
        workspace.StatusFilter = TranslationStatusFilter.Pending;
        var pending = Assert.Single(workspace.VisibleItems).Unit!;
        var plan = await workspace.PreviewBulkAsync(false, CancellationToken.None);
        Assert.True(Assert.Single(plan).CanApply);
        Assert.False(pending.IsDirty);
        Assert.True(workspace.ApplyBulk(plan));
        Assert.Equal("你好 [name]", pending.TranslationText);
        Assert.True(pending.IsDirty);
        Assert.True(workspace.NeedsResultsRefresh);
        Assert.Single(workspace.VisibleItems);
        await session.SaveAsync(false);
        Assert.True(workspace.UndoBulkCommand.CanExecute(null));
        Assert.True(workspace.UndoBulk());
        var restored = session.Snapshot!.TranslationUnits.Single(u => u.Identifier == "start_4");
        Assert.Equal("", restored.TranslationText);
        Assert.True(restored.IsDirty);
        await session.SaveAsync(false);
        var disk = await TestFiles.AnalyzeAsync(project.Root);
        Assert.Equal("", disk.TranslationUnits.Single(u => u.Identifier == "start_4").TranslationText);
    }

    [Fact]
    public async Task 多译法不自动选择且预览后来源改变阻止全部应用()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        var ambiguous = await workspace.PreviewBulkAsync(true, CancellationToken.None);
        Assert.DoesNotContain(ambiguous, p => p.CanApply);
        Assert.Contains(ambiguous, p => p.SkipReason == "存在多种有效译法");
        UniqueDonors(session.Snapshot!);
        var plan = await workspace.PreviewBulkAsync(true, CancellationToken.None);
        session.Snapshot!.TranslationUnits.First().TranslationText = "不同译法 [name]";
        Assert.False(workspace.ApplyBulk(plan.Where(p => p.CanApply).ToArray()));
        Assert.Equal("", session.Snapshot.TranslationUnits.Last().TranslationText);
    }

    [Fact]
    public async Task 批量目标再次编辑后禁止撤销且重新分析清空历史()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        UniqueDonors(session.Snapshot!);
        var plan = await workspace.PreviewBulkAsync(true, CancellationToken.None);
        Assert.True(workspace.ApplyBulk(plan.Where(p => p.CanApply).ToArray()));
        workspace.SelectedItem = workspace.VisibleItems.Single(i => i.Unit?.Identifier == "start_4");
        workspace.Inspector.TranslationText = "人工编辑 [name]";
        workspace.Inspector.TranslationText = "你好 [name]";
        Assert.False(workspace.UndoBulk());
        await session.AnalyzeAsync();
        Assert.False(workspace.UndoBulkCommand.CanExecute(null));
    }

    [Fact]
    public async Task 批量预览切换语言后失效并且取消不修改数据()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        UniqueDonors(session.Snapshot!);
        var plan = await workspace.PreviewBulkAsync(true, CancellationToken.None);
        session.Language = "english";
        Assert.False(workspace.ApplyBulk(plan.Where(p => p.CanApply).ToArray()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new BulkTranslationPlanner().Plan([], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task 不可变译文捕获不混入后续编辑且共享定义仅生成一个批量目标()
    {
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n", """
translate schinese strings:
    old "Hello [name]"
    new ""
    old "Hello [name]"
    new ""
""");
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var captured = TranslationReadSnapshot.Capture(snapshot);
        snapshot.SharedStrings[0].Translation = "你好 [name]";
        Assert.Equal("EMPTY_TRANSLATION", Assert.Single(new TranslationQualityService().Check(captured)).Diagnostics.Single().Code);
        Assert.Empty(new TranslationQualityService().Check(snapshot));
        Assert.False(TranslationReadSnapshot.Matches(captured, TranslationReadSnapshot.Capture(snapshot)));
        var donor = captured[0] with { Id = 1, Translation = "你好 [name]", Shared = null, Definitions = [] };
        var plan = new BulkTranslationPlanner().Plan([.. captured, donor]);
        Assert.Single(plan, p => p.CanApply);
        Assert.Contains("script.rpy", plan[0].Target.Impact);
    }

    [Fact]
    public async Task 保存恢复书签筛选并隔离不同语言与项目()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using (var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService()))
        {
            await session.AnalyzeAsync();
            var target = workspace.VisibleItems.Single(i => i.Unit?.Identifier == "start_4");
            workspace.SelectedItem = target;
            workspace.ToggleBookmarkCommand.Execute(target);
            workspace.SearchText = "Hello";
            workspace.StatusFilter = TranslationStatusFilter.Pending;
            workspace.SelectedScope = "file:game/script.rpy";
            workspace.RememberWorkspaceState();
            await session.PersistSettingsAsync();
            session.Language = "english";
            Assert.Equal("", workspace.SearchText);
            Assert.Equal(0, workspace.BookmarkedCount);
            session.Language = "schinese";
            await session.AnalyzeAsync();
            Assert.Equal(1, workspace.BookmarkedCount);
            Assert.Equal("Hello", workspace.SearchText);
        }
        var reopened = CreateTranslationSession(project.Root);
        using var restored = new TranslationWorkspaceViewModel(reopened, new EmptyPreviewService());
        await reopened.InitializeAsync();
        await reopened.AnalyzeAsync();
        Assert.Equal(1, restored.BookmarkedCount);
        Assert.Equal("Hello", restored.SearchText);
        Assert.Equal(TranslationStatusFilter.Pending, restored.StatusFilter);
        Assert.Equal("file:game/script.rpy", restored.SelectedScope);
        Assert.Equal("start_4", restored.SelectedItem?.Unit?.Identifier);
        await using var other = await TestFiles.CreateProjectAsync("label start:\n    return\n", """
translate schinese strings:
    old "Other"
    new "其他"
""");
        reopened.ProjectPath = other.Root;
        reopened.Language = "schinese";
        await reopened.AnalyzeAsync();
        Assert.Equal(0, restored.BookmarkedCount);
        Assert.Equal("", restored.SearchText);
    }

    [Fact]
    public async Task 书签插行后仅唯一上下文恢复且删除后保留失效提示()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var label = snapshot.Graph.Labels["start"];
        var bookmark = WorkspaceStateIdentity.Bookmark(snapshot, label);
        var path = Path.Combine(project.Root, "game", "script.rpy");
        await File.WriteAllTextAsync(path, "# 插行\n" + await File.ReadAllTextAsync(path));
        var shifted = await TestFiles.AnalyzeAsync(project.Root);
        Assert.Equal(2, WorkspaceStateIdentity.Resolve(shifted, bookmark)?.Region.StartLine);
        var session = CreateTranslationSession(project.Root);
        session.RememberWorkspaceState(new(project.Root, "schinese", label.Id, "Bookmarks", "StoryPath", Bookmarks: [bookmark]));
        await File.WriteAllTextAsync(path, "label other:\n    return\n");
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        Assert.Equal(0, workspace.BookmarkedCount);
        Assert.True(workspace.HasRecoveryMessage);
        workspace.RememberWorkspaceState();
        Assert.Single(session.GetWorkspaceState(project.Root, "schinese")!.Bookmarks!);
    }

    [Fact]
    public async Task 旧设置迁移且隐藏位置不清除筛选或切换视图()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var store = new AppSettingsStore(Path.Combine(project.Root, "settings.json"));
        await store.SaveAsync(new(project.Root, "schinese", "#D16BA5", LastTranslationViewMode: "Strings"), CancellationToken.None);
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.InitializeAsync();
        Assert.Equal("Strings", session.GetWorkspaceState(project.Root, "schinese")?.ViewMode);
        session.RememberWorkspaceState(new(project.Root, "schinese", "不存在", "Quality", "StoryPath", "隐藏所有", "Pending", "file:不存在"));
        await session.AnalyzeAsync();
        Assert.Equal(TranslationViewMode.Quality, workspace.ViewMode);
        Assert.Equal("隐藏所有", workspace.SearchText);
        Assert.Equal("all", workspace.SelectedScope);
        Assert.Null(workspace.SelectedItem);
        Assert.Empty(workspace.VisibleItems);
    }
    [Fact]
    public async Task 大项目连续筛选仅发布最新结果且质量取消和切换语言不会回写旧列表()
    {
        var tl = "translate schinese strings:\n" + string.Concat(Enumerable.Range(0, 3000)
            .Select(i => $"    old \"Text {i}\"\n    new \"译文 {i}\"\n"));
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n", tl);
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.ViewMode = TranslationViewMode.Strings;
        workspace.SearchText = "Text 1";
        workspace.SearchText = "Text 2999";
        await workspace.PendingResults.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Text 2999", Assert.Single(workspace.VisibleItems).SharedString!.OldText);
        workspace.SearchText = "";
        await workspace.PendingResults;
        workspace.ViewMode = TranslationViewMode.Quality;
        await workspace.PendingResults.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(workspace.VisibleItems);
        Assert.False(workspace.IsComputing);
        workspace.ViewMode = TranslationViewMode.Strings;
        workspace.CancelResultsCommand.Execute(null);
        await workspace.PendingResults;
        Assert.False(workspace.IsComputing);
        workspace.ViewMode = TranslationViewMode.Quality;
        session.Language = "english";
        await workspace.PendingResults.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(workspace.VisibleItems);
        Assert.False(workspace.IsComputing);
    }

    [Fact]
    public async Task 批量结果部分保存失败仍保留待保存状态且可撤销全部目标()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var second = Path.Combine(project.Root, "game", "tl", "schinese", "z_second.rpy");
        await File.WriteAllTextAsync(second, """
translate schinese other_1:
    # e "Hello [name]"
    e ""
""");
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        UniqueDonors(session.Snapshot!);
        var plan = await workspace.PreviewBulkAsync(true, CancellationToken.None);
        var applicable = plan.Where(p => p.CanApply).ToArray();
        Assert.Equal(2, applicable.Length);
        Assert.True(workspace.ApplyBulk(applicable));
        await File.AppendAllTextAsync(second, "\n# 外部修改\n");
        await session.SaveAsync(false);
        var firstUnit = session.Snapshot!.TranslationUnits.Single(u => u.Identifier == "start_4");
        var failedUnit = session.Snapshot.TranslationUnits.Single(u => u.Identifier == "other_1");
        Assert.False(firstUnit.IsDirty);
        Assert.True(failedUnit.IsDirty);
        Assert.True(workspace.UndoBulk());
        Assert.Equal("", firstUnit.TranslationText);
        Assert.Equal("", failedUnit.TranslationText);
        Assert.True(firstUnit.IsDirty);
        Assert.True(failedUnit.IsDirty);
        Assert.Contains("外部修改", await File.ReadAllTextAsync(second));
    }

    [Fact]
    public void 批量跳过复杂块冲突和占位符异常并支持指定范围()
    {
        TranslationReadEntry Entry(int id, string translation = "", bool raw = false, bool conflict = false) =>
            new(id, "Hello [name]", translation, conflict, raw, !raw, "a.rpy", id + 1, "a.rpy", [], null, null);
        var rows = new[] { Entry(0), Entry(1, "没有占位符"), Entry(2, "正确 [name]", conflict: true), Entry(3, raw: true) };
        var planner = new BulkTranslationPlanner();
        Assert.DoesNotContain(planner.Plan(rows), p => p.CanApply);
        var valid = Entry(4, "正确 [name]");
        var result = planner.Plan([.. rows, valid], new HashSet<int> { 0, 2, 3 });
        Assert.Equal(3, result.Count);
        Assert.Equal(0, Assert.Single(result, p => p.CanApply).Target.Id);
        Assert.Contains(result, p => p.SkipReason == "共享译文存在冲突");
        Assert.Contains(result, p => p.SkipReason == "复杂或不可编辑条目");
    }

    [Fact]
    public async Task 大项目计算期间编辑不会发布过期质量结果且关闭后不再通知()
    {
        var tl = "translate schinese strings:\n" + string.Concat(Enumerable.Range(0, 3000)
            .Select(i => $"    old \"Text {i}\"\n    new \"\"\n"));
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n", tl);
        var session = CreateTranslationSession(project.Root);
        var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.ViewMode = TranslationViewMode.Strings;
        await workspace.PendingResults;
        workspace.SelectedItem = workspace.VisibleItems[0];
        workspace.ViewMode = TranslationViewMode.Quality;
        workspace.Inspector.TranslationText = "刚输入的译文";
        await workspace.PendingResults;
        Assert.True(workspace.NeedsResultsRefresh);
        workspace.RefreshResultsCommand.Execute(null);
        await workspace.PendingResults;
        await workspace.PendingResults;
        Assert.DoesNotContain(workspace.VisibleItems, i => i.SharedString?.Translation == "刚输入的译文");
        workspace.SearchText = "Text 200";
        workspace.Dispose();
        var notifications = 0;
        workspace.PropertyChanged += (_, _) => notifications++;
        await workspace.PendingResults;
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task 批量应用和撤销沿用自动保存且不绕过文件基线()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        using var coordinator = new WorkspaceTaskCoordinator(ex => throw ex);
        var fs = new FileSystemService();
        var session = new ProjectSessionViewModel(new ProjectAnalysisService(fs), new ProjectCatalogService(fs),
            new ProjectWriter(fs), new NullFileDialogService(), new AcceptConfirmationService(),
            new AppSettingsStore(Path.Combine(project.Root, "settings.json")), new FixedThemeService(), new TaskCenterViewModel(), coordinator)
        { ProjectPath = project.Root, Language = "schinese", AutoSaveEnabled = true };
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        UniqueDonors(session.Snapshot!);
        var plan = await workspace.PreviewBulkAsync(true, CancellationToken.None);
        Assert.True(workspace.ApplyBulk(plan.Where(p => p.CanApply).ToArray()));
        Assert.True(await session.FlushAutoSaveAsync(CancellationToken.None));
        Assert.False(session.HasUnsavedChanges);
        Assert.True(workspace.UndoBulk());
        // 恢复为空会触发占位符警告，自动保存应保持原有阻断策略。
        Assert.False(await session.FlushAutoSaveAsync(CancellationToken.None));
        Assert.True(session.HasUnsavedChanges);
        await session.SaveAsync(false);
        var disk = await TestFiles.AnalyzeAsync(project.Root);
        Assert.Equal("", disk.TranslationUnits.Single(u => u.Identifier == "start_4").TranslationText);
    }

    [Fact]
    public async Task 大项目导航等待新投影并聚焦正确目标()
    {
        var source = "label start:\n" + string.Concat(Enumerable.Range(0, 2200).Select(i => $"    e \"Line {i}\"\n"));
        var tl = string.Concat(Enumerable.Range(0, 2200).Select(i => $"# game/script.rpy:{i + 2}\ntranslate schinese line_{i}:\n    # e \"Line {i}\"\n    e \"\"\n"));
        await using var project = await TestFiles.CreateProjectAsync(source, tl);
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        await workspace.PendingResults;
        workspace.SearchText = "Line 1";
        await workspace.PendingResults;
        var node = session.Snapshot!.Graph.Nodes.Single(n => n.OriginalText == "Line 2199");
        Assert.True(workspace.Navigate(new NavigateToSourceRequest(node.Id, null, null, null)));
        await workspace.PendingResults;
        Assert.Equal(node.Id, workspace.SelectedItem?.Key);
        Assert.Equal("", workspace.SearchText);
    }

}
