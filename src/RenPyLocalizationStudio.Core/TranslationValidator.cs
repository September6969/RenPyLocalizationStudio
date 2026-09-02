using System.Text.RegularExpressions;

namespace RenPyLocalizationStudio.Core;

public sealed partial class TranslationValidator
{
    [GeneratedRegex("\\[(?<value>[^\\[\\]]+)]")]
    private static partial Regex InterpolationRegex();

    [GeneratedRegex("%(?!%)(?:\\([^)]+\\))?[#0 +\\-]?[0-9.*]*[diouxXeEfFgGcrsa%]")]
    private static partial Regex PercentPlaceholderRegex();

    [GeneratedRegex("\\{/?[^{}]+}")]
    private static partial Regex TextTagRegex();

    public IReadOnlyList<Diagnostic> Validate(string source, string translation, string? relativePath = null, int? line = null)
    {
        var diagnostics = new List<Diagnostic>();
        CompareTokens("INTERPOLATION_MISMATCH", "插值表达式", InterpolationRegex(), source, translation, false, diagnostics, relativePath, line);
        ComparePercentTokens(source, translation, diagnostics, relativePath, line);
        CompareTokens("TEXT_TAG_MISMATCH", "文本标签", TextTagRegex(), source, translation, true, diagnostics, relativePath, line);
        return diagnostics;
    }

    private static void ComparePercentTokens(
        string source,
        string translation,
        ICollection<Diagnostic> diagnostics,
        string? relativePath,
        int? line)
    {
        var expected = PercentPlaceholderRegex().Matches(source).Select(match => match.Value).ToArray();
        var actual = PercentPlaceholderRegex().Matches(translation).Select(match => match.Value).ToArray();
        var hasPositionalPlaceholder = expected.Concat(actual).Any(token => !token.StartsWith("%(", StringComparison.Ordinal));
        CompareTokenArrays("FORMAT_PLACEHOLDER_MISMATCH", "百分号占位符", expected, actual,
            hasPositionalPlaceholder, diagnostics, relativePath, line);
    }

    private static void CompareTokens(
        string code,
        string displayName,
        Regex regex,
        string source,
        string translation,
        bool preserveOrder,
        ICollection<Diagnostic> diagnostics,
        string? relativePath,
        int? line)
    {
        var expected = regex.Matches(source).Select(match => match.Value).ToArray();
        var actual = regex.Matches(translation).Select(match => match.Value).ToArray();
        CompareTokenArrays(code, displayName, expected, actual, preserveOrder, diagnostics, relativePath, line);
    }

    private static void CompareTokenArrays(
        string code,
        string displayName,
        string[] expected,
        string[] actual,
        bool preserveOrder,
        ICollection<Diagnostic> diagnostics,
        string? relativePath,
        int? line)
    {
        var expectedComparison = preserveOrder ? expected : expected.Order(StringComparer.Ordinal).ToArray();
        var actualComparison = preserveOrder ? actual : actual.Order(StringComparer.Ordinal).ToArray();
        if (!expectedComparison.SequenceEqual(actualComparison, StringComparer.Ordinal))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                code,
                $"{displayName}{(preserveOrder ? "顺序或内容" : "内容")}不一致。原文：{string.Join(", ", expected)}；译文：{string.Join(", ", actual)}",
                relativePath,
                line));
        }
    }
}
