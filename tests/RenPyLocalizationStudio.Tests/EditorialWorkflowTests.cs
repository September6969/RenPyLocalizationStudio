using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed partial class ProjectSessionViewModelTests
{
    [Fact]
    public void 术语检查避免英文子串误报并识别禁用译名()
    {
        GlossaryTerm[] terms = [new("Ann", "安", "安妮"), new("Crystal City", "水晶城")];
        Assert.Empty(GlossaryChecker.Check("Annette is here", "安妮来了", terms));
        var issues = GlossaryChecker.Check("Ann visited Crystal City", "安妮来到了城里", terms);
        Assert.Contains(issues, d => d.Code == "GLOSSARY_FORBIDDEN");
        Assert.Contains(issues, d => d.Message.Contains("水晶城"));
        Assert.Empty(GlossaryChecker.Check("Ann", "", terms));
    }

    [Fact]
    public async Task 术语持久化并进入编辑提示与质量检查()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        await workspace.SaveGlossaryAsync([new("Hello", "你好", "您好")]);
        workspace.SelectedItem = workspace.VisibleItems.Single(i => i.Unit?.Identifier == "start_3");
        Assert.Contains("禁用", workspace.Inspector.EditorialHint);
        workspace.ViewMode = TranslationViewMode.Quality;
        Assert.Contains(workspace.VisibleItems, i => i.Subtitle.Contains("禁用译名"));
        var reopened = CreateTranslationSession(project.Root);
        using var restored = new TranslationWorkspaceViewModel(reopened, new EmptyPreviewService());
        await reopened.InitializeAsync(); await reopened.AnalyzeAsync();
        Assert.Equal("你好", Assert.Single(restored.Glossary).Preferred);
    }

    [Fact]
    public async Task 校对记录修改后退回待审且备注持久保存并隔离语言()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.SelectedItem = workspace.VisibleItems.Single(i => i.Unit?.Identifier == "start_1");
        workspace.SetReview(ReviewStatus.Reviewed, "人名已确认");
        Assert.Equal("已校对", workspace.SelectedReviewText);
        workspace.ReviewFilter = ReviewStatus.Reviewed;
        Assert.Single(workspace.VisibleItems);
        workspace.Inspector.TranslationText = "人工修订 [name]";
        Assert.Equal("待校对", workspace.SelectedReviewText);
        workspace.Inspector.TranslationText = "你好 [name]";
        Assert.Equal("待校对", workspace.SelectedReviewText);
        Assert.Equal("人名已确认", workspace.SelectedReviewNote);
        await workspace.SaveEditorialSettingsAsync();
        workspace.RefreshResultsCommand.Execute(null);
        Assert.Empty(workspace.VisibleItems);
        await workspace.SaveGlossaryAsync([new("Hello", "你好")]);
        session.Language = "english";
        Assert.Empty(workspace.Glossary);
        Assert.Null(workspace.ReviewFilter);
    }

    [Fact]
    public async Task 高级搜索按原文译文位置区分并拒绝无效正则()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.SearchField = TranslationSearchField.Original;
        workspace.SearchText = "你好"; Assert.Empty(workspace.VisibleItems);
        workspace.SearchField = TranslationSearchField.Translation; Assert.Equal(2, workspace.VisibleItems.Count);
        workspace.SearchField = TranslationSearchField.Original; workspace.SearchText = "hello";
        Assert.Equal(4, workspace.VisibleItems.Count);
        workspace.SearchCaseSensitive = true; Assert.Empty(workspace.VisibleItems);
        workspace.SearchRegex = true; workspace.SearchText = "^Hello"; Assert.Equal(4, workspace.VisibleItems.Count);
        await workspace.SaveEditorialSettingsAsync();
        var reopened = CreateTranslationSession(project.Root);
        using var restored = new TranslationWorkspaceViewModel(reopened, new EmptyPreviewService());
        await reopened.InitializeAsync(); await reopened.AnalyzeAsync();
        Assert.Equal(TranslationSearchField.Original, restored.SearchField);
        Assert.True(restored.SearchCaseSensitive); Assert.True(restored.SearchRegex);
        Assert.Equal(4, restored.VisibleItems.Count);
        workspace.SearchText = "["; Assert.NotEmpty(workspace.SearchError);
        workspace.ClearSearchCommand.Execute(null); Assert.Empty(workspace.SearchError);
    }

    [Fact]
    public void 校对表完整往返引号逗号换行和公式字符且拒绝坏表()
    {
        var row = new ReviewExchangeRow("schinese", "key", "=SUM(A1)\n\"quote\",", "'old", "+新译文\n继续", "需讨论", "备注\r\n第二行");
        var encoded = ReviewCsv.Write([row]);
        Assert.Contains("\"'=SUM", encoded);
        Assert.Equal(row, Assert.Single(ReviewCsv.Read(encoded)));
        Assert.Throws<InvalidDataException>(() => ReviewCsv.Read("a,b\n1,2"));
        Assert.Throws<InvalidDataException>(() => ReviewCsv.Read("Language,Key,Original,Baseline,Translation,Status,Note\n\"bad"));
    }

    [Fact]
    public async Task 校对回填核对基线并能回填仅有备注的修改()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        var exported = workspace.ExportReviewRows();
        var incoming = exported.Select(row => row with { Status = "已校对", Note = "确认语气" }).ToArray();
        var preview = await workspace.PreviewEditorialAsync("import", "", "", new(), incoming, true, CancellationToken.None);
        Assert.Equal(4, preview.Count(p => p.CanApply));
        Assert.True(workspace.ApplyEditorial(preview));
        workspace.SelectedItem = workspace.VisibleItems.Single(i => i.Unit?.Identifier == "start_1");
        Assert.Equal("已校对", workspace.SelectedReviewText); Assert.Equal("确认语气", workspace.SelectedReviewNote);
        Assert.True(workspace.UndoBulk());
        Assert.Equal("待校对", workspace.SelectedReviewText); Assert.Equal("", workspace.SelectedReviewNote);
        workspace.Inspector.TranslationText = "当前已改 [name]";
        var stale = await workspace.PreviewEditorialAsync("import", "", "", new(), incoming, true, CancellationToken.None);
        Assert.Contains(stale, p => p.Target.Unit?.Identifier == "start_1" && p.SkipReason == "当前译文与导出基线不一致");
    }

    [Fact]
    public async Task 重复标识和原文变化的校对行不能回填()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var snapshot = await TestFiles.AnalyzeAsync(project.Root); var rows = TranslationReadSnapshot.Capture(snapshot);
        var target = rows[0];
        var input = new ReviewExchangeRow("schinese", TranslationIdentity.Key(target), target.Original!, target.Translation, "改译 [name]", "", "");
        Assert.Contains(TranslationEditPlanner.Import(rows, [input, input], "schinese"), p => p.SkipReason == "表中条目标识重复");
        Assert.Contains(TranslationEditPlanner.Import(rows, [input with { Original = "Changed" }], "schinese"), p => p.SkipReason == "原文已变化");
        Assert.Contains(TranslationEditPlanner.Import(rows, [input with { Language = "french" }], "schinese"), p => p.SkipReason == "目标语言不一致");
    }

    [Fact]
    public async Task 批量替换预览保护占位符且可整体撤销()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        var unsafePlan = await workspace.PreviewEditorialAsync("replace", "[name]", "", new(), null, true, CancellationToken.None);
        Assert.DoesNotContain(unsafePlan, p => p.CanApply);
        var plan = await workspace.PreviewEditorialAsync("replace", "你好", "欢迎", new(), null, true, CancellationToken.None);
        var selected = plan.Where(p => p.CanApply).ToArray(); Assert.Equal(2, selected.Length);
        Assert.True(workspace.ApplyEditorial(selected));
        Assert.Equal(2, session.Snapshot!.TranslationUnits.Count(u => u.TranslationText == "欢迎 [name]"));
        Assert.True(workspace.UndoBulk());
        Assert.Equal(2, session.Snapshot.TranslationUnits.Count(u => u.TranslationText == "你好 [name]"));
    }

    [Fact]
    public async Task 迁移只向空译文提供候选且重复目标必须人工选一种()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        ReviewExchangeRow[] csv = [new("schinese", "old1", "Hello [name]!", "", "旧译法 [name]", "", ""), new("schinese", "old2", "Hello [name].", "", "另一译法 [name]", "", "")];
        var plan = await workspace.PreviewEditorialAsync("migrate", "", "", new(), csv, true, CancellationToken.None);
        Assert.Equal(2, plan.Count(p => p.CanApply));
        Assert.All(plan, p => Assert.Equal("start_4", p.Target.Unit?.Identifier));
        Assert.Throws<InvalidOperationException>(() => workspace.ApplyEditorial(plan));
        Assert.True(workspace.ApplyEditorial([plan[0]]));
        Assert.Contains("旧原文", plan[0].Source);
    }

    [Fact]
    public async Task 外部对比识别双方修改并保存时保留外部其他内容()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.SelectedItem = workspace.VisibleItems.Single(i => i.Unit?.Identifier == "start_1");
        workspace.Inspector.TranslationText = "本地 [name]";
        var original = await File.ReadAllTextAsync(project.TlPath);
        await File.WriteAllTextAsync(project.TlPath, original.Replace("你好 [name]", "磁盘 [name]") + "\n# 保留外部注释\n", new System.Text.UTF8Encoding(true));
        var rows = await workspace.PreviewExternalAsync(CancellationToken.None);
        var both = Assert.Single(rows, r => r.Local?.Unit?.Identifier == "start_1");
        Assert.Equal(MergeChoice.Unresolved, both.DefaultChoice);
        Assert.True(both.AllowLocal);
        var choices = rows.ToDictionary(r => r.Key, r => r.Local?.Unit?.Identifier == "start_1" ? MergeChoice.Local : MergeChoice.Disk);
        await workspace.ApplyExternalAsync(choices, CancellationToken.None);
        Assert.Equal("本地 [name]", session.Snapshot!.TranslationUnits.Single(u => u.Identifier == "start_1").TranslationText);
        Assert.Equal("磁盘 [name]", session.Snapshot.TranslationUnits.Single(u => u.Identifier == "start_2").TranslationText);
        await session.SaveAsync(false);
        var saved = await File.ReadAllTextAsync(project.TlPath);
        Assert.Contains("本地 [name]", saved); Assert.Contains("磁盘 [name]", saved); Assert.Contains("保留外部注释", saved);
    }

    [Fact]
    public async Task 外部对比后新增文件或再次编辑会拒绝采用()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        var rows = await workspace.PreviewExternalAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(project.Root, "game", "new.rpy"), "label new_label:\n    return\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ApplyExternalAsync(rows.ToDictionary(r => r.Key, r => MergeChoice.Disk), CancellationToken.None));
        rows = await workspace.PreviewExternalAsync(CancellationToken.None);
        workspace.SelectedItem = workspace.VisibleItems.Single(i => i.Unit?.Identifier == "start_1");
        workspace.Inspector.TranslationText = "再次编辑 [name]";
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ApplyExternalAsync(rows.ToDictionary(r => r.Key, r => MergeChoice.Disk), CancellationToken.None));
    }

    [Fact]
    public async Task 外部对比拒绝无法完整读取的磁盘项目()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        await File.WriteAllBytesAsync(Path.Combine(project.Root, "game", "invalid.rpy"), [0xff, 0xff]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.PreviewExternalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 共享译文三方对比能保留单方修改并阻止危险取舍()
    {
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n", "translate schinese strings:\n    old \"Continue\"\n    new \"继续\"\n");
        var local = await TestFiles.AnalyzeAsync(project.Root);
        var baseline = ExternalTranslationMerge.Baselines(local);
        local.SharedStrings.Single().Translation = "继续游戏";
        var disk = await TestFiles.AnalyzeAsync(project.Root);
        var left = ExternalTranslationMerge.Capture(local); var right = ExternalTranslationMerge.Capture(disk);
        var row = Assert.Single(ExternalTranslationMerge.Plan(left, right, baseline));
        Assert.True(row.AllowLocal); Assert.Equal(MergeChoice.Local, row.DefaultChoice);
        row = Assert.Single(ExternalTranslationMerge.Plan(left, [], baseline));
        Assert.False(row.AllowLocal); Assert.Equal(MergeChoice.Unresolved, row.DefaultChoice);
        row = Assert.Single(ExternalTranslationMerge.Plan(left, [right[0] with { Raw = true, Translation = "复杂块" }], baseline));
        Assert.False(row.AllowLocal);
    }

    [Fact]
    public async Task 当前范围替换会刷新已过期筛选并且撤销保留原校对备注()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        workspace.SelectedItem = workspace.VisibleItems.Single(i => i.Unit?.Identifier == "start_1");
        workspace.SetReview(ReviewStatus.Reviewed, "已确认");
        workspace.ReviewFilter = ReviewStatus.Reviewed;
        workspace.SetReview(ReviewStatus.Pending, "待改");
        var stale = await workspace.PreviewEditorialAsync("replace", "你好", "欢迎", new(), null, false, CancellationToken.None);
        Assert.Empty(stale);
        workspace.ReviewFilter = null;
        workspace.SelectedItem = workspace.VisibleItems.Single(i => i.Unit?.Identifier == "start_1");
        workspace.SetReview(ReviewStatus.Reviewed, "已确认");
        var plan = await workspace.PreviewEditorialAsync("replace", "你好", "欢迎", new(), null, true, CancellationToken.None);
        Assert.True(workspace.ApplyEditorial(plan.Where(p => p.CanApply).ToArray()));
        Assert.Equal("待校对", workspace.SelectedReviewText);
        Assert.True(workspace.UndoBulk());
        Assert.Equal("已校对", workspace.SelectedReviewText); Assert.Equal("已确认", workspace.SelectedReviewNote);
    }

    [Fact]
    public void 搜索正则有超时且取消迁移不会计算候选()
    {
        var search = new TranslationSearch("(a+)+$", new(RegularExpression: true));
        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() => search.Matches(new string('a', 100000) + "!", "", "", ""));
        Assert.Throws<OperationCanceledException>(() => TranslationEditPlanner.Migrate([], [], "schinese", new CancellationToken(true)));
    }
}
