using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.Services;

public sealed record ToolRuntimePaths(string Python, string SafeRpaExtractor, string Unrpyc);

/// <summary>启动第三方工具前校验发布 manifest，避免运行时文件被替换或发布包残缺。</summary>
public static class ToolRuntimeManifestValidator
{
    public static async Task<OperationResult<ToolRuntimePaths>> ValidateAsync(string applicationDirectory, CancellationToken cancellationToken)
    {
        var tools = Path.GetFullPath(Path.Combine(applicationDirectory, "tools"));
        var manifestPath = Path.Combine(tools, "manifest.json");
        try
        {
            if (!File.Exists(manifestPath))
                return OperationResult<ToolRuntimePaths>.Failure(Diagnostic("TOOL_MANIFEST_MISSING", "工具运行时 manifest.json 缺失。", manifestPath));

            await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return OperationResult<ToolRuntimePaths>.Failure(Diagnostic("TOOL_MANIFEST_INVALID", "工具运行时 manifest 缺少 files 清单。", manifestPath));

            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in files.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = item.GetProperty("path").GetString()?.Replace('/', Path.DirectorySeparatorChar);
                var expectedLength = item.GetProperty("length").GetInt64();
                var expectedHash = item.GetProperty("sha256").GetString();
                if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(expectedHash) || Path.IsPathRooted(relative))
                    return OperationResult<ToolRuntimePaths>.Failure(Diagnostic("TOOL_MANIFEST_ENTRY_INVALID", "工具 manifest 包含非法路径或哈希。", manifestPath));
                var full = Path.GetFullPath(Path.Combine(tools, relative));
                if (!full.StartsWith(tools + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return OperationResult<ToolRuntimePaths>.Failure(Diagnostic("TOOL_MANIFEST_PATH_TRAVERSAL", $"工具 manifest 路径越界：{relative}", relative));
                if (!File.Exists(full))
                    return OperationResult<ToolRuntimePaths>.Failure(Diagnostic("TOOL_RUNTIME_FILE_MISSING", $"工具运行时文件缺失：{relative}", relative));
                var info = new FileInfo(full);
                if (info.Length != expectedLength)
                    return OperationResult<ToolRuntimePaths>.Failure(Diagnostic("TOOL_RUNTIME_LENGTH_MISMATCH", $"工具运行时文件长度不匹配：{relative}", relative));
                await using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    return OperationResult<ToolRuntimePaths>.Failure(Diagnostic("TOOL_RUNTIME_HASH_MISMATCH", $"工具运行时文件哈希不匹配：{relative}", relative));
                listed.Add(Path.GetRelativePath(tools, full));
            }

            var paths = new ToolRuntimePaths(
                Path.Combine(tools, "python", "python.exe"),
                Path.Combine(tools, "rpa", "safe_rpa_extract.py"),
                Path.Combine(tools, "unrpyc", "unrpyc.py"));
            foreach (var required in new[] { paths.Python, paths.SafeRpaExtractor, paths.Unrpyc })
            {
                var relative = Path.GetRelativePath(tools, required);
                if (!listed.Contains(relative))
                    return OperationResult<ToolRuntimePaths>.Failure(Diagnostic("TOOL_RUNTIME_REQUIRED_FILE_UNLISTED", $"必要工具未纳入 manifest：{relative}", relative));
            }
            return OperationResult<ToolRuntimePaths>.Success(paths);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<ToolRuntimePaths>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "TOOL_RUNTIME_VALIDATION_CANCELLED", "工具运行时校验已取消。"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                                            or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException
                                            or NotSupportedException)
        {
            return OperationResult<ToolRuntimePaths>.Failure(Diagnostic("TOOL_MANIFEST_READ_FAILED", "无法读取或校验工具运行时 manifest。", manifestPath, exception.Message));
        }
    }

    private static Diagnostic Diagnostic(string code, string message, string? path = null, string? details = null) =>
        new(DiagnosticSeverity.Error, code, message, path, Category: DiagnosticCategory.Security, TechnicalDetails: details);
}
