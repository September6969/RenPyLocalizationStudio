using System.Text.RegularExpressions;

namespace RenPyLocalizationStudio.Core;

public sealed partial class TlParser
{
    [GeneratedRegex("^translate\\s+(?<language>\\S+)\\s+(?<id>[^:]+):\\s*(?:#.*)?$")]
    private static partial Regex TranslateHeaderRegex();

    [GeneratedRegex("^\\s*#\\s*(?<path>.+?\\.rpy):(?<line>\\d+)\\s*$")]
    private static partial Regex SourceLocationRegex();

    [GeneratedRegex("^\\s*#\\s*(?<statement>.+)$")]
    private static partial Regex SourceStatementRegex();

    [GeneratedRegex("^\\s*(?<keyword>old|new)\\s+(?<prefix>_p\\s*\\()?\"(?<value>(?:\\\\.|[^\"\\\\])*)\"(?<suffix>\\))?\\s*(?:#.*)?$")]
    private static partial Regex StringEntryRegex();

    [GeneratedRegex("(?<quote>\")(?<value>(?:\\\\.|[^\"\\\\])*)\\k<quote>")]
    private static partial Regex QuotedValueRegex();

    public TlDocument Parse(Utf8TextFile file, string relativePath, string expectedLanguage)
    {
        var document = new TlDocument
        {
            File = file,
            Language = expectedLanguage,
            RelativePath = TextUtilities.NormalizePath(relativePath),
            BaselineText = file.Text,
            BaselineSha256 = file.Sha256
        };
        var lines = TextUtilities.SliceLines(file.Text);

        for (var index = 0; index < lines.Count; index++)
        {
            var header = TranslateHeaderRegex().Match(lines[index].Content);
            if (!header.Success)
            {
                continue;
            }

            var language = header.Groups["language"].Value;
            var identifier = header.Groups["id"].Value.Trim();
            var blockEndIndex = FindBlockEnd(lines, index + 1);
            if (!string.Equals(language, expectedLanguage, StringComparison.Ordinal))
            {
                document.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "TL_LANGUAGE_MISMATCH",
                    $"翻译声明使用语言 {language}，当前目录语言为 {expectedLanguage}。",
                    document.RelativePath,
                    lines[index].Number));
            }

            if (identifier == "strings")
            {
                ParseStringsBlock(document, lines, index, blockEndIndex, language);
            }
            else
            {
                ParseDialogueBlock(document, lines, index, blockEndIndex, language, identifier);
            }

