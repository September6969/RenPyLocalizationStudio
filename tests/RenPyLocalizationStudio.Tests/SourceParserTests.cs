using System.Text;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class SourceParserTests
{
    [Fact]
    public void Parse_黄金夹具隔离三引号和Screen并保留动态跳转诊断()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "ParserEdgeCases.rpy");
        var source = File.ReadAllText(fixture, Encoding.UTF8);

        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source), "game/ParserEdgeCases.rpy");

        Assert.Contains("start", document.Graph.Labels.Keys);
        Assert.Contains("finish", document.Graph.Labels.Keys);
        Assert.DoesNotContain("fake_from_triple", document.Graph.Labels.Keys);
        Assert.Single(document.ScreenRegions);
        Assert.DoesNotContain(document.Graph.Nodes, node => node.OriginalText is "screen text" or "button");
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "DYNAMIC_TRANSFER");
        var dialogue = Assert.Single(document.Graph.Nodes, node => node.Kind == FlowNodeKind.Dialogue);
        Assert.Equal("narrator happy", dialogue.Speaker);
    }

    [Fact]
    public void Parse_隔离Python和Screen并识别常用控制流()
    {
        const string source = """"
label start:
    "1"
    $ fake = "jump hidden"
    python hide:
        payload = """ 
label fake_label:
jump fake_target
        """
        if True:
            pass
    screen fake_screen():
        text "界面"
        $ text = "label also_fake:"
    menu:
        "选项A" if enabled:
            "4"
            jump branch_a
        "选项B":
            "6"
            call branch_b
    return

label branch_a:
    "5"

label branch_b:
    "7"
"""";
        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source), "game/script.rpy");

        Assert.Contains("start", document.Graph.Labels.Keys);
        Assert.Contains("branch_a", document.Graph.Labels.Keys);
        Assert.Contains("branch_b", document.Graph.Labels.Keys);
        Assert.DoesNotContain("fake_label", document.Graph.Labels.Keys);
        Assert.Equal(2, document.Graph.Nodes.Count(node => node.Kind == FlowNodeKind.Choice));
        Assert.Contains(document.Graph.Nodes, node => node.Kind == FlowNodeKind.Jump && node.Target == "branch_a");
        Assert.Contains(document.Graph.Nodes, node => node.Kind == FlowNodeKind.Call && node.Target == "branch_b");
        Assert.Single(document.ScreenRegions);
        Assert.DoesNotContain(document.Graph.Nodes, node => node.OriginalText == "界面");
        Assert.Single(document.Graph.Nodes, node => node.Kind == FlowNodeKind.EndOfFile);
    }

    [Fact]
    public void Parse_动态跳转生成诊断而不执行()
    {
        const string source = """
label start:
    call screen confirmation
    jump expression destination
""";
        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source), "game/script.rpy");

        Assert.Contains(document.Graph.Nodes, node => node.Kind == FlowNodeKind.Unresolved && node.IsDynamic);
        Assert.DoesNotContain(document.Graph.Nodes, node => node.Target == "screen confirmation");
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "DYNAMIC_TRANSFER");
    }

    [Fact]
    public void Parse_说话人与正文使用语法捕获而不是冒号拆分()
    {
        const string source = """
label start:
    uv "时间: [clock] {i}ready{/i}"
    char.happy "路径 C:\\game: 仍属于正文"
    "旁白: 不应被识别为说话人"
""";

        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source), "game/dialogue.rpy");
        var dialogue = document.Graph.Nodes.Where(node => node.Kind == FlowNodeKind.Dialogue).ToArray();

        Assert.Equal("uv", dialogue[0].Speaker);
        Assert.Equal("时间: [clock] {i}ready{/i}", dialogue[0].OriginalText);
        Assert.Equal("char.happy", dialogue[1].Speaker);
        Assert.Contains(": 仍属于正文", dialogue[1].OriginalText);
        Assert.Null(dialogue[2].Speaker);
        Assert.Equal("旁白: 不应被识别为说话人", dialogue[2].OriginalText);
    }

    [Fact]
    public void Parse_支持带属性的Say并构建互斥分支与CallReturn继续点()
    {
        const string source = """
label start:
    e happy "Hello: world"
    if flag:
        "A"
    elif other:
        "B"
    else:
        "C"
    "After"
    call sub
    "Continue"

label sub:
    "Sub"
    return
""";

        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source), "game/script.rpy");
        var dialogue = document.Graph.Nodes.Single(node => node.OriginalText == "Hello: world");
        var branches = document.Graph.Nodes.Where(node => node.Kind == FlowNodeKind.Condition).ToArray();
        var after = document.Graph.Nodes.Single(node => node.OriginalText == "After");
        var continuation = document.Graph.Nodes.Single(node => node.OriginalText == "Continue");
        var call = Assert.Single(document.Graph.Nodes, node => node.Kind == FlowNodeKind.Call);
        var returnNode = Assert.Single(document.Graph.Nodes, node => node.Kind == FlowNodeKind.Return);

        Assert.Equal("e happy", dialogue.Speaker);
        Assert.Single(branches.Select(node => node.BranchGroupId).Distinct());
        Assert.DoesNotContain(document.Graph.Edges, edge => branches.Any(node => node.Id == edge.FromId) && branches.Any(node => node.Id == edge.ToId) && edge.Kind == FlowEdgeKind.Sequence);
        Assert.All(new[] { "A", "B", "C" }, text =>
        {
            var branchDialogue = document.Graph.Nodes.Single(node => node.OriginalText == text);
            Assert.Contains(document.Graph.Edges, edge => edge.FromId == branchDialogue.Id && edge.ToId == after.Id && edge.Kind == FlowEdgeKind.Sequence);
        });
        Assert.DoesNotContain(document.Graph.Edges, edge => edge.FromId == call.Id && edge.ToId == continuation.Id && edge.Kind == FlowEdgeKind.Sequence);
        Assert.Contains(document.Graph.Edges, edge => edge.FromId == returnNode.Id && edge.ToId == continuation.Id && edge.Kind == FlowEdgeKind.Return);
    }

    [Fact]
    public void Parse_十万节点不递归展开流程()
    {
        var source = new StringBuilder("label start:\n");
        for (var index = 0; index < 100_000; index++)
        {
            source.Append("    narrator \"").Append(index).Append("\"\n");
        }

        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source.ToString()), "game/large.rpy");
        var snapshot = new ProjectSnapshot { ProjectRoot = "X", GameDirectory = "X/game", Language = "schinese" };
        snapshot.Graph.Nodes.AddRange(document.Graph.Nodes);
        snapshot.Graph.Edges.AddRange(document.Graph.Edges);
        foreach (var label in document.Graph.Labels) snapshot.Graph.Labels[label.Key] = label.Value;
        var projection = FlowProjectionService.Project(snapshot, FlowGroupingMode.StoryPath);

        Assert.Equal(100_000, document.Graph.Nodes.Count(node => node.Kind == FlowNodeKind.Dialogue));
        Assert.Equal(document.Graph.Nodes.Count, projection.Count);
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Parse_菜单标题接入选项且非剧情块不会产生伪节点()
    {
        const string source = """
transform fake:
    if True:
        xalign 0.5

image fake:
    "fake.png"

layeredimage person:
    if True:
        "fake.png"

label start:
    menu:
        narrator "请选择"
        "左":
            "A"
        "右":
            "B"
    "After"
""";

        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source), "game/script.rpy");
        var menu = Assert.Single(document.Graph.Nodes, node => node.Kind == FlowNodeKind.Menu);
        var caption = Assert.Single(document.Graph.Nodes, node => node.OriginalText == "请选择");
        var choices = document.Graph.Nodes.Where(node => node.Kind == FlowNodeKind.Choice).ToArray();

        Assert.DoesNotContain(document.Graph.Nodes, node => node.Kind == FlowNodeKind.Condition);
        Assert.Contains(document.Graph.Edges, edge => edge.FromId == menu.Id && edge.ToId == caption.Id);
        Assert.All(choices, choice => Assert.Contains(document.Graph.Edges,
            edge => edge.FromId == caption.Id && edge.ToId == choice.Id && edge.Kind == FlowEdgeKind.Choice));
    }

    [Fact]
    public void Parse_重复Label诊断且正常结尾连接文件结束()
    {
        const string source = """
label start:
    "A"
label next:
    "B"
label next:
    return
""";

        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source), "game/script.rpy");
        var firstDialogue = Assert.Single(document.Graph.Nodes, node => node.OriginalText == "A");
        var firstNext = document.Graph.Labels["next"];

        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "DUPLICATE_LABEL");
        Assert.Contains(document.Graph.Edges, edge => edge.FromId == firstDialogue.Id && edge.ToId == firstNext.Id);
    }

    [Fact]
    public void StoryPath_所有分支先于公共汇合段显示()
    {
        const string source = """
label start:
    if flag:
        "A"
    else:
        "B"
    "After"
""";
        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source), "game/script.rpy");
        var snapshot = new ProjectSnapshot { ProjectRoot = "X", GameDirectory = "X/game", Language = "schinese" };
        snapshot.Graph.Nodes.AddRange(document.Graph.Nodes);
        snapshot.Graph.Edges.AddRange(document.Graph.Edges);
        foreach (var label in document.Graph.Labels) snapshot.Graph.Labels[label.Key] = label.Value;

        var rows = FlowProjectionService.Project(snapshot, FlowGroupingMode.StoryPath).ToList();

        Assert.True(rows.FindIndex(row => row.Node.OriginalText == "After") > rows.FindIndex(row => row.Node.OriginalText == "A"));
        Assert.True(rows.FindIndex(row => row.Node.OriginalText == "After") > rows.FindIndex(row => row.Node.OriginalText == "B"));
    }
}
