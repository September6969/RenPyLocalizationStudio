using System.Security.Cryptography;

namespace RenPyLocalizationStudio.Core.Services;

public sealed record ProjectRoot(string FullPath);
public sealed record ValidatedProjectPath(ProjectRoot Root, string FullPath, string RelativePath);
public sealed record ValidatedExecutablePath(string FullPath);
public sealed record ValidatedToolPath(string FullPath, string Sha256 = "");
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
    OperationResult<ValidatedToolPath> ValidateToolPath(string path);
    Task<OperationResult<Utf8TextFile>> ReadUtf8Async(ValidatedProjectPath path, CancellationToken cancellationToken);
    Task<OperationResult<BinaryFileContent>> ReadBytesAsync(ValidatedProjectPath path, CancellationToken cancellationToken);
    Task<OperationResult<bool>> ExistsAsync(ValidatedProjectPath path, CancellationToken cancellationToken);
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
    Task<OperationResult<IReadOnlyList<string>>> EnumerateDirectoriesAsync(
        ProjectRoot root,
        string relativeDirectory,
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
            var rootInfo = new DirectoryInfo(full);
            if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return OperationResult<ProjectRoot>.Failure(SecurityDiagnostic("PROJECT_ROOT_REPARSE_POINT", "项目根目录不能是符号链接或目录联接。", projectRoot));
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
            if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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

    public OperationResult<ValidatedToolPath> ValidateToolPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith("\\\\", StringComparison.Ordinal) || IsDevicePath(full) || !File.Exists(full))
                return OperationResult<ValidatedToolPath>.Failure(SecurityDiagnostic("TOOL_PATH_INVALID", "工具文件不存在，或位于不允许的 UNC/设备路径。", path));
            var info = new FileInfo(full);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return OperationResult<ValidatedToolPath>.Failure(SecurityDiagnostic("TOOL_PATH_REPARSE_POINT", "工具文件不能是符号链接。", path));
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            return OperationResult<ValidatedToolPath>.Success(new ValidatedToolPath(full, Convert.ToHexString(SHA256.HashData(stream))));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return OperationResult<ValidatedToolPath>.Failure(FileDiagnostic("TOOL_PATH_INVALID", ex.Message, path));
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

    public Task<OperationResult<bool>> ExistsAsync(ValidatedProjectPath path, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(OperationResult<bool>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "OPERATION_CANCELLED", "操作已取消。", path.RelativePath)));
        return Task.FromResult(OperationResult<bool>.Success(File.Exists(path.FullPath)));
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
            if (request.ExpectedSha256 is not null)
            {
                if (!File.Exists(target))
                {
                    return OperationResult<AtomicWriteSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "EXTERNAL_MODIFICATION",
                        "目标文件在预览后已被删除，已停止写入。", request.Target.RelativePath, Category: DiagnosticCategory.FileSystem,
                        SuggestedAction: "重新生成预览后再保存。"));
                }
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
            // 替换完成即进入提交态，不能因随后取消哈希读取而把已落盘文件误报为未保存。
            var hash = Convert.ToHexString(SHA256.HashData(request.Content));
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

    public async Task<OperationResult<IReadOnlyList<ValidatedProjectPath>>> EnumerateFilesAsync(
        ProjectRoot root, string relativeDirectory, string searchPattern, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                var directory = ValidateProjectPath(root, relativeDirectory);
                if (!directory.IsSuccess || directory.Value is null)
                    return new OperationResult<IReadOnlyList<ValidatedProjectPath>>(OperationStatus.Failed, null, directory.Diagnostics);

                var items = new List<ValidatedProjectPath>();
                var diagnostics = new List<Diagnostic>();
                if (!Directory.Exists(directory.Value.FullPath)) return OperationResult<IReadOnlyList<ValidatedProjectPath>>.Success(items);
                var pending = new Stack<string>();
                pending.Push(directory.Value.FullPath);
                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = pending.Pop();
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(current, searchPattern, SearchOption.TopDirectoryOnly))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var validated = ValidateProjectPath(root, Path.GetRelativePath(root.FullPath, file));
                            if (validated.IsSuccess && validated.Value is not null) items.Add(validated.Value);
                            else diagnostics.AddRange(validated.Diagnostics);
                        }

                        foreach (var child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var info = new DirectoryInfo(child);
                            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            pending.Push(child);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "ENUMERATE_DIRECTORY_SKIPPED",
                            "目录无法访问，分析结果可能不完整。", Path.GetRelativePath(root.FullPath, current),
                            Category: DiagnosticCategory.FileSystem, TechnicalDetails: exception.Message));
                    }
                }
                return OperationResult<IReadOnlyList<ValidatedProjectPath>>.Success(items, diagnostics);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return OperationResult<IReadOnlyList<ValidatedProjectPath>>.Cancelled(CancelledDiagnostic(relativeDirectory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return OperationResult<IReadOnlyList<ValidatedProjectPath>>.Failure(FileDiagnostic("ENUMERATE_FAILED", ex.Message, relativeDirectory)); }
    }

    public async Task<OperationResult<IReadOnlyList<string>>> EnumerateDirectoriesAsync(
        ProjectRoot root, string relativeDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                var directory = ValidateProjectPath(root, relativeDirectory);
                if (!directory.IsSuccess || directory.Value is null)
                    return new OperationResult<IReadOnlyList<string>>(OperationStatus.Failed, null, directory.Diagnostics);

                if (!Directory.Exists(directory.Value.FullPath)) return OperationResult<IReadOnlyList<string>>.Success([]);

                var items = new List<string>();
                foreach (var child in Directory.EnumerateDirectories(directory.Value.FullPath, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var validated = ValidateProjectPath(root, Path.GetRelativePath(root.FullPath, child));
                    if (validated.IsSuccess && validated.Value is not null) items.Add(Path.GetFileName(validated.Value.FullPath));
                }
                return OperationResult<IReadOnlyList<string>>.Success(items.Order(StringComparer.OrdinalIgnoreCase).ToArray());
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<IReadOnlyList<string>>.Cancelled(CancelledDiagnostic(relativeDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<IReadOnlyList<string>>.Failure(FileDiagnostic("ENUMERATE_DIRECTORIES_FAILED", ex.Message, relativeDirectory));
        }
    }

    private static bool HasEscapingReparsePoint(string root, string target)
    {
        var file = new FileInfo(target);
        if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            var resolvedFile = file.ResolveLinkTarget(true);
            if (resolvedFile is not null && !IsWithinRoot(root, resolvedFile.FullName)) return true;
        }

        var directory = new DirectoryInfo(target);
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            var resolvedDirectory = directory.ResolveLinkTarget(true);
            if (resolvedDirectory is not null && !IsWithinRoot(root, resolvedDirectory.FullName)) return true;
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(target)!);
        while (current is not null && current.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var resolved = current.ResolveLinkTarget(true);
                if (resolved is not null && !IsWithinRoot(root, resolved.FullName)) return true;
            }
            current = current.Parent;
        }
        return false;
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
