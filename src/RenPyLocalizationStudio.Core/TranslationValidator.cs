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
        CompareTokens("INTERPOLATION_MISMATCH", "插值表达式", InterpolationRegex(), source, translation, diagnostics, relativePath, line);
        CompareTokens("FORMAT_PLACEHOLDER_MISMATCH", "百分号占位符", PercentPlaceholderRegex(), source, translation, diagnostics, relativePath, line);
        CompareTokens("TEXT_TAG_MISMATCH", "文本标签", TextTagRegex(), source, translation, diagnostics, relativePath, line);
        return diagnostics;
    }

    private static void CompareTokens(
        string code,
        string displayName,
        Regex regex,
        string source,
        string translation,
        ICollection<Diagnostic> diagnostics,
        string? relativePath,
        int? line)
    {
        var expected = regex.Matches(source).Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();
        var actual = regex.Matches(translation).Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                code,
                $"{displayName}不一致。原文：{string.Join(", ", expected)}；译文：{string.Join(", ", actual)}",
                relativePath,
                line));
        }
    }
}
