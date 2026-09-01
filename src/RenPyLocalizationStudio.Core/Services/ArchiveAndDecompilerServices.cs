using System.Security.Cryptography;
using System.Text;

namespace RenPyLocalizationStudio.Core.Services;

public sealed record ArchiveExtractionRequest(ProjectRoot Project, ValidatedToolPath Python, ValidatedToolPath RpaTool,
    IReadOnlyList<string> ArchiveRelativePaths, string OutputRelativeDirectory, bool Confirmed, string? ConfirmedPlanFingerprint = null);
public sealed record ArchiveExtractionSummary(int ProcessedArchives, string OutputRelativeDirectory, IReadOnlyList<string> PreservedArchives);
public enum ToolPlanDisposition { Ready, Skipped, Error }
public sealed record ArchiveExtractionPlanItem(string ArchiveRelativePath, string EntryPath, string OutputRelativePath, ToolPlanDisposition Disposition, string Message);
public sealed record ArchiveExtractionPlan(IReadOnlyList<ArchiveExtractionPlanItem> Items, string Fingerprint = "",
    IReadOnlyDictionary<string, string>? ArchiveSha256 = null)
{
    public int ReadyCount => Items.Count(item => item.Disposition == ToolPlanDisposition.Ready);
    public int SkippedCount => Items.Count(item => item.Disposition == ToolPlanDisposition.Skipped);
}
public sealed record ScriptDecompileRequest(ProjectRoot Project, ValidatedToolPath Python, ValidatedToolPath Unrpyc,
    IReadOnlyList<string> ScriptRelativePaths, bool Confirmed, string? ConfirmedPlanFingerprint = null);
public sealed record ScriptDecompileSummary(int ProcessedScripts, IReadOnlyList<string> PreservedScripts);
public sealed record ScriptDecompilePlanItem(string SourceRelativePath, string OutputRelativePath, ToolPlanDisposition Disposition, string Message);
public sealed record ScriptDecompilePlan(IReadOnlyList<ScriptDecompilePlanItem> Items, string Fingerprint = "")
{
    public int ReadyCount => Items.Count(item => item.Disposition == ToolPlanDisposition.Ready);
    public int SkippedCount => Items.Count(item => item.Disposition == ToolPlanDisposition.Skipped);
}

public interface IArchiveExtractionService : IAsyncOperationService<ArchiveExtractionRequest, ArchiveExtractionSummary>
{
    Task<OperationResult<ArchiveExtractionPlan>> PlanAsync(ArchiveExtractionRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken);
}
public interface IScriptDecompilerService : IAsyncOperationService<ScriptDecompileRequest, ScriptDecompileSummary>
{
    Task<OperationResult<ScriptDecompilePlan>> PlanAsync(ScriptDecompileRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken);
}

