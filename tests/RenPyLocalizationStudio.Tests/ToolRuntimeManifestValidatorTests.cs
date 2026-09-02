using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class ToolRuntimeManifestValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsCompleteMatchingManifest()
    {
        var root = CreateRuntime(out _);
        try
        {
            var result = await ToolRuntimeManifestValidator.ValidateAsync(root, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsModifiedRuntimeFile()
    {
        var root = CreateRuntime(out var pythonPath);
        try
        {
            await File.AppendAllTextAsync(pythonPath, "tampered", Encoding.UTF8);

            var result = await ToolRuntimeManifestValidator.ValidateAsync(root, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code is "TOOL_RUNTIME_LENGTH_MISMATCH" or "TOOL_RUNTIME_HASH_MISMATCH");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsManifestPathTraversalWithoutThrowing()
    {
        var root = CreateRuntime(out _);
        try
        {
            var manifest = Path.Combine(root, "tools", "manifest.json");
            File.WriteAllText(manifest, "{\"files\":[{\"path\":\"../escape\",\"length\":0,\"sha256\":\"00\"}]}", new UTF8Encoding(false));

            var result = await ToolRuntimeManifestValidator.ValidateAsync(root, CancellationToken.None);

            Assert.Equal(OperationStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOOL_MANIFEST_PATH_TRAVERSAL");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateRuntime(out string pythonPath)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rls-runtime-{Guid.NewGuid():N}");
        var tools = Path.Combine(root, "tools");
        var relativePaths = new[]
        {
            "python/python.exe",
            "rpa/safe_rpa_extract.py",
            "unrpyc/unrpyc.py"
        };
        var files = new List<object>();
        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(tools, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var content = Encoding.UTF8.GetBytes(relative);
            File.WriteAllBytes(full, content);
            files.Add(new { path = relative, length = content.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(content)) });
        }
        File.WriteAllText(Path.Combine(tools, "manifest.json"), JsonSerializer.Serialize(new { files }), new UTF8Encoding(false));
        pythonPath = Path.Combine(tools, "python", "python.exe");
        return root;
    }
}
