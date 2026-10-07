using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class TranslationQualityTests
{
    [Fact]
    public async Task 质量检查将空译文与同原文不同译法关联到确切单元并读取最新修改()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var service = new TranslationQualityService();
        var issues = service.Check(snapshot);
        Assert.Equal(4, issues.Count);
        var pending = Assert.Single(issues, issue => issue.Diagnostics.Any(diagnostic => diagnostic.Code == "EMPTY_TRANSLATION"));
        Assert.Same(snapshot.TranslationUnits.Last(), pending.Unit);
        Assert.Single(pending.Diagnostics);
        Assert.All(issues.Where(issue => issue != pending), issue =>
            Assert.Contains(issue.Diagnostics, diagnostic => diagnostic.Code == "INCONSISTENT_TRANSLATION" && diagnostic.Severity == DiagnosticSeverity.Info));

        foreach (var unit in snapshot.TranslationUnits) unit.TranslationText = "你好 [name]";
        Assert.Empty(service.Check(snapshot));
    }

    [Fact]
    public async Task 一条译文多个占位符问题只占一行且不漏报文本标签和百分号()
    {
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n", """
translate schinese strings:
    old "Hello [name], {b}%s{/b}"
    new "你好"
""");
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var issue = Assert.Single(new TranslationQualityService().Check(snapshot));
        Assert.Same(Assert.Single(snapshot.SharedStrings), issue.SharedString);
        Assert.Equal(3, issue.Diagnostics.Count);
        Assert.Contains(issue.Diagnostics, diagnostic => diagnostic.Code == "INTERPOLATION_MISMATCH");
        Assert.Contains(issue.Diagnostics, diagnostic => diagnostic.Code == "TEXT_TAG_MISMATCH");
        Assert.Contains(issue.Diagnostics, diagnostic => diagnostic.Code == "FORMAT_PLACEHOLDER_MISMATCH");
    }

    [Fact]
    public async Task 共享字符串重复定义只报一条冲突且统一后不保留旧诊断()
    {
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n", """
translate schinese strings:
    old "Hello [name]"
    new "你好 [name]"
    old "Hello [name]"
    new "您好"
""");
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var service = new TranslationQualityService();
        var issue = Assert.Single(service.Check(snapshot));
        Assert.Contains(issue.Diagnostics, diagnostic => diagnostic.Code == "SHARED_TRANSLATION_CONFLICT");
        Assert.Contains(issue.Diagnostics, diagnostic => diagnostic.Code == "INTERPOLATION_MISMATCH");
        snapshot.SharedStrings[0].Unify("你好 [name]");
        Assert.Empty(service.Check(snapshot));
    }

    [Fact]
    public async Task 复杂块只提示人工核对而不把脚本代码当成普通译文检查()
    {
        await using var project = await TestFiles.CreateProjectAsync("label start:\n    return\n", """
# game/script.rpy:2
translate schinese complex:
    # e "Hello [name]"
    $ count += 1
    e "你好 [name]"
    e "继续"
""");
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var unit = Assert.Single(snapshot.TranslationUnits);
        Assert.True(unit.IsRawMode);
        var issue = Assert.Single(new TranslationQualityService().Check(snapshot));
        Assert.Equal("RAW_BLOCK_REVIEW", Assert.Single(issue.Diagnostics).Code);
    }
}