public sealed class ArchiveExtractionService : IArchiveExtractionService
{
    private readonly IFileSystemService _fileSystem; private readonly IProcessRunnerService _runner;
    public ArchiveExtractionService(IFileSystemService fileSystem, IProcessRunnerService runner) { _fileSystem = fileSystem; _runner = runner; }
    public async Task<OperationResult<ArchiveExtractionPlan>> PlanAsync(ArchiveExtractionRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        var output = _fileSystem.ValidateProjectPath(request.Project, request.OutputRelativeDirectory);
        if (!output.IsSuccess || output.Value is null) return new(OperationStatus.Failed, null, output.Diagnostics);
        var python = _fileSystem.ValidateExecutable(request.Python.FullPath);
        if (!python.IsSuccess || python.Value is null) return new(OperationStatus.Failed, null, python.Diagnostics);
        var wrapper = Path.Combine(Path.GetDirectoryName(request.RpaTool.FullPath)!, "safe_rpa_extract.py");
        var rpaTool = _fileSystem.ValidateToolPath(request.RpaTool.FullPath);
        var wrapperTool = _fileSystem.ValidateToolPath(wrapper);
        if (!rpaTool.IsSuccess || rpaTool.Value is null) return new(OperationStatus.Failed, null, rpaTool.Diagnostics);
        if (!wrapperTool.IsSuccess || wrapperTool.Value is null) return OperationResult<ArchiveExtractionPlan>.Failure(Security("SAFE_EXTRACTOR_MISSING", "安全解包入口缺失或不安全。"));

        var items = new List<ArchiveExtractionPlanItem>();
        var diagnostics = new List<Diagnostic>();
        var archiveHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fingerprintMaterial = new StringBuilder()
            .Append(wrapperTool.Value.Sha256).Append('\n')
            .Append(rpaTool.Value.Sha256).Append('\n');
        foreach (var relative in request.ArchiveRelativePaths)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(OperationStatus.Cancelled, new ArchiveExtractionPlan(items), diagnostics);
            var archive = _fileSystem.ValidateProjectPath(request.Project, relative);
            if (!archive.IsSuccess || archive.Value is null)
            {
                diagnostics.AddRange(AsSkippedWarnings(archive.Diagnostics, "ARCHIVE_SKIPPED"));
                items.Add(new ArchiveExtractionPlanItem(relative, string.Empty, request.OutputRelativeDirectory,
                    ToolPlanDisposition.Error, "归档路径无效，已从本次计划跳过。"));
                continue;
            }
            var result = await _runner.ExecuteAsync(new ProcessExecutionPlan(python.Value, request.Project.FullPath,
                [wrapperTool.Value.FullPath, "--plan", rpaTool.Value.FullPath, archive.Value.FullPath, output.Value.FullPath],
                SafePythonEnvironment(), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(3), MaxCapturedOutputCharacters: 8_000_000),
                progress, cancellationToken).ConfigureAwait(false);
            if (result.Status == OperationStatus.Cancelled) return new(OperationStatus.Cancelled, new ArchiveExtractionPlan(items), diagnostics);
            if (!result.IsSuccess || result.Value is null)
            {
                diagnostics.AddRange(AsSkippedWarnings(result.Diagnostics, "ARCHIVE_SKIPPED"));
                items.Add(new ArchiveExtractionPlanItem(relative, string.Empty, request.OutputRelativeDirectory, ToolPlanDisposition.Error, "归档无法安全读取或包含非法路径。"));
                continue;
            }
            diagnostics.AddRange(result.Diagnostics);
            var archiveItemsBefore = items.Count;
            string? archiveHash = null;
            string? summaryHash = null;
            var expectedItemCount = -1;
            foreach (var line in result.Value.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', 5);
                if (parts.Length == 2 && parts[0] == "ARCHIVE")
                {
                    archiveHash = parts[1];
                    continue;
                }
                if (parts.Length == 3 && parts[0] == "SUMMARY" && int.TryParse(parts[1], out var parsedCount))
                {
                    expectedItemCount = parsedCount;
                    summaryHash = parts[2];
                    continue;
                }
                if (parts.Length < 4 || parts[0] != "PLAN") continue;
                var outputRelative = Path.GetRelativePath(request.Project.FullPath, parts[3]);
                var disposition = parts[1] == "SKIP" ? ToolPlanDisposition.Skipped : ToolPlanDisposition.Ready;
                items.Add(new ArchiveExtractionPlanItem(relative, parts[2], outputRelative, disposition,
                    disposition == ToolPlanDisposition.Skipped ? "目标已存在，将跳过。" : "将写入项目目录。"));
            }
            var parsedItemCount = items.Count - archiveItemsBefore;
            if (archiveHash is null || summaryHash is null || expectedItemCount != parsedItemCount)
            {
                if (parsedItemCount > 0)
                    items.RemoveRange(archiveItemsBefore, parsedItemCount);
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "ARCHIVE_PLAN_TRUNCATED",
                    "该归档的计划输出不完整，已从本次计划跳过。", relative,
                    Category: DiagnosticCategory.Security));
                items.Add(new ArchiveExtractionPlanItem(relative, string.Empty, request.OutputRelativeDirectory,
                    ToolPlanDisposition.Error, "归档计划输出不完整，已跳过。"));
                continue;
            }
            archiveHashes[relative] = archiveHash;
            fingerprintMaterial.Append(relative).Append('\t').Append(expectedItemCount).Append('\t').Append(summaryHash).Append('\n');
        }
        var status = diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
            ? OperationStatus.SucceededWithWarnings
            : OperationStatus.Succeeded;
        return new(status, new ArchiveExtractionPlan(items, HashText(fingerprintMaterial.ToString()), archiveHashes), diagnostics);
    }
    public async Task<OperationResult<ArchiveExtractionSummary>> ExecuteAsync(ArchiveExtractionRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!request.Confirmed || string.IsNullOrWhiteSpace(request.ConfirmedPlanFingerprint))
            return OperationResult<ArchiveExtractionSummary>.Failure(Security("ARCHIVE_CONFIRMATION_REQUIRED", "解包前必须确认当前文件计划。"));
        var currentPlan = await PlanAsync(request with { Confirmed = false, ConfirmedPlanFingerprint = null }, progress, cancellationToken).ConfigureAwait(false);
        if (!currentPlan.IsSuccess || currentPlan.Value is null)
            return new(OperationStatus.Failed, null, currentPlan.Diagnostics);
        if (!FingerprintsEqual(request.ConfirmedPlanFingerprint, currentPlan.Value.Fingerprint))
            return OperationResult<ArchiveExtractionSummary>.Failure(Security("ARCHIVE_PLAN_CHANGED", "归档、工具或输出状态在确认后发生变化，已停止解包。"));
        var output = _fileSystem.ValidateProjectPath(request.Project, request.OutputRelativeDirectory);
        if (!output.IsSuccess || output.Value is null) return new(OperationStatus.Failed, null, output.Diagnostics);
        var python = _fileSystem.ValidateExecutable(request.Python.FullPath);
        if (!python.IsSuccess || python.Value is null) return new(OperationStatus.Failed, null, python.Diagnostics);
        var rpaTool = _fileSystem.ValidateToolPath(request.RpaTool.FullPath);
        var wrapperTool = _fileSystem.ValidateToolPath(Path.Combine(Path.GetDirectoryName(request.RpaTool.FullPath)!, "safe_rpa_extract.py"));
        if (!rpaTool.IsSuccess || rpaTool.Value is null) return new(OperationStatus.Failed, null, rpaTool.Diagnostics);
        if (!wrapperTool.IsSuccess || wrapperTool.Value is null) return new(OperationStatus.Failed, null, wrapperTool.Diagnostics);
        var diagnostics = new List<Diagnostic>(); var processed = 0; var preserved = new List<string>();
        foreach (var relative in request.ArchiveRelativePaths)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(OperationStatus.Cancelled, new ArchiveExtractionSummary(processed, request.OutputRelativeDirectory, preserved), diagnostics);
            var archive = _fileSystem.ValidateProjectPath(request.Project, relative);
            if (!archive.IsSuccess || archive.Value is null) { diagnostics.AddRange(archive.Diagnostics); continue; }
            if (currentPlan.Value.ArchiveSha256 is null || !currentPlan.Value.ArchiveSha256.TryGetValue(relative, out var expectedArchiveHash))
            {
                diagnostics.Add(Security("ARCHIVE_PLAN_HASH_MISSING", "确认计划缺少归档哈希，已停止该归档解包。"));
                continue;
            }
            var result = await _runner.ExecuteAsync(new ProcessExecutionPlan(python.Value, request.Project.FullPath,
                [wrapperTool.Value.FullPath, "--expected-sha256", expectedArchiveHash, rpaTool.Value.FullPath, archive.Value.FullPath, output.Value.FullPath], SafePythonEnvironment(),
                TimeSpan.FromHours(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(3)), progress, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics); if (result.IsSuccess) processed++; preserved.Add(relative);
        }
        var status = diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? OperationStatus.Failed : diagnostics.Count > 0 ? OperationStatus.SucceededWithWarnings : OperationStatus.Succeeded;
        return new(status, new ArchiveExtractionSummary(processed, request.OutputRelativeDirectory, preserved), diagnostics);
    }
    private static IReadOnlyDictionary<string, string?> SafePythonEnvironment() => new Dictionary<string, string?>
    {
        ["PYTHONUTF8"] = "1",
        ["PYTHONNOUSERSITE"] = "1",
        ["PYTHONDONTWRITEBYTECODE"] = "1",
        ["SYSTEMROOT"] = Environment.GetEnvironmentVariable("SYSTEMROOT"),
        ["TEMP"] = Path.GetTempPath(),
        ["TMP"] = Path.GetTempPath()
    };
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FingerprintsEqual(string expected, string actual)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)); }
        catch (FormatException) { return false; }
    }
    private static Diagnostic Security(string code, string message) => new(DiagnosticSeverity.Error, code, message, Category: DiagnosticCategory.Security);
    private static IEnumerable<Diagnostic> AsSkippedWarnings(IEnumerable<Diagnostic> diagnostics, string prefix) =>
        diagnostics.Select(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
            ? diagnostic with
            {
                Severity = DiagnosticSeverity.Warning,
                Code = $"{prefix}_{diagnostic.Code}",
                Message = $"该项已跳过：{diagnostic.Message}"
            }
            : diagnostic);
}

