using System.Text;
using System.Text.RegularExpressions;

namespace RenPyLocalizationStudio.Core;

internal static partial class TextUtilities
{
    [GeneratedRegex("^([ \\t]*)")]
    private static partial Regex LeadingWhitespaceRegex();

    public static int GetIndent(string line)
    {
        var whitespace = LeadingWhitespaceRegex().Match(line).Groups[1].Value;
        var indent = 0;
        foreach (var character in whitespace)
        {
            indent += character == '\t' ? 4 : 1;
        }

        return indent;
    }

    public static List<LineSlice> SliceLines(string text)
    {
        var result = new List<LineSlice>();
        var start = 0;
        var lineNumber = 1;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            var contentEnd = newline < 0 ? end : newline;
            if (contentEnd > start && text[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            result.Add(new LineSlice(lineNumber++, start, end - start, text[start..contentEnd]));
            start = end;
        }

        if (text.Length == 0)
        {
            result.Add(new LineSlice(1, 0, 0, string.Empty));
        }

        return result;
    }

    public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    public static string EscapeRenPyString(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => character.ToString()
            });
        }

        return builder.ToString();
    }

    public static string UnescapeRenPyString(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }

            var next = value[++index];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '"' => '"',
                '\'' => '\'',
                _ => "\\" + next
            });
        }

        return builder.ToString();
    }
}

internal sealed record LineSlice(int Number, int Start, int Length, string Content)
{
    public int End => Start + Length;
}
