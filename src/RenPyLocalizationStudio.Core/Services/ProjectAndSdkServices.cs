namespace RenPyLocalizationStudio.Core.Services;

public sealed record ProjectAnalysisRequest(string ProjectRoot, string Language);
public interface IProjectAnalysisService : IAsyncOperationService<ProjectAnalysisRequest, ProjectSnapshot>;

public sealed class ProjectAnalysisService : IProjectAnalysisService
{
    private readonly ProjectAnalyzer _analyzer;
    public ProjectAnalysisService(ProjectAnalyzer? analyzer = null) => _analyzer = analyzer ?? new ProjectAnalyzer();

    public async Task<OperationResult<ProjectSnapshot>> ExecuteAsync(ProjectAnalysisRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Scanning, "正在扫描 Ren’Py 项目"));
            var snapshot = await _analyzer.AnalyzeAsync(request.ProjectRoot, request.Language, cancellationToken).ConfigureAwait(false);
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Completed, "项目分析完成", snapshot.Graph.Nodes.Count, snapshot.Graph.Nodes.Count));
            return OperationResult<ProjectSnapshot>.Success(snapshot, snapshot.Diagnostics.Where(x => x.Severity != DiagnosticSeverity.Error).ToArray());
        }
        catch (OperationCanceledException) { return OperationResult<ProjectSnapshot>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "ANALYSIS_CANCELLED", "项目分析已取消。")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException)
        { return OperationResult<ProjectSnapshot>.Failure(new Diagnostic(DiagnosticSeverity.Error, "ANALYSIS_FAILED", ex.Message, Category: DiagnosticCategory.Parsing)); }
    }
}

public sealed record SdkDiscoveryRequest(string? PreferredPath = null);
public sealed record SdkInstallation(string RootPath, string ExecutablePath, string DisplayVersion);
public sealed record SdkTranslationRequest(
    string ProjectRoot,
    string Language,
    SdkInstallation Sdk,
    bool CountOnly,
    bool Empty,
    bool StringsOnly,
    bool NoTodo,
    TimeSpan? Timeout = null);
public sealed record SdkTranslationSummary(string CommandPreview, int ExitCode, string Output, TimeSpan Duration);

public interface IRenPySdkService
{
    Task<OperationResult<IReadOnlyList<SdkInstallation>>> DiscoverAsync(SdkDiscoveryRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken);
    Task<OperationResult<SdkTranslationSummary>> ExecuteAsync(SdkTranslationRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken);
}

public sealed class RenPySdkService : IRenPySdkService
{
    private readonly IFileSystemService _fileSystem;
    private readonly IProcessRunnerService _processRunner;
    public RenPySdkService(IFileSystemService fileSystem, IProcessRunnerService processRunner) { _fileSystem = fileSystem; _processRunner = processRunner; }

    public Task<OperationResult<IReadOnlyList<SdkInstallation>>> DiscoverAsync(SdkDiscoveryRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(request.PreferredPath)) roots.Add(request.PreferredPath);
            var eRoot = new DriveInfo("E").IsReady ? "E:\\" : null;
            if (eRoot is not null)
            {
                foreach (var directory in Directory.EnumerateDirectories(eRoot, "renpy-*-sdk", SearchOption.TopDirectoryOnly)) roots.Add(directory);
            }
            var list = new List<SdkInstallation>();
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exe = Path.Combine(root, "renpy.exe");
                if (!_fileSystem.ValidateExecutable(exe).IsSuccess) continue;
                var version = Path.GetFileName(root).Replace("renpy-", "", StringComparison.OrdinalIgnoreCase).Replace("-sdk", "", StringComparison.OrdinalIgnoreCase);
                list.Add(new SdkInstallation(Path.GetFullPath(root), Path.GetFullPath(exe), version));
                progress.Report(ToolOperationProgress.Create(ToolOperationStage.Scanning, $"发现 Ren’Py SDK {version}", list.Count, null, root));
            }
            return OperationResult<IReadOnlyList<SdkInstallation>>.Success(list.OrderByDescending(x => x.DisplayVersion, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (OperationCanceledException) { return OperationResult<IReadOnlyList<SdkInstallation>>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "SDK_DISCOVERY_CANCELLED", "SDK 检测已取消。")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return OperationResult<IReadOnlyList<SdkInstallation>>.Failure(new Diagnostic(DiagnosticSeverity.Error, "SDK_DISCOVERY_FAILED", ex.Message, Category: DiagnosticCategory.Sdk)); }
    }, CancellationToken.None);

    public async Task<OperationResult<SdkTranslationSummary>> ExecuteAsync(SdkTranslationRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Language, "^[a-z][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return OperationResult<SdkTranslationSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "LANGUAGE_INVALID", "语言名必须以小写字母开头，且只能包含小写 ASCII、数字和下划线。", Category: DiagnosticCategory.Sdk));
        var executable = _fileSystem.ValidateExecutable(request.Sdk.ExecutablePath);
        if (!executable.IsSuccess || executable.Value is null) return new(OperationStatus.Failed, null, executable.Diagnostics);
        var root = _fileSystem.ValidateProjectRoot(request.ProjectRoot);
        if (!root.IsSuccess || root.Value is null) return new(OperationStatus.Failed, null, root.Diagnostics);

        var args = new List<string> { request.ProjectRoot, "translate", request.Language };
        if (request.CountOnly) args.Add("--count");
        if (request.Empty) args.Add("--empty");
        if (request.StringsOnly) args.Add("--strings-only");
        if (request.NoTodo) args.Add("--no-todo");
        var command = $"\"{request.Sdk.ExecutablePath}\" {string.Join(' ', args.Select(Quote))}";
        var process = await _processRunner.ExecuteAsync(new ProcessExecutionPlan(executable.Value, request.Sdk.RootPath, args,
            new Dictionary<string, string?> { ["PYTHONUTF8"] = "1" }, request.Timeout ?? TimeSpan.FromHours(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(3)), progress, cancellationToken).ConfigureAwait(false);
        if (process.Value is null) return new(process.Status, null, process.Diagnostics);
        var summary = new SdkTranslationSummary(command, process.Value.ExitCode, process.Value.StandardOutput + Environment.NewLine + process.Value.StandardError, process.Value.Duration);
        return new(process.Status, summary, process.Diagnostics);
    }
    private static string Quote(string value) => value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}
