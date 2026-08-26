using RenPyLocalizationStudio.Core;
using System.Text;

namespace RenPyLocalizationStudio.Tests;

public sealed class SourceParserTests
{
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
    public void Parse_十万节点不递归展开流程()
    {
        var source = new StringBuilder("label start:\n");
        for (var index = 0; index < 100_000; index++)
        {
            source.Append("    narrator \"").Append(index).Append("\"\n");
        }

        var document = new RenPySourceParser().Parse(TestFiles.InMemory(source.ToString()), "game/large.rpy");

        Assert.Equal(100_000, document.Graph.Nodes.Count(node => node.Kind == FlowNodeKind.Dialogue));
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
