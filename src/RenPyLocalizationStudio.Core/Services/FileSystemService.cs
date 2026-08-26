using System.Security.Cryptography;

namespace RenPyLocalizationStudio.Core.Services;

public sealed record ProjectRoot(string FullPath);
public sealed record ValidatedProjectPath(ProjectRoot Root, string FullPath, string RelativePath);
public sealed record ValidatedExecutablePath(string FullPath);
public sealed record ValidatedToolPath(string FullPath);
public sealed record BinaryFileContent(byte[] Content, string Sha256);
public sealed record AtomicWriteRequest(ValidatedProjectPath Target, byte[] Content, string? ExpectedSha256, string BackupSuffix = ".rls.bak", bool RequireTargetMissing = false);
public sealed record AtomicWriteSummary(string RelativePath, string Sha256, bool BackupCreated);
public sealed record FileMoveRequest(ValidatedProjectPath Source, ValidatedProjectPath Target, string ExpectedSourceSha256);
public sealed record FileMoveSummary(string SourceRelativePath, string TargetRelativePath);

public interface IFileSystemService
{
    OperationResult<ProjectRoot> ValidateProjectRoot(string projectRoot);
    OperationResult<ValidatedProjectPath> ValidateProjectPath(ProjectRoot root, string relativePath);
    OperationResult<ValidatedExecutablePath> ValidateExecutable(string path);
    Task<OperationResult<Utf8TextFile>> ReadUtf8Async(ValidatedProjectPath path, CancellationToken cancellationToken);
    Task<OperationResult<BinaryFileContent>> ReadBytesAsync(ValidatedProjectPath path, CancellationToken cancellationToken);
    Task<OperationResult<AtomicWriteSummary>> AtomicWriteAsync(
        AtomicWriteRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken);
    Task<OperationResult<FileMoveSummary>> MoveAsync(
        FileMoveRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyList<ValidatedProjectPath>>> EnumerateFilesAsync(
        ProjectRoot root,
        string relativeDirectory,
        string searchPattern,
        CancellationToken cancellationToken);
}

public sealed class FileSystemService : IFileSystemService
{
    public OperationResult<ProjectRoot> ValidateProjectRoot(string projectRoot)
    {
        try
        {
            var full = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(full) || full.StartsWith("\\\\", StringComparison.Ordinal) || IsDevicePath(full))
            {
                return OperationResult<ProjectRoot>.Failure(SecurityDiagnostic("PROJECT_ROOT_INVALID", "项目目录不存在，或属于不允许的 UNC/设备路径。", projectRoot));
            }

            return OperationResult<ProjectRoot>.Success(new ProjectRoot(full));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return OperationResult<ProjectRoot>.Failure(FileDiagnostic("PROJECT_ROOT_INVALID", ex.Message, projectRoot));
        }
    }

