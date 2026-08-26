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
}
