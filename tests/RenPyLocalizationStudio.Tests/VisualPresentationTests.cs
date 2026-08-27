using ICSharpCode.AvalonEdit;
using RenPyLocalizationStudio.App.Behaviors;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class VisualPresentationTests
{
    [Theory]
    [InlineData("#D16BA5", 0xD1, 0x6B, 0xA5)]
    [InlineData("#00ff7F", 0x00, 0xFF, 0x7F)]
    public void Theme_只接受六位Hex并正确解析(string text, int red, int green, int blue)
    {
        Assert.True(ThemeService.TryParseAccentColor(text, out var color));
        Assert.Equal(red, color.R);
        Assert.Equal(green, color.G);
        Assert.Equal(blue, color.B);
    }

    [Theory]
    [InlineData("Red")]
    [InlineData("#FFF")]
    [InlineData("#ZZZZZZ")]
    [InlineData("")]
    public void Theme_拒绝非RrgGbb格式(string text) =>
        Assert.False(ThemeService.TryParseAccentColor(text, out _));

    [Fact]
    public void Diagnostics_同时按严重度与全文筛选()
    {
        var error = ContentItem.FromDiagnostic(new Diagnostic(DiagnosticSeverity.Error, "BROKEN", "文件损坏", "game/a.rpy", 3));
        var warning = ContentItem.FromDiagnostic(new Diagnostic(DiagnosticSeverity.Warning, "MISSING", "缺少译文", "game/b.rpy", 5));

        var filtered = DiagnosticPresentationFilter.Apply([error, warning], nameof(DiagnosticSeverity.Warning), "b.rpy").ToArray();

        Assert.Single(filtered);
        Assert.Same(warning, filtered[0]);
    }

    [Fact]
    public void CodeEditor_Tab在空行插入四个空格且Caret保持有效()
    {
        Exception? captured = null;
        string? text = null;
        var caret = -1;
        var thread = new Thread(() =>
        {
            try
            {
                var editor = new TextEditor { Text = string.Empty };
                editor.CaretOffset = 0;
                RenPyCodeEditorBehavior.ApplyTab(editor, false);
                text = editor.Text;
                caret = editor.CaretOffset;
            }
            catch (Exception exception) { captured = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(captured);
        Assert.Equal("    ", text);
        Assert.Equal(4, caret);
    }

    [Fact]
    public void TranslationNavigation_随路径文件与Label投影切换内容()
    {
        var snapshot = new ProjectSnapshot { ProjectRoot = "X", GameDirectory = "X/game", Language = "schinese" };
        var start = new FlowNode { Id = "start", Kind = FlowNodeKind.Label, DisplayText = "label start", LabelName = "start", Region = new SourceRegion("game/a.rpy", 1, 1), Indent = 0 };
        var room = new FlowNode { Id = "room", Kind = FlowNodeKind.Label, DisplayText = "label room", LabelName = "room", Region = new SourceRegion("game/b.rpy", 1, 1), Indent = 0 };
        var jump = new FlowNode { Id = "jump", Kind = FlowNodeKind.Jump, DisplayText = "jump room", Target = "room", Region = new SourceRegion("game/a.rpy", 2, 2), Indent = 4 };
        snapshot.Graph.Nodes.AddRange([start, jump, room]);
        snapshot.Graph.Labels["start"] = start;
        snapshot.Graph.Labels["room"] = room;

        var paths = TranslationNavigationBuilder.Build(snapshot, FlowGroupingMode.StoryPath);
        var files = TranslationNavigationBuilder.Build(snapshot, FlowGroupingMode.SourceFile);
        var labels = TranslationNavigationBuilder.Build(snapshot, FlowGroupingMode.Label);

        Assert.Single(paths);
        Assert.Equal("start", paths[0].Name);
        Assert.Equal(["a.rpy", "b.rpy"], files.Select(item => item.Name));
        Assert.Equal(["start", "room"], labels.Select(item => item.Name));
        Assert.All(files, item => Assert.StartsWith("group:SourceFile:", item.NodeId));
    }

    [Fact]
    public void TranslationNavigation_书签只显示稳定节点并保持源码顺序()
    {
        var snapshot = new ProjectSnapshot { ProjectRoot = "X", GameDirectory = "X/game", Language = "schinese" };
        var first = new FlowNode { Id = "first", Kind = FlowNodeKind.Dialogue, DisplayText = "first", OriginalText = "first", Region = new SourceRegion("game/a.rpy", 2, 2), Indent = 0 };
        var second = new FlowNode { Id = "second", Kind = FlowNodeKind.Jump, DisplayText = "jump second", Target = "second", Region = new SourceRegion("game/a.rpy", 3, 3), Indent = 0 };
        var ignored = new FlowNode { Id = "ignored", Kind = FlowNodeKind.Dialogue, DisplayText = "ignored", Region = new SourceRegion("game/a.rpy", 4, 4), Indent = 0 };
        snapshot.Graph.Nodes.AddRange([first, second, ignored]);

        var bookmarks = TranslationNavigationBuilder.BuildBookmarks(snapshot, ["second", "first", "missing"]);

        Assert.Equal(["first", "jump second"], bookmarks.Select(item => item.Name));
        Assert.Equal(["first", "second"], bookmarks.Select(item => item.NodeId));
        Assert.Contains("书签 · game/a.rpy:3", bookmarks[1].Location);
    }

    [Fact]
    public void ContentItem_书签状态可独立切换()
    {
        var item = ContentItem.FromFlow(DialogueNode("uv", "正文"), null, null, 0);

        item.SetBookmarked(true);
        Assert.True(item.IsBookmarked);

        item.SetBookmarked(false);
        Assert.False(item.IsBookmarked);
    }

    [Fact]
    public void TranslationWorkspace_首次切换书签为添加再次切换为移除()
    {
        var bookmarks = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(TranslationWorkspaceViewModel.ToggleBookmarkState(bookmarks, "node"));
        Assert.Contains("node", bookmarks);
        Assert.False(TranslationWorkspaceViewModel.ToggleBookmarkState(bookmarks, "node"));
        Assert.Empty(bookmarks);
    }

    [Fact]
    public void ProjectSession_快照仅能用于当前项目和语言()
    {
        var snapshot = new ProjectSnapshot
        {
            ProjectRoot = @"E:\GameA",
            GameDirectory = @"E:\GameA\game",
            Language = "schinese"
        };

        Assert.True(ProjectSessionViewModel.IsSnapshotForScope(snapshot, @"E:\GameA", "schinese"));
        Assert.False(ProjectSessionViewModel.IsSnapshotForScope(snapshot, @"E:\GameB", "schinese"));
        Assert.False(ProjectSessionViewModel.IsSnapshotForScope(snapshot, @"E:\GameA", "english"));
    }

    [Fact]
    public void TranslationNavigation_向下遇到Jump时定位目标Label()
    {
        var jump = new FlowNode
        {
            Id = "jump",
            Kind = FlowNodeKind.Jump,
            DisplayText = "jump target",
            Target = "target",
            Region = new SourceRegion("game/a.rpy", 3, 3),
            Indent = 0
        };
        var target = new FlowNode
        {
            Id = "target",
            Kind = FlowNodeKind.Label,
            LabelName = "target",
            DisplayText = "label target",
            Region = new SourceRegion("game/a.rpy", 8, 8),
            Indent = 0
        };
        var items = new[]
        {
            ContentItem.FromFlow(jump, null, null, 0),
            ContentItem.FromFlow(target, null, null, 0)
        };
        var graph = new FlowGraph();
        graph.Nodes.AddRange([jump, target]);
        graph.Labels["target"] = target;

        var resolved = TranslationWorkspaceViewModel.TryResolveJumpTargetIndex(items, graph, 0, out var targetIndex, out var targetLabel);

        Assert.True(resolved);
        Assert.Equal(1, targetIndex);
        Assert.Equal("target", targetLabel);
    }

    [Fact]
    public void ContentItem_分离说话人与正文并隐藏正常绑定状态()
    {
        var node = DialogueNode("uv", "正文: 保留冒号");
        var unit = Unit(node, "译文");

        var item = ContentItem.FromFlow(node, unit, null, 0);

        Assert.Equal("uv", item.SpeakerText);
        Assert.Equal("uv: ", item.SpeakerPrefix);
        Assert.Equal("正文: 保留冒号", item.BodyText);
        Assert.Equal(TranslationStatusKind.Bound, item.StatusKind);
        Assert.False(item.IsStatusVisible);
        Assert.True(item.IsTranslationComplete);
    }

    [Fact]
    public void ContentItem_缺少New与冲突保持显眼状态()
    {
        var missing = Unit(null, string.Empty, missingNew: true);
        var missingItem = ContentItem.FromTranslation(missing);
        var shared = new SharedStringEntry { Language = "schinese", OldText = "Start", HasConflict = true };
        var conflictItem = ContentItem.FromSharedString(shared);

        Assert.Equal(TranslationStatusKind.Missing, missingItem.StatusKind);
        Assert.True(missingItem.IsStatusVisible);
        Assert.Equal("缺少 new", missingItem.Badge);
        Assert.Equal(TranslationStatusKind.Conflict, conflictItem.StatusKind);
        Assert.True(conflictItem.IsStatusVisible);
    }

    [Fact]
    public void Coverage_只统计可编辑条目且空译文不算完成()
    {
        var node = DialogueNode("uv", "正文");
        var completed = ContentItem.FromFlow(node, Unit(node, "完成"), null, 0);
        var empty = ContentItem.FromFlow(DialogueNode(null, "空"), Unit(node, string.Empty), null, 0);
        var label = ContentItem.FromFlow(new FlowNode
        {
            Id = "label",
            Kind = FlowNodeKind.Label,
            DisplayText = "label start",
            Region = new SourceRegion("game/script.rpy", 1, 1),
            LabelName = "start",
            Indent = 0
        }, null, null, 0);

        var coverage = TranslationCoverageCalculator.Calculate([completed, empty, label]);

        Assert.Equal(2, coverage.EditableCount);
        Assert.Equal(1, coverage.TranslatedCount);
        Assert.Equal(50, coverage.Percentage);
    }

    [Fact]
    public void ContentItem_缓存投影仍能搜索最新译文()
    {
        var node = DialogueNode("uv", "Source");
        var unit = Unit(node, "旧译文");
        var item = ContentItem.FromFlow(node, unit, null, 0);

        unit.TranslationText = "实时更新的译文";

        Assert.Contains("实时更新的译文", item.SearchText);
    }

    [Fact]
    public void Inspector_空流程与编辑状态使用不同状态对象()
    {
        var inspector = new TranslationInspectorViewModel(new TaskCenterViewModel());
        Assert.IsType<EmptyInspectorStateViewModel>(inspector.CurrentInspectorState);

        inspector.SetTarget(null, "jump target", "game/script.rpy:12");
        Assert.IsType<FlowInspectorStateViewModel>(inspector.CurrentInspectorState);

        inspector.SetTarget(EditorTarget.ForUnit(Unit(null, "译文")));
        Assert.IsType<TranslationEditorInspectorStateViewModel>(inspector.CurrentInspectorState);
    }

    private static FlowNode DialogueNode(string? speaker, string text) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = FlowNodeKind.Dialogue,
        DisplayText = speaker is null ? text : $"{speaker}: {text}",
        Speaker = speaker,
        OriginalText = text,
        Region = new SourceRegion("game/script.rpy", 2, 2),
        Indent = 4
    };

    private static TranslationUnit Unit(FlowNode? node, string translation, bool missingNew = false) => new()
    {
        Kind = missingNew ? TranslationUnitKind.String : TranslationUnitKind.Dialogue,
        Language = "schinese",
        FilePath = "E:\\game\\tl\\schinese\\script.rpy",
        RelativeTlPath = "script.rpy",
        BlockSpan = new TextSpan(0, 1),
        HeaderLine = 1,
        TranslationText = translation,
        TranslationValueSpan = missingNew ? null : new TextSpan(0, translation.Length),
        MissingNew = missingNew,
        BoundNode = node
    };
}
