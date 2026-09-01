using SkiaSharp;

namespace RenPyLocalizationStudio.Core.Services;

public sealed record PrefixRenamePlanRequest(ProjectRoot Root, string RelativeDirectory, string Prefix, bool Recursive = true);
public sealed record PrefixRenameItem(string SourceRelativePath, string TargetRelativePath, string SourceSha256, long Length, bool HasConflict);
public sealed record PrefixRenamePlan(PrefixRenamePlanRequest Request, IReadOnlyList<PrefixRenameItem> Items)
{
    public int ReadyCount => Items.Count(x => !x.HasConflict);
    public int ConflictCount => Items.Count(x => x.HasConflict);
}
public sealed record PrefixRenameExecutionRequest(PrefixRenamePlan Plan);
public sealed record PrefixRenameSummary(int RenamedFiles, int SkippedFiles);

public interface IPrefixRenameService
{
    Task<OperationResult<PrefixRenamePlan>> PlanAsync(
        PrefixRenamePlanRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken);

    Task<OperationResult<PrefixRenameSummary>> ExecuteAsync(
        PrefixRenameExecutionRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken);
}

public sealed class PrefixRenameService(IFileSystemService fileSystem) : IPrefixRenameService
{
    public async Task<OperationResult<PrefixRenamePlan>> PlanAsync(
        PrefixRenamePlanRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Prefix) || request.Prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return OperationResult<PrefixRenamePlan>.Failure(new Diagnostic(DiagnosticSeverity.Error, "RENAME_PREFIX_INVALID",
                "待移除前缀不能为空，也不能包含文件名非法字符。", Category: DiagnosticCategory.FileSystem));

        var enumeration = await fileSystem.EnumerateFilesAsync(request.Root, request.RelativeDirectory, "*", cancellationToken).ConfigureAwait(false);
        if (!enumeration.IsSuccess || enumeration.Value is null)
            return new OperationResult<PrefixRenamePlan>(enumeration.Status, null, enumeration.Diagnostics);

