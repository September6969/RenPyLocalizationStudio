using System.Security.Cryptography;
using System.Text;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

internal sealed class InMemoryFileSystem : IFileSystemService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;

    public InMemoryFileSystem(string root = "C:\\project") => _root = Path.GetFullPath(root).TrimEnd('\\');

    public int WriteCount { get; private set; }

    public void AddText(string relativePath, string text, bool bom = false, string newLine = "\n")
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal);
        var payload = StrictUtf8.GetBytes(normalized);
        _files[Normalize(relativePath)] = bom ? Encoding.UTF8.Preamble.ToArray().Concat(payload).ToArray() : payload;
    }

    public void AddBytes(string relativePath, byte[] bytes) => _files[Normalize(relativePath)] = bytes;

    public OperationResult<ProjectRoot> ValidateProjectRoot(string projectRoot) =>
        string.Equals(Path.GetFullPath(projectRoot).TrimEnd('\\'), _root, StringComparison.OrdinalIgnoreCase)
            ? OperationResult<ProjectRoot>.Success(new ProjectRoot(_root))
            : OperationResult<ProjectRoot>.Failure(Error("PROJECT_ROOT_INVALID", projectRoot));

    public OperationResult<ValidatedProjectPath> ValidateProjectPath(ProjectRoot root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return OperationResult<ValidatedProjectPath>.Failure(Error("PATH_ROOTED", relativePath));
        }

        var full = Path.GetFullPath(Path.Combine(root.FullPath, relativePath));
        if (!full.StartsWith(root.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<ValidatedProjectPath>.Failure(Error("PATH_TRAVERSAL", relativePath));
        }

        return OperationResult<ValidatedProjectPath>.Success(
            new ValidatedProjectPath(root, full, Path.GetRelativePath(root.FullPath, full)));
    }

    public OperationResult<ValidatedExecutablePath> ValidateExecutable(string path) =>
        path.Equals("C:\\tools\\renpy.exe", StringComparison.OrdinalIgnoreCase)
            ? OperationResult<ValidatedExecutablePath>.Success(new ValidatedExecutablePath(path))
            : OperationResult<ValidatedExecutablePath>.Failure(Error("EXECUTABLE_NOT_FOUND", path));

    public Task<OperationResult<Utf8TextFile>> ReadUtf8Async(ValidatedProjectPath path, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(OperationResult<Utf8TextFile>.Cancelled(Error("OPERATION_CANCELLED", path.RelativePath)));
        }

        if (!_files.TryGetValue(Normalize(path.RelativePath), out var bytes))
        {
            return Task.FromResult(OperationResult<Utf8TextFile>.Failure(Error("UTF8_READ_FAILED", path.RelativePath)));
        }

        try
        {
            var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var payload = bom ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;
            var text = StrictUtf8.GetString(payload);
            return Task.FromResult(OperationResult<Utf8TextFile>.Success(new Utf8TextFile
            {
                FullPath = path.FullPath,
                Text = text,
                HasBom = bom,
                NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
                HasFinalNewLine = text.EndsWith('\n'),
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            }));
        }
        catch (DecoderFallbackException)
        {
            return Task.FromResult(OperationResult<Utf8TextFile>.Failure(
                new Diagnostic(DiagnosticSeverity.Error, "UTF8_READ_FAILED", "文件不是有效的 UTF-8。", path.RelativePath, Category: DiagnosticCategory.Encoding)));
        }
    }

    public Task<OperationResult<BinaryFileContent>> ReadBytesAsync(ValidatedProjectPath path, CancellationToken cancellationToken)
    {
        if (!_files.TryGetValue(Normalize(path.RelativePath), out var bytes))
        {
            return Task.FromResult(OperationResult<BinaryFileContent>.Failure(Error("BINARY_READ_FAILED", path.RelativePath)));
        }

        return Task.FromResult(OperationResult<BinaryFileContent>.Success(
            new BinaryFileContent(bytes.ToArray(), Convert.ToHexString(SHA256.HashData(bytes)))));
    }

    public Task<OperationResult<AtomicWriteSummary>> AtomicWriteAsync(
        AtomicWriteRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(OperationResult<AtomicWriteSummary>.Cancelled(Error("OPERATION_CANCELLED", request.Target.RelativePath)));
        }

        var key = Normalize(request.Target.RelativePath);
        if (request.ExpectedSha256 is not null && _files.TryGetValue(key, out var current) &&
            !Convert.ToHexString(SHA256.HashData(current)).Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(OperationResult<AtomicWriteSummary>.Failure(Error("EXTERNAL_MODIFICATION", key)));
        }

        _files[key] = request.Content.ToArray();
        WriteCount++;
        return Task.FromResult(OperationResult<AtomicWriteSummary>.Success(
            new AtomicWriteSummary(key, Convert.ToHexString(SHA256.HashData(request.Content)), false)));
    }

    public Task<OperationResult<FileMoveSummary>> MoveAsync(
        FileMoveRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<FileMoveSummary>.Failure(Error("MOVE_UNSUPPORTED", request.Source.RelativePath)));

    public Task<OperationResult<IReadOnlyList<ValidatedProjectPath>>> EnumerateFilesAsync(
        ProjectRoot root,
        string relativeDirectory,
        string searchPattern,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(OperationResult<IReadOnlyList<ValidatedProjectPath>>.Cancelled(Error("OPERATION_CANCELLED", relativeDirectory)));
        }

        var prefix = Normalize(relativeDirectory).TrimEnd('/') + "/";
        var extension = searchPattern.StartsWith("*.", StringComparison.Ordinal) ? searchPattern[1..] : null;
        var values = _files.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(path => extension is null || path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(path => ValidateProjectPath(root, path).Value!)
            .ToArray();
        return Task.FromResult(OperationResult<IReadOnlyList<ValidatedProjectPath>>.Success(values));
    }

    public Task<OperationResult<IReadOnlyList<string>>> EnumerateDirectoriesAsync(
        ProjectRoot root,
        string relativeDirectory,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(OperationResult<IReadOnlyList<string>>.Cancelled(Error("OPERATION_CANCELLED", relativeDirectory)));
        }

        var prefix = Normalize(relativeDirectory).TrimEnd('/') + "/";
        var values = _files.Keys.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => path[prefix.Length..].Split('/')[0])
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult(OperationResult<IReadOnlyList<string>>.Success(values));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');
    private static Diagnostic Error(string code, string path) =>
        new(DiagnosticSeverity.Error, code, code, path, Category: DiagnosticCategory.FileSystem);
}