public sealed class ScriptDecompilerService : IScriptDecompilerService
{
    private readonly IFileSystemService _fileSystem; private readonly IProcessRunnerService _runner;
    public ScriptDecompilerService(IFileSystemService fileSystem, IProcessRunnerService runner) { _fileSystem = fileSystem; _runner = runner; }
    public async Task<OperationResult<ScriptDecompilePlan>> PlanAsync(ScriptDecompileRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        var items = new List<ScriptDecompilePlanItem>();
        var diagnostics = new List<Diagnostic>();
        var python = _fileSystem.ValidateExecutable(request.Python.FullPath);
        var unrpyc = _fileSystem.ValidateToolPath(request.Unrpyc.FullPath);
        if (!python.IsSuccess || python.Value is null) return new(OperationStatus.Failed, null, python.Diagnostics);
        if (!unrpyc.IsSuccess || unrpyc.Value is null) return new(OperationStatus.Failed, null, unrpyc.Diagnostics);
        var fingerprintMaterial = new StringBuilder()
            .Append(unrpyc.Value.Sha256).Append('\n');
        foreach (var relative in request.ScriptRelativePaths)
        {
            if (cancellationToken.IsCancellationRequested)
                return new OperationResult<ScriptDecompilePlan>(OperationStatus.Cancelled, new ScriptDecompilePlan(items), diagnostics);
            var source = _fileSystem.ValidateProjectPath(request.Project, relative);
            if (!source.IsSuccess || source.Value is null)
            {
                diagnostics.AddRange(AsSkippedWarnings(source.Diagnostics, "DECOMPILE_SKIPPED"));
                items.Add(new ScriptDecompilePlanItem(relative, string.Empty, ToolPlanDisposition.Error, "源脚本路径无效。"));
                continue;
            }
            var sourceBytes = await _fileSystem.ReadBytesAsync(source.Value, cancellationToken).ConfigureAwait(false);
            if (!sourceBytes.IsSuccess || sourceBytes.Value is null)
            {
                diagnostics.AddRange(AsSkippedWarnings(sourceBytes.Diagnostics, "DECOMPILE_SKIPPED"));
                items.Add(new ScriptDecompilePlanItem(relative, string.Empty, ToolPlanDisposition.Error, "无法读取源脚本。"));
                continue;
            }
            var output = GetDecompilerOutputPath(source.Value.RelativePath);
            var outputPath = _fileSystem.ValidateProjectPath(request.Project, output);
            if (!outputPath.IsSuccess || outputPath.Value is null)
            {
                diagnostics.AddRange(AsSkippedWarnings(outputPath.Diagnostics, "DECOMPILE_SKIPPED"));
                items.Add(new ScriptDecompilePlanItem(relative, output, ToolPlanDisposition.Error, "输出路径无效。"));
                continue;
            }
            var existsResult = await _fileSystem.ExistsAsync(outputPath.Value, cancellationToken).ConfigureAwait(false);
            if (!existsResult.IsSuccess)
            {
                diagnostics.AddRange(AsSkippedWarnings(existsResult.Diagnostics, "DECOMPILE_SKIPPED"));
                items.Add(new ScriptDecompilePlanItem(relative, output, ToolPlanDisposition.Error, "无法检查输出文件状态。"));
                continue;
            }
            var exists = existsResult.Value;
            fingerprintMaterial.Append(relative).Append('\t').Append(sourceBytes.Value.Sha256).Append('\t').Append(exists).Append('\n');
            items.Add(new ScriptDecompilePlanItem(relative, output, exists ? ToolPlanDisposition.Skipped : ToolPlanDisposition.Ready,
                exists ? "对应源码已存在，将跳过。" : "将生成可读源码。"));
        }
        progress.Report(ToolOperationProgress.Create(ToolOperationStage.Planning, "反编译逐项计划已生成", items.Count, items.Count));
        var status = diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
            ? OperationStatus.SucceededWithWarnings
            : OperationStatus.Succeeded;
        return new OperationResult<ScriptDecompilePlan>(status, new ScriptDecompilePlan(items, HashText(fingerprintMaterial.ToString())), diagnostics);
    }
    public async Task<OperationResult<ScriptDecompileSummary>> ExecuteAsync(ScriptDecompileRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!request.Confirmed || string.IsNullOrWhiteSpace(request.ConfirmedPlanFingerprint))
            return OperationResult<ScriptDecompileSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "DECOMPILE_CONFIRMATION_REQUIRED", "反编译前必须确认当前文件计划。", Category: DiagnosticCategory.Security));
        var currentPlan = await PlanAsync(request with { Confirmed = false, ConfirmedPlanFingerprint = null }, progress, cancellationToken).ConfigureAwait(false);
        if (!currentPlan.IsSuccess || currentPlan.Value is null) return new(OperationStatus.Failed, null, currentPlan.Diagnostics);
        if (!FingerprintsEqual(request.ConfirmedPlanFingerprint, currentPlan.Value.Fingerprint))
            return OperationResult<ScriptDecompileSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "DECOMPILE_PLAN_CHANGED", "源脚本、工具或输出状态在确认后发生变化，已停止反编译。", Category: DiagnosticCategory.Security));
        var python = _fileSystem.ValidateExecutable(request.Python.FullPath);
        if (!python.IsSuccess || python.Value is null) return new(OperationStatus.Failed, null, python.Diagnostics);
        var unrpyc = _fileSystem.ValidateToolPath(request.Unrpyc.FullPath);
        if (!unrpyc.IsSuccess || unrpyc.Value is null) return new(OperationStatus.Failed, null, unrpyc.Diagnostics);
        var diagnostics = new List<Diagnostic>(); var processed = 0; var preserved = new List<string>();
        foreach (var relative in request.ScriptRelativePaths)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(OperationStatus.Cancelled, new ScriptDecompileSummary(processed, preserved), diagnostics);
            var source = _fileSystem.ValidateProjectPath(request.Project, relative);
            if (!source.IsSuccess || source.Value is null) { diagnostics.AddRange(source.Diagnostics); continue; }
            var outputRelative = GetDecompilerOutputPath(source.Value.RelativePath);
            var output = _fileSystem.ValidateProjectPath(request.Project, outputRelative);
            if (!output.IsSuccess || output.Value is null) { diagnostics.AddRange(output.Diagnostics); continue; }
            var exists = await _fileSystem.ExistsAsync(output.Value, cancellationToken).ConfigureAwait(false);
            if (!exists.IsSuccess) { diagnostics.AddRange(exists.Diagnostics); continue; }
            if (exists.Value) { diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "DECOMPILE_OUTPUT_EXISTS", "对应 .rpy 已存在，已跳过。", relative)); preserved.Add(relative); continue; }
            var result = await _runner.ExecuteAsync(new ProcessExecutionPlan(python.Value, request.Project.FullPath,
                [unrpyc.Value.FullPath, source.Value.FullPath], SafePythonEnvironment(), TimeSpan.FromHours(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(3)), progress, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics);
            if (result.IsSuccess)
            {
                var generated = await _fileSystem.ExistsAsync(output.Value, cancellationToken).ConfigureAwait(false);
                if (!generated.IsSuccess || !generated.Value)
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "DECOMPILE_OUTPUT_MISSING", "工具返回成功，但没有生成预期源码文件。", outputRelative, Category: DiagnosticCategory.Process));
                else processed++;
            }
            preserved.Add(relative);
        }
        var status = diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? OperationStatus.Failed : diagnostics.Count > 0 ? OperationStatus.SucceededWithWarnings : OperationStatus.Succeeded;
        return new(status, new ScriptDecompileSummary(processed, preserved), diagnostics);
    }
    private static string GetDecompilerOutputPath(string source) =>
        Path.ChangeExtension(source, source.EndsWith(".rpymc", StringComparison.OrdinalIgnoreCase) ? ".rpym" : ".rpy");
    private static IReadOnlyDictionary<string, string?> SafePythonEnvironment() => new Dictionary<string, string?>
    {
        ["PYTHONUTF8"] = "1",
        ["PYTHONNOUSERSITE"] = "1",
        ["PYTHONDONTWRITEBYTECODE"] = "1",
        ["SYSTEMROOT"] = Environment.GetEnvironmentVariable("SYSTEMROOT"),
        ["TEMP"] = Path.GetTempPath(),
        ["TMP"] = Path.GetTempPath()
    };
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FingerprintsEqual(string expected, string actual)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)); }
        catch (FormatException) { return false; }
    }
    private static IEnumerable<Diagnostic> AsSkippedWarnings(IEnumerable<Diagnostic> diagnostics, string prefix) =>
        diagnostics.Select(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
            ? diagnostic with
            {
                Severity = DiagnosticSeverity.Warning,
                Code = $"{prefix}_{diagnostic.Code}",
                Message = $"该项已跳过：{diagnostic.Message}"
            }
            : diagnostic);
}