            index = Math.Max(index, blockEndIndex - 1);
        }

        return document;
    }

    private static void ParseDialogueBlock(
        TlDocument document,
        IReadOnlyList<LineSlice> lines,
        int headerIndex,
        int blockEndIndex,
        string language,
        string identifier)
    {
        var (sourcePath, sourceLine) = FindPrecedingSourceLocation(lines, headerIndex);
        string? sourceStatement = null;
        TextSpan? valueSpan = null;
        var translation = string.Empty;
        var activeLineCount = 0;
        TextSpan? candidateValueSpan = null;
        var candidateTranslation = string.Empty;

        for (var index = headerIndex + 1; index < blockEndIndex; index++)
        {
            var line = lines[index];
            var location = SourceLocationRegex().Match(line.Content);
            if (location.Success && sourcePath is null && activeLineCount == 0 && sourceStatement is null)
            {
                sourcePath = TextUtilities.NormalizePath(location.Groups["path"].Value);
                sourceLine = int.Parse(location.Groups["line"].Value);
                continue;
            }

            var statement = SourceStatementRegex().Match(line.Content);
            if (statement.Success && activeLineCount == 0 &&
                IsOfficialSourceStatement(statement.Groups["statement"].Value))
            {
                sourceStatement = statement.Groups["statement"].Value.Trim();
                continue;
            }

            if (line.Content.TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(line.Content))
            {
                continue;
            }

            activeLineCount++;
            var quoted = QuotedValueRegex().Matches(line.Content).Cast<Match>().LastOrDefault();
            if (quoted is not null && IsEditableDialogueStatement(line.Content, quoted))
            {
                candidateTranslation = TextUtilities.UnescapeRenPyString(quoted.Groups["value"].Value);
                candidateValueSpan = new TextSpan(line.Start + quoted.Groups["value"].Index, quoted.Groups["value"].Length);
            }
        }

        if (activeLineCount == 1 && candidateValueSpan is not null)
        {
            translation = candidateTranslation;
            valueSpan = candidateValueSpan;
        }

        var start = lines[headerIndex].Start;
        var end = blockEndIndex < lines.Count ? lines[blockEndIndex].Start : document.File.Text.Length;
        var bodyStart = headerIndex + 1 < lines.Count ? lines[headerIndex + 1].Start : end;
        document.Units.Add(new TranslationUnit
        {
            Kind = TranslationUnitKind.Dialogue,
            Language = language,
            FilePath = document.File.FullPath,
            RelativeTlPath = document.RelativePath,
            BlockSpan = new TextSpan(start, Math.Max(0, end - start)),
            HeaderLine = lines[headerIndex].Number,
            Identifier = identifier,
            SourcePath = sourcePath,
            SourceLine = sourceLine,
            OriginalStatement = sourceStatement,
            TranslationText = translation,
            TranslationValueSpan = valueSpan,
            RawBodySpan = new TextSpan(bodyStart, Math.Max(0, end - bodyStart)),
            RawBodyText = document.File.Text.Substring(bodyStart, Math.Max(0, end - bodyStart))
        });

        if (valueSpan is null)
        {
            document.Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "TL_COMPLEX_BLOCK",
                $"翻译块 {identifier} 没有可结构化编辑的单行字符串，将使用原始块模式。",
                document.RelativePath,
                lines[headerIndex].Number));
        }
    }

    private static (string? Path, int? Line) FindPrecedingSourceLocation(IReadOnlyList<LineSlice> lines, int headerIndex)
    {
        for (var index = headerIndex - 1; index >= 0; index--)
        {
            var content = lines[index].Content;
            var location = SourceLocationRegex().Match(content);
            if (location.Success)
            {
                return (
                    TextUtilities.NormalizePath(location.Groups["path"].Value),
                    int.Parse(location.Groups["line"].Value));
            }

            var trimmed = content.Trim();
            if (TranslateHeaderRegex().IsMatch(content) || (trimmed.Length > 0 && !trimmed.StartsWith('#')))
            {
                break;
            }
        }

        return (null, null);
    }

    private static void ParseStringsBlock(
        TlDocument document,
        IReadOnlyList<LineSlice> lines,
        int headerIndex,
        int blockEndIndex,
        string language)
    {
        for (var index = headerIndex + 1; index < blockEndIndex; index++)
        {
            var oldMatch = StringEntryRegex().Match(lines[index].Content);
            if (!oldMatch.Success || oldMatch.Groups["keyword"].Value != "old")
            {
                continue;
            }

            string? sourcePath = null;
            int? sourceLine = null;
            for (var commentIndex = index - 1; commentIndex > headerIndex; commentIndex--)
            {
                if (string.IsNullOrWhiteSpace(lines[commentIndex].Content))
                {
                    continue;
                }

                var location = SourceLocationRegex().Match(lines[commentIndex].Content);
                if (location.Success)
                {
                    sourcePath = TextUtilities.NormalizePath(location.Groups["path"].Value);
                    sourceLine = int.Parse(location.Groups["line"].Value);
                    break;
                }

                // 用户可能在官方源码位置注释与 old 之间插入任意普通注释。
                // 注释不应切断位置回溯；遇到真实语句或上一条 old/new 才停止。
                if (lines[commentIndex].Content.TrimStart().StartsWith('#')) continue;
                break;
            }

            Match? newMatch = null;
            var newIndex = -1;
            var itemEndIndex = blockEndIndex;
            for (var candidateIndex = index + 1; candidateIndex < blockEndIndex; candidateIndex++)
            {
                var candidate = StringEntryRegex().Match(lines[candidateIndex].Content);
                if (candidate.Success && candidate.Groups["keyword"].Value == "old")
                {
                    itemEndIndex = candidateIndex;
                    break;
                }

                if (candidate.Success && candidate.Groups["keyword"].Value == "new")
                {
                    newMatch = candidate;
                    newIndex = candidateIndex;
                }
            }

            var oldText = TextUtilities.UnescapeRenPyString(oldMatch.Groups["value"].Value);
            var translation = newMatch is null ? string.Empty : TextUtilities.UnescapeRenPyString(newMatch.Groups["value"].Value);
            var startIndex = FindItemStart(lines, headerIndex, index);
            var start = lines[startIndex].Start;
            var end = itemEndIndex < lines.Count ? lines[itemEndIndex].Start : document.File.Text.Length;
            document.Units.Add(new TranslationUnit
            {
                Kind = TranslationUnitKind.String,
                Language = language,
                FilePath = document.File.FullPath,
                RelativeTlPath = document.RelativePath,
                BlockSpan = new TextSpan(start, Math.Max(0, end - start)),
                HeaderLine = lines[index].Number,
                SourcePath = sourcePath,
                SourceLine = sourceLine,
                OldText = oldText,
                TranslationText = translation,
                OldLineSpan = new TextSpan(lines[index].Start, lines[index].Length),
                TranslationValueSpan = newMatch is null
                    ? null
                    : new TextSpan(lines[newIndex].Start + newMatch.Groups["value"].Index, newMatch.Groups["value"].Length),
                MissingNew = newMatch is null
            });

            if (newMatch is null)
            {
                document.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "TL_MISSING_NEW",
                    $"字符串表条目缺少 new：{oldText}",
                    document.RelativePath,
                    lines[index].Number));
            }

            index = Math.Max(index, itemEndIndex - 1);
        }
    }

    private static int FindItemStart(IReadOnlyList<LineSlice> lines, int headerIndex, int oldIndex)
    {
        var start = oldIndex;
        for (var index = oldIndex - 1; index > headerIndex; index--)
        {
            var trimmed = lines[index].Content.Trim();
            if (trimmed.Length == 0)
            {
                start = index;
                break;
            }

            if (trimmed.StartsWith('#'))
            {
                start = index;
                continue;
            }

            break;
        }

        return start;
    }

    private static int FindBlockEnd(IReadOnlyList<LineSlice> lines, int startIndex)
    {
        for (var index = startIndex; index < lines.Count; index++)
        {
            if (TranslateHeaderRegex().IsMatch(lines[index].Content))
            {
                return index;
            }

            if (TextUtilities.GetIndent(lines[index].Content) == 0 && SourceLocationRegex().IsMatch(lines[index].Content))
            {
                return index;
            }

            var trimmed = lines[index].Content.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#') && TextUtilities.GetIndent(lines[index].Content) == 0)
            {
                return index;
            }
        }

        return lines.Count;
    }

    private static bool IsEditableDialogueStatement(string line, Match quoted)
    {
        var prefix = line[..quoted.Index].Trim();
        if (prefix.Contains('=') || prefix.StartsWith('$'))
        {
            return false;
        }

        var firstWord = prefix.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstWord is not ("show" or "scene" or "hide" or "play" or "queue" or "voice" or "python" or "if" or "elif" or "else" or "jump" or "call" or "pause" or "window");
    }

    private static bool IsOfficialSourceStatement(string statement)
    {
        var quoted = QuotedValueRegex().Matches(statement).Cast<Match>().LastOrDefault();
        return quoted is not null && IsEditableDialogueStatement(statement, quoted);
    }
}
