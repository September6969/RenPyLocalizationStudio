using System.Text;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class ProjectWriterTests
{
    private const string Source = """
label start:
    "Hello, [player_name]!"
    menu:
        "Choice":
            jump next

label next:
    return
""";

    private const string Tl = """"
# game/script.rpy:2
translate schinese start_a1:

    # narrator "Hello, [player_name]!"
    # 用户注释必须保留
    narrator "Hello, [player_name]!"

translate schinese strings:

    # game/script.rpy:4
    old "Choice"
    # 奇怪的用户注释
    new ""

translate schinese python:
    sample = """
# RFT-FLOW-BEGIN
# RFT-FLOW-END
    """
"""";

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\r\n")]
    public async Task Save_继承Bom换行保留注释并保持受管注释幂等(bool bom, string newLine)
    {
        await using var project = await TestFiles.CreateProjectAsync(Source, Tl, bom, newLine);
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        snapshot.SharedStrings.Single().Translation = "选择";

        var first = await TestFiles.SaveAsync(snapshot, true, true);
        Assert.Equal(1, first.Value?.SavedFiles);
        var firstBytes = await File.ReadAllBytesAsync(project.TlPath);
        Assert.Equal(bom, firstBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var firstText = await File.ReadAllTextAsync(project.TlPath, new UTF8Encoding(bom));
        Assert.Contains("# 用户注释必须保留", firstText);
        Assert.Contains("# 奇怪的用户注释", firstText);
        Assert.Contains("sample = \"\"\"\n# RFT-FLOW-BEGIN\n# RFT-FLOW-END", firstText.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("new \"选择\"", firstText);
        Assert.Equal(firstText.Contains("\r\n", StringComparison.Ordinal), newLine == "\r\n");

        var refreshed = await TestFiles.AnalyzeAsync(project.Root);
        var second = await TestFiles.SaveAsync(refreshed, true, true);
        Assert.Equal(1, second.Value?.SavedFiles);
        var secondText = await File.ReadAllTextAsync(project.TlPath, new UTF8Encoding(bom));
        Assert.Equal(firstText, secondText);
        Assert.True(File.Exists(project.TlPath + ".rls.bak"));
    }

    [Fact]
    public async Task Save_外部修改时拒绝覆盖()
    {
        await using var project = await TestFiles.CreateProjectAsync(Source, Tl);
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        snapshot.SharedStrings.Single().Translation = "选择";
        await File.AppendAllTextAsync(project.TlPath, "\n# 外部修改\n", new UTF8Encoding(false));

        var result = await TestFiles.SaveAsync(snapshot, false, true);

        Assert.Equal(0, result.Value?.SavedFiles ?? 0);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "EXTERNAL_FILE_CHANGE");
    }

    [Fact]
    public async Task Save_强制覆盖外部修改时保留备份且仍写入译文()
    {
        await using var project = await TestFiles.CreateProjectAsync(Source, Tl);
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        snapshot.SharedStrings.Single().Translation = "选择";
        await File.AppendAllTextAsync(project.TlPath, "\n# 外部修改\n", new UTF8Encoding(false));

        var result = await TestFiles.SaveAsync(snapshot, false, true, forceOverwrite: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value?.SavedFiles);
        var saved = await File.ReadAllTextAsync(project.TlPath, new UTF8Encoding(false));
        var backup = await File.ReadAllTextAsync(project.TlPath + ".rls.bak", new UTF8Encoding(false));
        Assert.Contains("new \"选择\"", saved);
        Assert.Contains("# 外部修改", backup);
    }

    [Fact]
    public async Task Save_原始块模式只替换块体并保留Translate头()
    {
        const string source = """
label start:
    "Source"
""";
        const string tl = """
# game/script.rpy:2
translate schinese start_complex:
    # "Source"
    if flag:
        "甲"
    else:
        "乙"
""";
        await using var project = await TestFiles.CreateProjectAsync(source, tl);
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var unit = Assert.Single(snapshot.TranslationUnits);
        Assert.True(unit.IsRawMode);
        unit.RawBodyText = unit.RawBodyText.Replace("甲", "左", StringComparison.Ordinal).Replace("乙", "右", StringComparison.Ordinal);
        unit.IsDirty = true;

        var result = await TestFiles.SaveAsync(snapshot, false, true);
        var saved = await File.ReadAllTextAsync(project.TlPath);

        Assert.Equal(1, result.Value?.SavedFiles);
        Assert.Contains("translate schinese start_complex:", saved);
        Assert.Contains("\"左\"", saved);
        Assert.Contains("\"右\"", saved);
    }
}
