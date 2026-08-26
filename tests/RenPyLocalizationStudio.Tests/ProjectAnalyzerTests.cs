using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class ProjectAnalyzerTests
{
    [Fact]
    public async Task Analyze_相同Old共享译文并覆盖Menu与Screen引用()
    {
        const string source = """
label start:
    menu:
        "确定":
            jump next

screen confirmation():
    textbutton _("确定")

label next:
    return
""";
        const string tl = """
translate schinese strings:
    # game/script.rpy:3
    old "确定"
    new "确认"
""";
        await using var project = await TestFiles.CreateProjectAsync(source, tl);
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);

        var entry = Assert.Single(snapshot.SharedStrings);
        Assert.Equal("确认", entry.Translation);
        Assert.Contains(entry.References, reference => reference.SourceKind == StringSourceKind.Menu);
        Assert.Contains(entry.References, reference => reference.SourceKind == StringSourceKind.Screen);
        entry.Translation = "好";
        Assert.All(entry.Definitions, definition => Assert.Equal("好", definition.TranslationText));
    }

    [Fact]
    public async Task Analyze_解析跨文件Jump但不连接跨文件自然落入()
    {
        const string source = """
label start:
    jump second
""";
        const string tl = """
translate schinese strings:
""";
        await using var project = await TestFiles.CreateProjectAsync(source, tl);
        var secondPath = Path.Combine(project.Root, "game", "second.rpy");
        await File.WriteAllTextAsync(secondPath, "label second:\n    return\n", new System.Text.UTF8Encoding(false));

        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var jump = Assert.Single(snapshot.Graph.Nodes, node => node.Kind == FlowNodeKind.Jump);
        var target = snapshot.Graph.Labels["second"];

        Assert.Contains(snapshot.Graph.Edges, edge => edge.FromId == jump.Id && edge.ToId == target.Id && edge.Kind == FlowEdgeKind.Jump);
        var firstEnd = Assert.Single(snapshot.Graph.Nodes, node => node.Kind == FlowNodeKind.EndOfFile && node.Region.RelativePath == "game/script.rpy");
        Assert.DoesNotContain(snapshot.Graph.Edges, edge => edge.FromId == firstEnd.Id && edge.ToId == target.Id);
    }

    [Fact]
    public async Task Analyze_字符串行号偏移时按唯一Old绑定菜单选项()
    {
        const string source = """
label start:
    menu:
        "Keep it friendly":
            return
""";
        const string tl = """
translate schinese strings:
    # game/script.rpy:4
    old "Keep it friendly"
    new "友好一点"
""";
        await using var project = await TestFiles.CreateProjectAsync(source, tl);

        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var choice = Assert.Single(snapshot.Graph.Nodes, node => node.Kind == FlowNodeKind.Choice);
        var unit = Assert.Single(snapshot.TranslationUnits);

        Assert.Same(choice, unit.BoundNode);
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.Code == "UNBOUND_TRANSLATION");
    }

    [Fact]
    public async Task Analyze_普通字符串不应被诊断为未绑定剧情翻译()
    {
        const string source = """
label start:
    return
""";
        const string tl = """
translate schinese strings:
    # game/script.rpy:1
    old "Timo"
    new "提莫"
""";
        await using var project = await TestFiles.CreateProjectAsync(source, tl);

        var snapshot = await TestFiles.AnalyzeAsync(project.Root);

        Assert.Single(snapshot.SharedStrings);
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.Code == "UNBOUND_TRANSLATION");
    }

    [Fact]
    public async Task Analyze_样式翻译块不应被诊断为未绑定剧情翻译()
    {
        const string source = """
label start:
    return
""";
        const string tl = """
translate schinese style endscene_stats is text:
    font "tl/schinese/fonts/font.ttf"
""";
        await using var project = await TestFiles.CreateProjectAsync(source, tl);

        var snapshot = await TestFiles.AnalyzeAsync(project.Root);

        Assert.DoesNotContain(snapshot.TranslationUnits, unit => unit.IsUnboundFlowTranslation);
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.Code == "UNBOUND_TRANSLATION");
    }
}
