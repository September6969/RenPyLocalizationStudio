using System.Text;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

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
    public async Task Save_同一快照连续保存不会把自身写入误判为外部修改()
    {
        await using var project = await TestFiles.CreateProjectAsync(Source, Tl);
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var shared = Assert.Single(snapshot.SharedStrings);

        shared.Translation = "选择";
        var first = await TestFiles.SaveAsync(snapshot, false, true);
        Assert.True(first.IsSuccess);

        shared.Translation = "选项";
        var second = await TestFiles.SaveAsync(snapshot, false, true);

        Assert.True(second.IsSuccess);
        Assert.Equal(1, second.Value?.SavedFiles);
        Assert.Contains("new \"选项\"", await File.ReadAllTextAsync(project.TlPath));
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

    [Fact]
    public async Task Save_部分失败仍返回逐文件提交并刷新成功文件基线()
    {
        await using var project = await TestFiles.CreateProjectAsync(Source, Tl);
        var secondPath = Path.Combine(project.Root, "game", "tl", "schinese", "z-second.rpy");
        await File.WriteAllTextAsync(secondPath, "translate schinese strings:\n    old \"Choice\"\n    new \"\"\n");
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        snapshot.SharedStrings.Single().Translation = "选择";
        await File.AppendAllTextAsync(secondPath, "# 外部修改\n");

        var result = await TestFiles.SaveAsync(snapshot, false, true);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(2, result.Value!.Files.Count);
        Assert.Contains(result.Value.Files, file => file.Status == SaveFileStatus.Saved);
        Assert.Contains(result.Value.Files, file => file.Status == SaveFileStatus.Failed);
        var firstDocument = snapshot.TlDocuments.Single(document => document.RelativePath == "script.rpy");
        var secondDocument = snapshot.TlDocuments.Single(document => document.RelativePath == "z-second.rpy");
        Assert.All(firstDocument.Units, unit => Assert.False(unit.IsDirty));
        Assert.Contains(secondDocument.Units, unit => unit.IsDirty);
        Assert.Contains("new \"选择\"", firstDocument.BaselineText);
    }

    [Fact]
    public async Task Save_孤立受管标记时拒绝写入且不截断文件()
    {
        const string brokenTl = """
translate schinese strings:
    old "Choice"
    new ""

# RFT-FLOW-BEGIN
# 用户的重要尾部内容
""";
        await using var project = await TestFiles.CreateProjectAsync(Source, brokenTl);
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        snapshot.SharedStrings.Single().Translation = "选择";

        var result = await TestFiles.SaveAsync(snapshot, false, true);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RFT_FLOW_MARKER_UNBALANCED");
        Assert.Contains("# 用户的重要尾部内容", await File.ReadAllTextAsync(project.TlPath));
    }

    [Fact]
    public async Task Save_写入等待期间的新编辑保持脏状态()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddText("game/script.rpy", Source);
        fileSystem.AddText("game/tl/schinese/script.rpy", Tl);
        var analysis = await new ProjectAnalysisService(fileSystem).ExecuteAsync(
            new ProjectAnalysisRequest("C:\\project", "schinese"),
            new Progress<ToolOperationProgress>(),
            CancellationToken.None);
        var snapshot = Assert.IsType<ProjectSnapshot>(analysis.Value);
        var definition = Assert.Single(snapshot.SharedStrings).Definitions.Single();
        definition.TranslationText = "第一次";
        definition.IsDirty = true;
        fileSystem.BeforeAtomicWrite = () =>
        {
            definition.TranslationText = "写入期间的新编辑";
            definition.IsDirty = true;
        };

        var result = await new ProjectWriter(fileSystem).ExecuteAsync(
            new ProjectSaveRequest(snapshot, false, true),
            new Progress<ToolOperationProgress>(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(definition.IsDirty);
        Assert.Equal("写入期间的新编辑", definition.TranslationText);
        Assert.Contains("new \"第一次\"", snapshot.TlDocuments.Single().BaselineText);
    }

    [Fact]
    public async Task Validate_转义引号不会截断原文占位符检查()
    {
        const string source = "label start:\n    e \"Say \\\"hello\\\" to [name].\"\n";
        const string tl = "# game/script.rpy:2\ntranslate schinese start_line:\n    # e \"Say \\\"hello\\\" to [name].\"\n    e \"你好。\"\n";
        await using var project = await TestFiles.CreateProjectAsync(source, tl);
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var unit = Assert.Single(snapshot.TranslationUnits);
        unit.TranslationText = "你好。";
        unit.IsDirty = true;

        var diagnostics = new ProjectWriter(new FileSystemService()).Validate(snapshot);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "INTERPOLATION_MISMATCH");
    }
}