        var allFiles = enumeration.Value;
        var existing = allFiles.Select(x => Normalize(x.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = allFiles.Where(x => IsInScope(x, request) && Path.GetFileName(x.FullPath).StartsWith(request.Prefix, StringComparison.Ordinal)).ToArray();
        var items = new List<PrefixRenameItem>(candidates.Length);
        var diagnostics = new List<Diagnostic>();

        for (var index = 0; index < candidates.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return OperationResult<PrefixRenamePlan>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "OPERATION_CANCELLED", "重命名预览已取消。"));
            var source = candidates[index];
            var newName = Path.GetFileName(source.FullPath)[request.Prefix.Length..];
            if (string.IsNullOrWhiteSpace(newName))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "RENAME_EMPTY_NAME", "移除前缀后文件名为空，已跳过。",
                    source.RelativePath, Category: DiagnosticCategory.FileSystem));
                continue;
            }

            var directory = Path.GetDirectoryName(source.RelativePath) ?? string.Empty;
            var targetRelative = Path.Combine(directory, newName);
            var target = fileSystem.ValidateProjectPath(request.Root, targetRelative);
            if (!target.IsSuccess || target.Value is null)
            {
                diagnostics.AddRange(target.Diagnostics);
                continue;
            }

            var content = await fileSystem.ReadBytesAsync(source, cancellationToken).ConfigureAwait(false);
            if (!content.IsSuccess || content.Value is null)
            {
                diagnostics.AddRange(content.Diagnostics);
                continue;
            }

            var conflict = existing.Contains(Normalize(target.Value.RelativePath));
            if (conflict)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "RENAME_TARGET_CONFLICT", "目标文件已存在，此项不会执行。",
                    target.Value.RelativePath, Category: DiagnosticCategory.FileSystem, SuggestedAction: "先处理同名文件，再刷新预览。"));
            items.Add(new PrefixRenameItem(source.RelativePath, target.Value.RelativePath, content.Value.Sha256, content.Value.Content.LongLength, conflict));
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Planning, "正在规划文件重命名", index + 1, candidates.Length, source.RelativePath));
        }

        return OperationResult<PrefixRenamePlan>.Success(new PrefixRenamePlan(request, items), diagnostics);
    }

    public async Task<OperationResult<PrefixRenameSummary>> ExecuteAsync(
        PrefixRenameExecutionRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var renamed = 0;
        var skipped = 0;
        var diagnostics = new List<Diagnostic>();
        var ready = request.Plan.Items.Where(x => !x.HasConflict).ToArray();
        foreach (var item in request.Plan.Items.Where(x => x.HasConflict)) skipped++;

        for (var index = 0; index < ready.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return OperationResult<PrefixRenameSummary>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "OPERATION_CANCELLED", "批量重命名已取消。"));
            var source = fileSystem.ValidateProjectPath(request.Plan.Request.Root, ready[index].SourceRelativePath);
            var target = fileSystem.ValidateProjectPath(request.Plan.Request.Root, ready[index].TargetRelativePath);
            if (!source.IsSuccess || source.Value is null || !target.IsSuccess || target.Value is null)
            {
                diagnostics.AddRange(source.Diagnostics.Concat(target.Diagnostics));
                skipped++;
                continue;
            }

            var result = await fileSystem.MoveAsync(new FileMoveRequest(source.Value, target.Value, ready[index].SourceSha256), progress, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess) renamed++;
            else { skipped++; diagnostics.AddRange(result.Diagnostics); }
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Writing, "正在执行批量重命名", index + 1, ready.Length, ready[index].SourceRelativePath));
        }

        var summary = new PrefixRenameSummary(renamed, skipped);
        return diagnostics.Count == 0
            ? OperationResult<PrefixRenameSummary>.Success(summary)
            : new OperationResult<PrefixRenameSummary>(
                diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? OperationStatus.Failed : OperationStatus.SucceededWithWarnings,
                summary,
                diagnostics);
    }

    private static bool IsInScope(ValidatedProjectPath path, PrefixRenamePlanRequest request)
    {
        if (request.Recursive) return true;
        var requested = Normalize(request.RelativeDirectory).TrimEnd('/');
        var parent = Normalize(Path.GetDirectoryName(path.RelativePath) ?? string.Empty).TrimEnd('/');
        return parent.Equals(requested, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

public enum ImageCompressionDisposition { Ready, SkippedNoSaving, Unsupported, Conflict }
public sealed record ImageCompressionPlanRequest(
    ProjectRoot Root,
    string RelativeDirectory,
    int Quality = 85,
    double MinimumSavingPercent = 1,
    bool ReplaceOriginals = false,
    string OutputDirectory = "game/rls-compressed",
    bool Recursive = true);
public sealed record ImageCompressionItem(
    string SourceRelativePath,
    string TargetRelativePath,
    string SourceSha256,
    long OriginalBytes,
    long CompressedBytes,
    ImageCompressionDisposition Disposition,
    string Message)
{
    public long SavedBytes => Math.Max(0, OriginalBytes - CompressedBytes);
    public double SavingPercent => OriginalBytes == 0 ? 0 : SavedBytes * 100d / OriginalBytes;
}
public sealed record ImageCompressionPlan(ImageCompressionPlanRequest Request, IReadOnlyList<ImageCompressionItem> Items)
{
    public int ReadyCount => Items.Count(x => x.Disposition == ImageCompressionDisposition.Ready);
    public long TotalSavedBytes => Items.Where(x => x.Disposition == ImageCompressionDisposition.Ready).Sum(x => x.SavedBytes);
}
public sealed record ImageCompressionExecutionRequest(ImageCompressionPlan Plan);
public sealed record ImageCompressionSummary(int CompressedFiles, int SkippedFiles, long SavedBytes);

public interface IImageCompressionService
{
    Task<OperationResult<ImageCompressionPlan>> PlanAsync(
        ImageCompressionPlanRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken);

    Task<OperationResult<ImageCompressionSummary>> ExecuteAsync(
        ImageCompressionExecutionRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken);
}

public sealed class ImageCompressionService(IFileSystemService fileSystem) : IImageCompressionService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    public async Task<OperationResult<ImageCompressionPlan>> PlanAsync(
        ImageCompressionPlanRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (request.Quality is < 1 or > 100 || request.MinimumSavingPercent is < 0 or > 100)
            return OperationResult<ImageCompressionPlan>.Failure(new Diagnostic(DiagnosticSeverity.Error, "IMAGE_OPTIONS_INVALID",
                "图片质量必须为 1–100，最小节省比例必须为 0–100。", Category: DiagnosticCategory.FileSystem));
        if (!request.ReplaceOriginals && string.IsNullOrWhiteSpace(request.OutputDirectory))
            return OperationResult<ImageCompressionPlan>.Failure(new Diagnostic(DiagnosticSeverity.Error, "IMAGE_OUTPUT_REQUIRED",
                "不覆盖原图时必须指定项目内输出目录。", Category: DiagnosticCategory.FileSystem));

        var enumeration = await fileSystem.EnumerateFilesAsync(request.Root, request.RelativeDirectory, "*", cancellationToken).ConfigureAwait(false);
        if (!enumeration.IsSuccess || enumeration.Value is null)
            return new OperationResult<ImageCompressionPlan>(enumeration.Status, null, enumeration.Diagnostics);
        var all = enumeration.Value;
        var existing = all.Select(x => Normalize(x.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!request.ReplaceOriginals)
        {
            var outputs = await fileSystem.EnumerateFilesAsync(request.Root, request.OutputDirectory, "*", cancellationToken).ConfigureAwait(false);
            if (outputs.Value is not null) foreach (var output in outputs.Value) existing.Add(Normalize(output.RelativePath));
        }

        var sources = all.Where(x => IsInScope(x, request) && SupportedExtensions.Contains(Path.GetExtension(x.FullPath))).ToArray();
        var items = new List<ImageCompressionItem>(sources.Length);
        var diagnostics = new List<Diagnostic>();
        for (var index = 0; index < sources.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return OperationResult<ImageCompressionPlan>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "OPERATION_CANCELLED", "图片压缩预览已取消。"));
            var source = sources[index];
            var read = await fileSystem.ReadBytesAsync(source, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess || read.Value is null) { diagnostics.AddRange(read.Diagnostics); continue; }
            var targetRelative = request.ReplaceOriginals
                ? source.RelativePath
                : Path.Combine(request.OutputDirectory, Path.GetRelativePath(request.RelativeDirectory, source.RelativePath));
            var target = fileSystem.ValidateProjectPath(request.Root, targetRelative);
            if (!target.IsSuccess || target.Value is null) { diagnostics.AddRange(target.Diagnostics); continue; }

            var compressed = Compress(read.Value.Content, Path.GetExtension(source.FullPath), request.Quality, out var reason);
            if (compressed is null)
            {
                items.Add(new ImageCompressionItem(source.RelativePath, target.Value.RelativePath, read.Value.Sha256,
                    read.Value.Content.LongLength, read.Value.Content.LongLength, ImageCompressionDisposition.Unsupported, reason));
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "IMAGE_UNSUPPORTED", reason, source.RelativePath, Category: DiagnosticCategory.FileSystem));
                continue;
            }

            var saving = read.Value.Content.Length == 0 ? 0 : (read.Value.Content.Length - compressed.Length) * 100d / read.Value.Content.Length;
            var conflict = !request.ReplaceOriginals && existing.Contains(Normalize(target.Value.RelativePath));
            var disposition = conflict ? ImageCompressionDisposition.Conflict
                : saving < request.MinimumSavingPercent || compressed.Length >= read.Value.Content.Length
                    ? ImageCompressionDisposition.SkippedNoSaving : ImageCompressionDisposition.Ready;
            var message = conflict ? "输出文件已存在" : disposition == ImageCompressionDisposition.Ready ? $"预计节省 {saving:F1}%" : "压缩后没有达到最小节省比例";
            if (conflict)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "IMAGE_TARGET_CONFLICT", "输出文件已存在，此项不会覆盖。",
                    target.Value.RelativePath, Category: DiagnosticCategory.FileSystem));
            items.Add(new ImageCompressionItem(source.RelativePath, target.Value.RelativePath, read.Value.Sha256,
                read.Value.Content.LongLength, compressed.LongLength, disposition, message));
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Planning, "正在生成图片压缩计划", index + 1, sources.Length, source.RelativePath));
        }

        return OperationResult<ImageCompressionPlan>.Success(new ImageCompressionPlan(request, items), diagnostics);
    }

    public async Task<OperationResult<ImageCompressionSummary>> ExecuteAsync(
        ImageCompressionExecutionRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var completed = 0;
        var skipped = request.Plan.Items.Count(x => x.Disposition != ImageCompressionDisposition.Ready);
        long saved = 0;
        var ready = request.Plan.Items.Where(x => x.Disposition == ImageCompressionDisposition.Ready).ToArray();
        for (var index = 0; index < ready.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return OperationResult<ImageCompressionSummary>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "OPERATION_CANCELLED", "图片压缩已取消。"));
            var item = ready[index];
            var source = fileSystem.ValidateProjectPath(request.Plan.Request.Root, item.SourceRelativePath);
            var target = fileSystem.ValidateProjectPath(request.Plan.Request.Root, item.TargetRelativePath);
            if (!source.IsSuccess || source.Value is null || !target.IsSuccess || target.Value is null)
            {
                diagnostics.AddRange(source.Diagnostics.Concat(target.Diagnostics)); skipped++; continue;
            }
            var read = await fileSystem.ReadBytesAsync(source.Value, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess || read.Value is null) { diagnostics.AddRange(read.Diagnostics); skipped++; continue; }
            if (!read.Value.Sha256.Equals(item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "EXTERNAL_MODIFICATION", "原图在预览后发生变化，已跳过。",
                    item.SourceRelativePath, Category: DiagnosticCategory.FileSystem)); skipped++; continue;
            }
            var compressed = Compress(read.Value.Content, Path.GetExtension(source.Value.FullPath), request.Plan.Request.Quality, out var reason);
            if (compressed is null) { diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "IMAGE_UNSUPPORTED", reason, item.SourceRelativePath)); skipped++; continue; }

            var write = await fileSystem.AtomicWriteAsync(new AtomicWriteRequest(target.Value, compressed,
                request.Plan.Request.ReplaceOriginals ? item.SourceSha256 : null, ".rls.bak", !request.Plan.Request.ReplaceOriginals), progress, cancellationToken).ConfigureAwait(false);
            if (write.IsSuccess) { completed++; saved += Math.Max(0, read.Value.Content.LongLength - compressed.LongLength); }
            else { diagnostics.AddRange(write.Diagnostics); skipped++; }
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Writing, "正在压缩图片", index + 1, ready.Length, item.SourceRelativePath));
        }
        var summary = new ImageCompressionSummary(completed, skipped, saved);
        return diagnostics.Count == 0
            ? OperationResult<ImageCompressionSummary>.Success(summary)
            : new OperationResult<ImageCompressionSummary>(
                diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? OperationStatus.Failed : OperationStatus.SucceededWithWarnings,
                summary,
                diagnostics);
    }

    private static byte[]? Compress(byte[] content, string extension, int quality, out string reason)
    {
        reason = string.Empty;
        try
        {
            using var stream = new SKMemoryStream(content);
            using var codec = SKCodec.Create(stream);
            if (codec is null) { reason = "无法识别图片编码"; return null; }
            if (codec.FrameCount > 1) { reason = "动画图片不会被扁平化压缩"; return null; }
            using var bitmap = SKBitmap.Decode(content);
            if (bitmap is null) { reason = "图片解码失败"; return null; }
            using var image = SKImage.FromBitmap(bitmap);
            var format = extension.ToLowerInvariant() switch
            {
                ".png" => SKEncodedImageFormat.Png,
                ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
                ".webp" => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Png
            };
            using var encoded = image.Encode(format, quality);
            if (encoded is null) { reason = "图片编码失败"; return null; }
            return encoded.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            reason = $"图片处理失败：{ex.Message}";
            return null;
        }
    }

    private static bool IsInScope(ValidatedProjectPath path, ImageCompressionPlanRequest request)
    {
        if (request.Recursive) return true;
        var requested = Normalize(request.RelativeDirectory).TrimEnd('/');
        var parent = Normalize(Path.GetDirectoryName(path.RelativePath) ?? string.Empty).TrimEnd('/');
        return parent.Equals(requested, StringComparison.OrdinalIgnoreCase);
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
}
