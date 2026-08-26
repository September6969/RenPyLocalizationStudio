namespace RenPyLocalizationStudio.Core.Services;

public sealed record ProjectAnalysisRequest(string ProjectRoot, string Language);
public interface IProjectAnalysisService : IAsyncOperationService<ProjectAnalysisRequest, ProjectSnapshot>;
public sealed record ProjectLanguageDiscoveryRequest(string ProjectRoot);
public interface IProjectCatalogService : IAsyncOperationService<ProjectLanguageDiscoveryRequest, IReadOnlyList<string>>;

public sealed class ProjectAnalysisService : IProjectAnalysisService
{
    private readonly IFileSystemService _fileSystem;
    private readonly ProjectAnalyzer _analyzer;

    public ProjectAnalysisService(IFileSystemService fileSystem, ProjectAnalyzer? analyzer = null)
    {
        _fileSystem = fileSystem;
        _analyzer = analyzer ?? new ProjectAnalyzer();
    }

    public async Task<OperationResult<ProjectSnapshot>> ExecuteAsync(ProjectAnalysisRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Scanning, "正在扫描 Ren’Py 项目"));
            var rootResult = _fileSystem.ValidateProjectRoot(request.ProjectRoot);
            if (!rootResult.IsSuccess || rootResult.Value is null)
            {
                return new OperationResult<ProjectSnapshot>(OperationStatus.Failed, null, rootResult.Diagnostics);
            }

            var root = rootResult.Value;
            var gameRelative = string.Equals(Path.GetFileName(root.FullPath), "game", StringComparison.OrdinalIgnoreCase) ? "." : "game";
            var allFiles = await _fileSystem.EnumerateFilesAsync(root, gameRelative, "*.rpy", cancellationToken).ConfigureAwait(false);
            if (!allFiles.IsSuccess || allFiles.Value is null)
            {
                return new OperationResult<ProjectSnapshot>(allFiles.Status, null, allFiles.Diagnostics);
            }

            var languagePrefix = TextUtilities.NormalizePath(Path.Combine(gameRelative, "tl", request.Language)).TrimEnd('/') + "/";
            var tlPrefix = TextUtilities.NormalizePath(Path.Combine(gameRelative, "tl")).TrimEnd('/') + "/";
            var sourcePaths = allFiles.Value
                .Where(path => !TextUtilities.NormalizePath(path.RelativePath).StartsWith(tlPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var translationPaths = allFiles.Value
                .Where(path => TextUtilities.NormalizePath(path.RelativePath).StartsWith(languagePrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (translationPaths.Length == 0)
            {
                return OperationResult<ProjectSnapshot>.Failure(new Diagnostic(DiagnosticSeverity.Error, "TRANSLATION_DIRECTORY_MISSING",
                    $"找不到翻译目录：game/tl/{request.Language}", Category: DiagnosticCategory.FileSystem));
            }

            var diagnostics = new List<Diagnostic>();
            var sources = await ReadFilesAsync(root, sourcePaths, null, progress, diagnostics, cancellationToken).ConfigureAwait(false);
            var translations = await ReadFilesAsync(root, translationPaths, languagePrefix, progress, diagnostics, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var gameDirectory = Path.GetFullPath(Path.Combine(root.FullPath, gameRelative));
            var snapshot = _analyzer.Analyze(root.FullPath, gameDirectory, request.Language, sources, translations);
            foreach (var diagnostic in diagnostics)
            {
                snapshot.Diagnostics.Add(diagnostic);
            }
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Completed, "项目分析完成", snapshot.Graph.Nodes.Count, snapshot.Graph.Nodes.Count));
            return OperationResult<ProjectSnapshot>.Success(snapshot, snapshot.Diagnostics.Where(x => x.Severity != DiagnosticSeverity.Error).ToArray());
        }
        catch (OperationCanceledException) { return OperationResult<ProjectSnapshot>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "ANALYSIS_CANCELLED", "项目分析已取消。")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException)
        { return OperationResult<ProjectSnapshot>.Failure(new Diagnostic(DiagnosticSeverity.Error, "ANALYSIS_FAILED", ex.Message, Category: DiagnosticCategory.Parsing)); }
    }

    private async Task<List<(Utf8TextFile File, string RelativePath)>> ReadFilesAsync(
        ProjectRoot root,
        IReadOnlyList<ValidatedProjectPath> paths,
        string? stripPrefix,
        IProgress<ToolOperationProgress> progress,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var files = new List<(Utf8TextFile, string)>();
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[index];
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Reading, "正在读取项目文件", index + 1, paths.Count, path.RelativePath));
            var read = await _fileSystem.ReadUtf8Async(path, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess || read.Value is null)
            {
                diagnostics.AddRange(read.Diagnostics);
                continue;
            }

            var relative = TextUtilities.NormalizePath(path.RelativePath);
            if (stripPrefix is not null && relative.StartsWith(stripPrefix, StringComparison.OrdinalIgnoreCase))
            {
                relative = relative[stripPrefix.Length..];
            }
            files.Add((read.Value, relative));
        }
        return files;
    }
}

public sealed class ProjectCatalogService : IProjectCatalogService
{
    private readonly IFileSystemService _fileSystem;

    public ProjectCatalogService(IFileSystemService fileSystem) => _fileSystem = fileSystem;

    public async Task<OperationResult<IReadOnlyList<string>>> ExecuteAsync(
        ProjectLanguageDiscoveryRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var root = _fileSystem.ValidateProjectRoot(request.ProjectRoot);
        if (!root.IsSuccess || root.Value is null)
        {
            return new OperationResult<IReadOnlyList<string>>(OperationStatus.Failed, null, root.Diagnostics);
        }

        progress.Report(ToolOperationProgress.Create(ToolOperationStage.Scanning, "正在发现翻译语言"));
        var gameRelative = string.Equals(Path.GetFileName(root.Value.FullPath), "game", StringComparison.OrdinalIgnoreCase) ? "." : "game";
        return await _fileSystem.EnumerateDirectoriesAsync(root.Value, Path.Combine(gameRelative, "tl"), cancellationToken).ConfigureAwait(false);
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
