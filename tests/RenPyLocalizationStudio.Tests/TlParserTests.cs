using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class TlParserTests
{
    [Fact]
    public void Parse_兼容空译文缺失New和用户注释()
    {
        const string tl = """
# game/script.rpy:2
translate schinese start_a1:
    # narrator "Hello"
    # 用户在块内的注释
    narrator ""

translate schinese strings:
    # game/script.rpy:4
    old "Choice"
    # 中间注释
    new ""

    # game/script.rpy:6
    old "Missing"
    # 暂时没有 new
""";
        var document = new TlParser().Parse(TestFiles.InMemory(tl), "script.rpy", "schinese");

        Assert.Equal(3, document.Units.Count);
        Assert.Equal(string.Empty, document.Units[0].TranslationText);
        Assert.False(document.Units[1].MissingNew);
        Assert.True(document.Units[2].MissingNew);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "TL_MISSING_NEW");
    }

    [Fact]
    public void Parse_多语句翻译块进入原始块模式()
    {
        const string tl = """
# game/script.rpy:2
translate schinese start_complex:
    # e "Source"
    if flag:
        e "甲"
    else:
        e "乙"
""";

        var unit = Assert.Single(new TlParser().Parse(TestFiles.InMemory(tl), "script.rpy", "schinese").Units);

        Assert.True(unit.IsRawMode);
        Assert.Contains("if flag:", unit.RawBodyText);
    }

    [Fact]
    public void Parse_受管注释不会让源码行号串到下一个翻译块()
    {
        const string tl = """
# game/script.rpy:23
# RFT-FLOW-BEGIN
# [流程] game/script.rpy:1 label：start
# RFT-FLOW-END
translate schinese start_time:
    # "Time"
    "时间"

# game/script.rpy:26
translate schinese start_dialogue:
    # MC "There she is."
    MC "她就在那里。"
""";

        var units = new TlParser().Parse(TestFiles.InMemory(tl), "script.rpy", "schinese").Units;

        Assert.Equal(2, units.Count);
        Assert.Equal(23, units[0].SourceLine);
        Assert.Equal(26, units[1].SourceLine);
    }
}
