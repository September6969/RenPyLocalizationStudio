namespace RenPyLocalizationStudio.Core.Services;

public sealed record ArchiveExtractionRequest(ProjectRoot Project, ValidatedToolPath Python, ValidatedToolPath RpaTool,
    IReadOnlyList<string> ArchiveRelativePaths, string OutputRelativeDirectory, bool Confirmed);
public sealed record ArchiveExtractionSummary(int ProcessedArchives, string OutputRelativeDirectory, IReadOnlyList<string> PreservedArchives);
public sealed record ScriptDecompileRequest(ProjectRoot Project, ValidatedToolPath Python, ValidatedToolPath Unrpyc,
    IReadOnlyList<string> ScriptRelativePaths, bool Confirmed);
public sealed record ScriptDecompileSummary(int ProcessedScripts, IReadOnlyList<string> PreservedScripts);

public interface IArchiveExtractionService : IAsyncOperationService<ArchiveExtractionRequest, ArchiveExtractionSummary>;
public interface IScriptDecompilerService : IAsyncOperationService<ScriptDecompileRequest, ScriptDecompileSummary>;

public sealed class ArchiveExtractionService : IArchiveExtractionService
{
    private readonly IFileSystemService _fileSystem; private readonly IProcessRunnerService _runner;
    public ArchiveExtractionService(IFileSystemService fileSystem, IProcessRunnerService runner) { _fileSystem = fileSystem; _runner = runner; }
    public async Task<OperationResult<ArchiveExtractionSummary>> ExecuteAsync(ArchiveExtractionRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!request.Confirmed) return OperationResult<ArchiveExtractionSummary>.Failure(Security("ARCHIVE_CONFIRMATION_REQUIRED", "解包前必须确认文件计划。"));
        var output = _fileSystem.ValidateProjectPath(request.Project, request.OutputRelativeDirectory);
        if (!output.IsSuccess || output.Value is null) return new(OperationStatus.Failed, null, output.Diagnostics);
        var python = _fileSystem.ValidateExecutable(request.Python.FullPath);
        if (!python.IsSuccess || python.Value is null) return new(OperationStatus.Failed, null, python.Diagnostics);
        var diagnostics = new List<Diagnostic>(); var processed = 0; var preserved = new List<string>();
        foreach (var relative in request.ArchiveRelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = _fileSystem.ValidateProjectPath(request.Project, relative);
            if (!archive.IsSuccess || archive.Value is null) { diagnostics.AddRange(archive.Diagnostics); continue; }
            var wrapper = Path.Combine(Path.GetDirectoryName(request.RpaTool.FullPath)!, "safe_rpa_extract.py");
            if (!File.Exists(wrapper)) { diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "SAFE_EXTRACTOR_MISSING", "安全解包入口缺失。", Category: DiagnosticCategory.Security)); continue; }
            var result = await _runner.ExecuteAsync(new ProcessExecutionPlan(python.Value, request.Project.FullPath,
                [wrapper, request.RpaTool.FullPath, archive.Value.FullPath, output.Value.FullPath], new Dictionary<string, string?> { ["PYTHONUTF8"] = "1" },
                TimeSpan.FromHours(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(3)), progress, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics); if (result.IsSuccess) processed++; preserved.Add(relative);
        }
        var status = diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? OperationStatus.Failed : diagnostics.Count > 0 ? OperationStatus.SucceededWithWarnings : OperationStatus.Succeeded;
        return new(status, new ArchiveExtractionSummary(processed, request.OutputRelativeDirectory, preserved), diagnostics);
    }
    private static Diagnostic Security(string code, string message) => new(DiagnosticSeverity.Error, code, message, Category: DiagnosticCategory.Security);
}

public sealed class ScriptDecompilerService : IScriptDecompilerService
{
    private readonly IFileSystemService _fileSystem; private readonly IProcessRunnerService _runner;
    public ScriptDecompilerService(IFileSystemService fileSystem, IProcessRunnerService runner) { _fileSystem = fileSystem; _runner = runner; }
    public async Task<OperationResult<ScriptDecompileSummary>> ExecuteAsync(ScriptDecompileRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!request.Confirmed) return OperationResult<ScriptDecompileSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "DECOMPILE_CONFIRMATION_REQUIRED", "反编译前必须确认文件计划。", Category: DiagnosticCategory.Security));
        var python = _fileSystem.ValidateExecutable(request.Python.FullPath);
        if (!python.IsSuccess || python.Value is null) return new(OperationStatus.Failed, null, python.Diagnostics);
        var diagnostics = new List<Diagnostic>(); var processed = 0; var preserved = new List<string>();
        foreach (var relative in request.ScriptRelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = _fileSystem.ValidateProjectPath(request.Project, relative);
            if (!source.IsSuccess || source.Value is null) { diagnostics.AddRange(source.Diagnostics); continue; }
            var output = Path.ChangeExtension(source.Value.FullPath, ".rpy");
            if (File.Exists(output)) { diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "DECOMPILE_OUTPUT_EXISTS", "对应 .rpy 已存在，已跳过。", relative)); preserved.Add(relative); continue; }
            var result = await _runner.ExecuteAsync(new ProcessExecutionPlan(python.Value, request.Project.FullPath,
                [request.Unrpyc.FullPath, source.Value.FullPath], new Dictionary<string, string?> { ["PYTHONUTF8"] = "1" }, TimeSpan.FromHours(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(3)), progress, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics); if (result.IsSuccess) processed++; preserved.Add(relative);
        }
        var status = diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? OperationStatus.Failed : diagnostics.Count > 0 ? OperationStatus.SucceededWithWarnings : OperationStatus.Succeeded;
        return new(status, new ScriptDecompileSummary(processed, preserved), diagnostics);
    }
}
