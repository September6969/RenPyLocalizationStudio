using System.Security.Cryptography;
using System.Text;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

internal static class TestFiles
{
    public static Utf8TextFile InMemory(string text, string path = "E:\\fixture.rpy", bool bom = false, string newLine = "\n")
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal);
        var payload = new UTF8Encoding(false).GetBytes(normalized);
        var bytes = bom ? Encoding.UTF8.Preamble.ToArray().Concat(payload).ToArray() : payload;
        return new Utf8TextFile
        {
            FullPath = path,
            Text = normalized,
            HasBom = bom,
            NewLine = newLine,
            HasFinalNewLine = normalized.EndsWith('\n'),
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };
    }

    public static async Task<TemporaryProject> CreateProjectAsync(string source, string tl, bool bom = false, string newLine = "\n")
    {
        var root = Path.Combine(Path.GetTempPath(), "RenPyLocalizationStudio.Tests", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        var tlDirectory = Path.Combine(game, "tl", "schinese");
        Directory.CreateDirectory(tlDirectory);
        var sourcePath = Path.Combine(game, "script.rpy");
        var tlPath = Path.Combine(tlDirectory, "script.rpy");
        await WriteUtf8Async(sourcePath, source, false, newLine);
        await WriteUtf8Async(tlPath, tl, bom, newLine);
        return new TemporaryProject(root, tlPath);
    }

    private static async Task WriteUtf8Async(string path, string text, bool bom, string newLine)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal);
        var encoding = new UTF8Encoding(bom);
        await File.WriteAllTextAsync(path, normalized, encoding);
    }
}

internal sealed record TemporaryProject(string Root, string TlPath) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }

        return ValueTask.CompletedTask;
    }
}
