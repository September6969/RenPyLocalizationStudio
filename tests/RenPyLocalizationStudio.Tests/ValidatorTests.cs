using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class ValidatorTests
{
    [Fact]
    public void Validate_同时报告插值和百分号占位符变化()
    {
        var diagnostics = new TranslationValidator().Validate(
            "Hello, [player_name]! You have %d gold.",
            "你好，[name]！你有 %s 金币。");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "INTERPOLATION_MISMATCH");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "FORMAT_PLACEHOLDER_MISMATCH");
    }

    [Fact]
    public void Validate_位置占位符和文本标签保持顺序_命名占位符允许重排()
    {
        var positional = new TranslationValidator().Validate("%d {b}%s{/b}", "%s {b}%d{/b}");
        var named = new TranslationValidator().Validate("%(name)s %(count)d", "%(count)d %(name)s");
        var tags = new TranslationValidator().Validate("{b}{i}text{/i}{/b}", "{i}{b}文本{/b}{/i}");

        Assert.Contains(positional, diagnostic => diagnostic.Code == "FORMAT_PLACEHOLDER_MISMATCH");
        Assert.DoesNotContain(named, diagnostic => diagnostic.Code == "FORMAT_PLACEHOLDER_MISMATCH");
        Assert.Contains(tags, diagnostic => diagnostic.Code == "TEXT_TAG_MISMATCH");
    }
}
