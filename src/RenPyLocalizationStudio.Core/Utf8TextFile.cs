using System.Security.Cryptography;
using System.Text;

namespace RenPyLocalizationStudio.Core;

public sealed class Utf8TextFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public required string FullPath { get; init; }
    public required string Text { get; init; }
    public required bool HasBom { get; init; }
    public required string NewLine { get; init; }
    public required bool HasFinalNewLine { get; init; }
    public required string Sha256 { get; init; }

    public static async Task<Utf8TextFile> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var payload = hasBom ? bytes.AsSpan(Encoding.UTF8.Preamble.Length).ToArray() : bytes;
        string text;
        try
        {
            text = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"文件不是有效的 UTF-8：{path}", exception);
        }

        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var finalNewLine = text.EndsWith("\n", StringComparison.Ordinal);
        return new Utf8TextFile
        {
            FullPath = Path.GetFullPath(path),
            Text = text,
            HasBom = hasBom,
            NewLine = newLine,
            HasFinalNewLine = finalNewLine,
            Sha256 = ComputeSha256(bytes)
        };
    }

    public byte[] Encode(string text)
    {
        var normalized = NormalizeNewLines(text, NewLine);
        if (HasFinalNewLine && !normalized.EndsWith(NewLine, StringComparison.Ordinal))
        {
            normalized += NewLine;
        }
        else if (!HasFinalNewLine)
        {
            normalized = normalized.TrimEnd('\r', '\n');
        }

        var payload = new UTF8Encoding(false, true).GetBytes(normalized);
        if (!HasBom)
        {
            return payload;
        }

        var result = new byte[Encoding.UTF8.Preamble.Length + payload.Length];
        Encoding.UTF8.Preamble.CopyTo(result.AsSpan());
        payload.CopyTo(result, Encoding.UTF8.Preamble.Length);
        return result;
    }

    public static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
        => ComputeSha256(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));

    private static string NormalizeNewLines(string value, string newLine)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", newLine, StringComparison.Ordinal);
}