    public OperationResult<ValidatedProjectPath> ValidateProjectPath(ProjectRoot root, string relativePath)
    {
        try
        {
            if (Path.IsPathRooted(relativePath) || IsDevicePath(relativePath))
            {
                return OperationResult<ValidatedProjectPath>.Failure(SecurityDiagnostic("PATH_ROOTED", "写入路径必须是项目内相对路径。", relativePath));
            }

            var rootFull = Path.GetFullPath(root.FullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(rootFull, relativePath));
            var prefix = rootFull + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<ValidatedProjectPath>.Failure(SecurityDiagnostic("PATH_TRAVERSAL", "目标路径越过了项目根目录。", relativePath));
            }

            if (HasEscapingReparsePoint(rootFull, full))
            {
                return OperationResult<ValidatedProjectPath>.Failure(SecurityDiagnostic("REPARSE_POINT_ESCAPE", "目标路径包含指向项目外部的链接或目录联接。", relativePath));
            }

            return OperationResult<ValidatedProjectPath>.Success(new ValidatedProjectPath(root, full, Path.GetRelativePath(rootFull, full)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return OperationResult<ValidatedProjectPath>.Failure(FileDiagnostic("PATH_INVALID", ex.Message, relativePath));
        }
    }

    public OperationResult<ValidatedExecutablePath> ValidateExecutable(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return File.Exists(full)
                ? OperationResult<ValidatedExecutablePath>.Success(new ValidatedExecutablePath(full))
                : OperationResult<ValidatedExecutablePath>.Failure(FileDiagnostic("EXECUTABLE_NOT_FOUND", "找不到可执行文件。", path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return OperationResult<ValidatedExecutablePath>.Failure(FileDiagnostic("EXECUTABLE_INVALID", ex.Message, path));
        }
    }

    public async Task<OperationResult<Utf8TextFile>> ReadUtf8Async(ValidatedProjectPath path, CancellationToken cancellationToken)
    {
        try
        {
            return OperationResult<Utf8TextFile>.Success(await Utf8TextFile.ReadAsync(path.FullPath, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return OperationResult<Utf8TextFile>.Cancelled(CancelledDiagnostic(path.RelativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return OperationResult<Utf8TextFile>.Failure(FileDiagnostic("UTF8_READ_FAILED", ex.Message, path.RelativePath,
                ex is InvalidDataException ? DiagnosticCategory.Encoding : DiagnosticCategory.FileSystem));
        }
    }

    public async Task<OperationResult<BinaryFileContent>> ReadBytesAsync(ValidatedProjectPath path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            var content = memory.ToArray();
            return OperationResult<BinaryFileContent>.Success(new BinaryFileContent(content, Convert.ToHexString(SHA256.HashData(content))));
        }
        catch (OperationCanceledException)
        {
            return OperationResult<BinaryFileContent>.Cancelled(CancelledDiagnostic(path.RelativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<BinaryFileContent>.Failure(FileDiagnostic("BINARY_READ_FAILED", ex.Message, path.RelativePath));
        }
    }

    public async Task<OperationResult<AtomicWriteSummary>> AtomicWriteAsync(
        AtomicWriteRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revalidated = ValidateProjectPath(request.Target.Root, request.Target.RelativePath);
            if (!revalidated.IsSuccess || revalidated.Value is null)
            {
                return new OperationResult<AtomicWriteSummary>(OperationStatus.Failed, null, revalidated.Diagnostics);
            }

            var target = revalidated.Value.FullPath;
            if (request.RequireTargetMissing && File.Exists(target))
            {
                return OperationResult<AtomicWriteSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "TARGET_ALREADY_EXISTS",
                    "目标文件已存在，已停止覆盖。", request.Target.RelativePath, Category: DiagnosticCategory.FileSystem,
                    SuggestedAction: "更换输出目录，或先处理同名文件。"));
            }
            if (File.Exists(target) && request.ExpectedSha256 is not null)
            {
                var currentHash = await ComputeHashAsync(target, cancellationToken).ConfigureAwait(false);
                if (!currentHash.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult<AtomicWriteSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "EXTERNAL_MODIFICATION",
                        "文件已被外部修改，已停止覆盖。", request.Target.RelativePath, Category: DiagnosticCategory.FileSystem,
                        SuggestedAction: "重新加载项目后再保存。"));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            temporaryPath = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Writing, "正在写入临时文件", relativePath: request.Target.RelativePath));
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(request.Content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            var backupCreated = false;
            if (File.Exists(target))
            {
                progress.Report(ToolOperationProgress.Create(ToolOperationStage.BackingUp, "正在创建备份", relativePath: request.Target.RelativePath));
                File.Copy(target, target + request.BackupSuffix, true);
                backupCreated = true;
                File.Move(temporaryPath, target, true);
            }
            else
            {
                File.Move(temporaryPath, target);
            }

            temporaryPath = null;
            var hash = await ComputeHashAsync(target, cancellationToken).ConfigureAwait(false);
            return OperationResult<AtomicWriteSummary>.Success(new AtomicWriteSummary(request.Target.RelativePath, hash, backupCreated));
        }
        catch (OperationCanceledException)
        {
            return OperationResult<AtomicWriteSummary>.Cancelled(CancelledDiagnostic(request.Target.RelativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<AtomicWriteSummary>.Failure(FileDiagnostic("ATOMIC_WRITE_FAILED", ex.Message, request.Target.RelativePath));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public async Task<OperationResult<FileMoveSummary>> MoveAsync(
        FileMoveRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ValidateProjectPath(request.Source.Root, request.Source.RelativePath);
            var target = ValidateProjectPath(request.Target.Root, request.Target.RelativePath);
            if (!source.IsSuccess || source.Value is null)
                return new OperationResult<FileMoveSummary>(OperationStatus.Failed, null, source.Diagnostics);
            if (!target.IsSuccess || target.Value is null)
                return new OperationResult<FileMoveSummary>(OperationStatus.Failed, null, target.Diagnostics);
            if (!File.Exists(source.Value.FullPath))
                return OperationResult<FileMoveSummary>.Failure(FileDiagnostic("MOVE_SOURCE_MISSING", "源文件已不存在。", source.Value.RelativePath));
            if (File.Exists(target.Value.FullPath))
                return OperationResult<FileMoveSummary>.Failure(FileDiagnostic("MOVE_TARGET_EXISTS", "目标文件已经存在，未执行重命名。", target.Value.RelativePath));

            var currentHash = await ComputeHashAsync(source.Value.FullPath, cancellationToken).ConfigureAwait(false);
            if (!currentHash.Equals(request.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
                return OperationResult<FileMoveSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "EXTERNAL_MODIFICATION",
                    "源文件在预览后发生变化，已停止重命名。", source.Value.RelativePath, Category: DiagnosticCategory.FileSystem,
                    SuggestedAction: "重新生成预览后再执行。"));

            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Writing, "正在重命名", relativePath: source.Value.RelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(target.Value.FullPath)!);
            File.Move(source.Value.FullPath, target.Value.FullPath);
            return OperationResult<FileMoveSummary>.Success(new FileMoveSummary(source.Value.RelativePath, target.Value.RelativePath));
        }
        catch (OperationCanceledException)
        {
            return OperationResult<FileMoveSummary>.Cancelled(CancelledDiagnostic(request.Source.RelativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<FileMoveSummary>.Failure(FileDiagnostic("MOVE_FAILED", ex.Message, request.Source.RelativePath));
        }
    }

    public Task<OperationResult<IReadOnlyList<ValidatedProjectPath>>> EnumerateFilesAsync(
        ProjectRoot root, string relativeDirectory, string searchPattern, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var directory = ValidateProjectPath(root, relativeDirectory);
        if (!directory.IsSuccess || directory.Value is null)
        {
            return new OperationResult<IReadOnlyList<ValidatedProjectPath>>(OperationStatus.Failed, null, directory.Diagnostics);
        }

        try
        {
            var items = new List<ValidatedProjectPath>();
            if (!Directory.Exists(directory.Value.FullPath)) return OperationResult<IReadOnlyList<ValidatedProjectPath>>.Success(items);
            foreach (var file in Directory.EnumerateFiles(directory.Value.FullPath, searchPattern, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var validated = ValidateProjectPath(root, Path.GetRelativePath(root.FullPath, file));
                if (validated.IsSuccess && validated.Value is not null) items.Add(validated.Value);
            }
            return OperationResult<IReadOnlyList<ValidatedProjectPath>>.Success(items);
        }
        catch (OperationCanceledException) { return OperationResult<IReadOnlyList<ValidatedProjectPath>>.Cancelled(CancelledDiagnostic(relativeDirectory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return OperationResult<IReadOnlyList<ValidatedProjectPath>>.Failure(FileDiagnostic("ENUMERATE_FAILED", ex.Message, relativeDirectory)); }
    }, CancellationToken.None);

    private static bool HasEscapingReparsePoint(string root, string target)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(target)!);
        while (current is not null && current.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var resolved = current.ResolveLinkTarget(true);
                if (resolved is not null && !Path.GetFullPath(resolved.FullName).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            }
            current = current.Parent;
        }
        return false;
    }

    private static bool IsDevicePath(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal);
    private static async Task<string> ComputeHashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    private static Diagnostic FileDiagnostic(string code, string message, string? path, DiagnosticCategory category = DiagnosticCategory.FileSystem) => new(DiagnosticSeverity.Error, code, message, path, Category: category);
    private static Diagnostic SecurityDiagnostic(string code, string message, string? path) => new(DiagnosticSeverity.Error, code, message, path, Category: DiagnosticCategory.Security, SuggestedAction: "请选择项目目录内的安全路径。");
    private static Diagnostic CancelledDiagnostic(string? path) => new(DiagnosticSeverity.Info, "OPERATION_CANCELLED", "操作已取消。", path);
}
