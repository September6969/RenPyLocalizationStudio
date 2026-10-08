using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

internal static class Program
{
    private sealed record Measurement(int Count, string Operation, double TotalMs, double UiGapMs);
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var results = new List<Measurement>();
        foreach (var count in new[] { 10000, 50000 })
        {
            var snapshot = Fixture(count);
            var fs = new FileSystemService();
            var session = new ProjectSessionViewModel(new Analysis(snapshot), new ProjectCatalogService(fs), new ProjectWriter(fs),
                new Dialog(), new Confirmation(), new AppSettingsStore(Path.Combine(snapshot.ProjectRoot, "settings.json")),
                new Theme(), new TaskCenterViewModel())
            { ProjectPath = snapshot.ProjectRoot, Language = snapshot.Language };
            using var workspace = new TranslationWorkspaceViewModel(session, new Preview());
            workspace.PropertyChanged += (_, _) =>
            {
                if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("界面通知来自后台线程");
            };
            Wait(session.AnalyzeAsync());
            workspace.ViewMode = TranslationViewMode.Strings;
            WaitResults(workspace);
            // 首次用于预热；其余五次独立记录，时延门槛不作为 CI 断言。
            for (var run = -1; run < 5; run++)
            {
                Measure("search", () => workspace.SearchText = run % 2 == 0 ? "Line 12" : "Line 49");
                workspace.SearchText = "";
                WaitResults(workspace);
                Measure("scope", () => workspace.SelectedScope = run % 2 == 0 ? "file:game/chapter0.rpy" : "file:game/chapter1.rpy");
                workspace.ClearSearchCommand.Execute(null);
                WaitResults(workspace);
                workspace.ViewMode = TranslationViewMode.Quality;
                WaitResults(workspace);
                Measure("quality", () => workspace.RefreshResultsCommand.Execute(null));
                var bulkMethod = workspace.GetType().GetMethod("PreviewBulkAsync");
                if (bulkMethod is not null)
                    Measure("bulk", () => Wait((Task)bulkMethod.Invoke(workspace, [true, CancellationToken.None])!));
                workspace.ViewMode = TranslationViewMode.Strings;
                WaitResults(workspace);

                void Measure(string operation, Action action)
                {
                    var watch = Stopwatch.StartNew();
                    var previous = watch.Elapsed.TotalMilliseconds;
                    var maxGap = 0d;
                    var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
                    timer.Tick += (_, _) => { var now = watch.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - previous); previous = now; };
                    timer.Start();
                    action();
                    WaitResults(workspace);
                    maxGap = Math.Max(maxGap, watch.Elapsed.TotalMilliseconds - previous);
                    timer.Stop();
                    if (run >= 0) results.Add(new(count, operation, watch.Elapsed.TotalMilliseconds, maxGap));
                }
            }
            Wait(workspace.StopPreviewAsync(CancellationToken.None));
        }
        var output = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length > 0) File.WriteAllText(args[0], output, new System.Text.UTF8Encoding(false));
        Console.WriteLine(output);
    }

    private static void WaitResults(TranslationWorkspaceViewModel workspace)
    {
        var property = workspace.GetType().GetProperty("PendingResults");
        if (property is null) return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var task = (Task)property.GetValue(workspace)!;
            Wait(task);
            if (ReferenceEquals(task, property.GetValue(workspace))) return;
        }
        throw new InvalidOperationException("工作区未能收敛");
    }
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static ProjectSnapshot Fixture(int count)
    {
        var snapshot = new ProjectSnapshot { ProjectRoot = "E:/_codex_work/rls-performance-fixture", GameDirectory = "E:/_codex_work/rls-performance-fixture/game", Language = "schinese" };
        for (var file = 0; file < 10; file++)
        {
            var sourcePath = $"game/chapter{file}.rpy";
            var tlPath = $"chapter{file}.rpy";
            var source = new SourceDocument { File = TextFile(sourcePath), RelativePath = sourcePath };
            var doc = new TlDocument { File = TextFile(tlPath), RelativePath = tlPath, Language = snapshot.Language };
            snapshot.Sources.Add(source);
            snapshot.TlDocuments.Add(doc);
            for (var i = file; i < count / 2; i += 10)
            {
                var original = $"Line {i} [name]";
                var node = new FlowNode
                {
                    Id = $"node:{i}",
                    Kind = FlowNodeKind.Dialogue,
                    DisplayText = original,
                    OriginalText = original,
                    Region = new(sourcePath, i + 1, i + 1)
                };
                snapshot.Graph.Nodes.Add(node);
                var donor = new TranslationUnit
                {
                    Kind = TranslationUnitKind.Dialogue,
                    Language = snapshot.Language,
                    FilePath = doc.File.FullPath,
                    RelativeTlPath = tlPath,
                    BlockSpan = new(0, 0),
                    HeaderLine = i * 3 + 1,
                    OriginalStatement = $"e \"{original}\"",
                    TranslationValueSpan = new(0, 0),
                    SourcePath = sourcePath,
                    SourceLine = i + 1,
                    BoundNode = node,
                    TranslationText = $"译文 {i} [name]"
                };
                doc.Units.Add(donor);
                var shared = new SharedStringEntry { Language = snapshot.Language, OldText = original };
                var definition = new TranslationUnit
                {
                    Kind = TranslationUnitKind.String,
                    Language = snapshot.Language,
                    FilePath = doc.File.FullPath,
                    RelativeTlPath = tlPath,
                    BlockSpan = new(0, 0),
                    HeaderLine = i * 3 + 2,
                    OldText = original,
                    TranslationValueSpan = new(0, 0),
                    TranslationText = i % 3 == 0 ? "" : i % 3 == 1 ? $"译文 {i} [name]" : "缺少占位符"
                };
                shared.Definitions.Add(definition);
                shared.LoadTranslation(definition.TranslationText);
                shared.References.Add(new(null, null, sourcePath, i + 1, StringSourceKind.Screen));
                snapshot.SharedStrings.Add(shared);
                doc.Units.Add(definition);
            }
        }
        return snapshot;
    }
    private static Utf8TextFile TextFile(string path) => new()
    {
        FullPath = "E:/_codex_work/rls-performance-fixture/" + path,
        Text = "",
        HasBom = false,
        NewLine = "\n",
        HasFinalNewLine = true,
        Sha256 = ""
    };
    private sealed class Analysis(ProjectSnapshot snapshot) : IProjectAnalysisService
    {
        public Task<OperationResult<ProjectSnapshot>> ExecuteAsync(ProjectAnalysisRequest request, IProgress<ToolOperationProgress> progress, CancellationToken token)
            => Task.FromResult(OperationResult<ProjectSnapshot>.Success(snapshot));
    }
    private sealed class Preview : IRenPyImagePreviewService
    {
        public Task<RenPySceneContext> ResolveSceneContextAsync(RenPyImagePreviewRequest request, CancellationToken token) => Task.FromResult(new RenPySceneContext(null, null, []));
        public void InvalidateProject(string path) { }
    }
    private sealed class Dialog : IFileDialogService
    {
        public string? SelectProjectFolder(string? path) => null;
        public string? SelectSdkExecutable(string? path) => null;
    }
    private sealed class Confirmation : IConfirmationService
    {
        public bool Confirm(string title, string message, MessageBoxImage image = MessageBoxImage.Question) => true;
        public void ShowDiagnostics(string title, IEnumerable<Diagnostic> diagnostics) { }
        public void ShowMessage(string title, string message, MessageBoxImage image = MessageBoxImage.Information) { }
    }
    private sealed class Theme : IThemeService
    {
        public string AccentColor => "#D16BA5";
        public bool TryApplyAccent(string color, out string? error) { error = null; return true; }
    }
}
