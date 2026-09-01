using System.Text.RegularExpressions;

namespace RenPyLocalizationStudio.Core.Services;

public enum ExtraTextKind { Character, ScreenText, Tooltip, Input, Notification, TranslationFunction, Define, Default, Python, Dynamic }
public enum CandidateConfidence { High, Medium, Low }
public sealed record ExtraTextCandidate(string Id, string Text, string RelativePath, int Line, ExtraTextKind Kind, CandidateConfidence Confidence, string Reason, bool AlreadyTranslated);
public sealed record ExtraTextScanRequest(ProjectRoot Project, string Language);
public sealed record ExtraTextScanSummary(IReadOnlyList<ExtraTextCandidate> Candidates);
public interface IExtraTextScanService : IAsyncOperationService<ExtraTextScanRequest, ExtraTextScanSummary>;

public sealed partial class ExtraTextScanService : IExtraTextScanService
{
    private readonly IFileSystemService _fileSystem;
    public ExtraTextScanService(IFileSystemService fileSystem) => _fileSystem = fileSystem;

    public async Task<OperationResult<ExtraTextScanSummary>> ExecuteAsync(ExtraTextScanRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var gameDirectory = ProjectLayout.GameDirectory(request.Project);
        var tlDirectory = ProjectLayout.TlDirectory(request.Project, request.Language).Replace('\\', '/').TrimStart('.', '/');
        var files = await _fileSystem.EnumerateFilesAsync(request.Project, gameDirectory, "*.rpy", cancellationToken).ConfigureAwait(false);
        if (!files.IsSuccess || files.Value is null) return new(files.Status, null, files.Diagnostics);
        var sourceFiles = files.Value.Where(x => !IsTlPath(request.Project, x.RelativePath)).ToArray();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        var tlParser = new TlParser();
        foreach (var tl in files.Value.Where(x => x.RelativePath.Replace('\\', '/').TrimStart('.', '/').StartsWith(tlDirectory + "/", StringComparison.OrdinalIgnoreCase)))
        {
            var read = await _fileSystem.ReadUtf8Async(tl, cancellationToken).ConfigureAwait(false);
            if (read.Value is null)
            {
                diagnostics.AddRange(read.Diagnostics);
                continue;
            }
            var parsed = tlParser.Parse(read.Value, tl.RelativePath, request.Language);
            foreach (var unit in parsed.Units.Where(unit => unit.Kind == TranslationUnitKind.String &&
                                                            !unit.MissingNew &&
                                                            !string.IsNullOrWhiteSpace(unit.TranslationText) &&
                                                            unit.OldText is not null))
                existing.Add(unit.OldText!);
        }

        var candidates = new Dictionary<string, ExtraTextCandidate>(StringComparer.Ordinal);
        for (var index = 0; index < sourceFiles.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return OperationResult<ExtraTextScanSummary>.Cancelled(
                    new Diagnostic(DiagnosticSeverity.Info, "EXTRA_TEXT_SCAN_CANCELLED", "额外文本扫描已取消。"));
            var path = sourceFiles[index];
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Scanning, "扫描额外文本", index + 1, sourceFiles.Length, path.RelativePath));
            var read = await _fileSystem.ReadUtf8Async(path, cancellationToken).ConfigureAwait(false);
            if (read.Value is null)
            {
                diagnostics.AddRange(read.Diagnostics);
                continue;
            }
            var lines = read.Value.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            AddFileMatches(CallStringRegex(), read.Value.Text, path.RelativePath,
                match => ClassifyCall(match.Groups[1].Value, false), existing, candidates);
            AddFileMatches(AssignmentStringRegex(), read.Value.Text, path.RelativePath,
                match => ClassifyAssignment(match.Groups[1].Value), existing, candidates);
            var inScreen = false;
            var screenIndent = 0;
            var inPython = false;
            var pythonIndent = 0;
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                var trimmed = line.TrimStart();
                var indent = line.Length - trimmed.Length;
                if (trimmed.StartsWith("screen ", StringComparison.Ordinal) && trimmed.EndsWith(':')) { inScreen = true; screenIndent = indent; }
                else if (inScreen && trimmed.Length > 0 && indent <= screenIndent) inScreen = false;
                if (IsPythonBlockHeader(trimmed)) { inPython = true; pythonIndent = indent; }
                else if (inPython && trimmed.Length > 0 && indent <= pythonIndent) inPython = false;
                if (trimmed.StartsWith('#') || trimmed.Length == 0) continue;

                if (inScreen) AddMatches(ScreenStringRegex(), line, path.RelativePath, lineIndex + 1, _ => (ExtraTextKind.ScreenText, CandidateConfidence.Medium, "screen 文本可能未被官方提取"), existing, candidates);
                if ((inPython || trimmed.StartsWith('$')) &&
                    !CallStringRegex().IsMatch(line) && !AssignmentStringRegex().IsMatch(line))
                {
                    AddMatches(PythonStringRegex(), line, path.RelativePath, lineIndex + 1,
                        match => ClassifyPythonString(line, match), existing, candidates);
                }
            }
        }
        return OperationResult<ExtraTextScanSummary>.Success(
            new ExtraTextScanSummary(candidates.Values.OrderBy(x => x.RelativePath).ThenBy(x => x.Line).ToArray()),
            diagnostics);
    }

    private static bool IsTlPath(ProjectRoot root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('.', '/');
        var prefix = ProjectLayout.RootIsGameDirectory(root) ? "tl/" : "game/tl/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddFileMatches(Regex regex, string text, string path,
        Func<Match, (ExtraTextKind Kind, CandidateConfidence Confidence, string Reason)> classifier,
        HashSet<string> existing, Dictionary<string, ExtraTextCandidate> output)
    {
        foreach (Match match in regex.Matches(text))
        {
            var lineNumber = 1 + text.AsSpan(0, match.Index).Count('\n');
            AddMatch(match, path, lineNumber, classifier, existing, output);
        }
    }

    private static void AddMatches(Regex regex, string line, string path, int lineNumber,
        Func<Match, (ExtraTextKind Kind, CandidateConfidence Confidence, string Reason)> classifier,
        HashSet<string> existing, Dictionary<string, ExtraTextCandidate> output)
    {
        foreach (Match match in regex.Matches(line))
        {
            AddMatch(match, path, lineNumber, classifier, existing, output);
        }
    }

    private static void AddMatch(Match match, string path, int lineNumber,
        Func<Match, (ExtraTextKind Kind, CandidateConfidence Confidence, string Reason)> classifier,
        HashSet<string> existing, Dictionary<string, ExtraTextCandidate> output)
    {
        var textGroup = match.Groups["double"].Success ? match.Groups["double"]
            : match.Groups["single"].Success ? match.Groups["single"] : match.Groups[^1];
        var text = TextUtilities.UnescapeRenPyString(textGroup.Value);
        if (string.IsNullOrWhiteSpace(text) || LooksLikeInternalValue(text)) return;
        var (kind, confidence, reason) = classifier(match);
        var id = $"{path}:{lineNumber}:{match.Index}:{text}";
        output[id] = new ExtraTextCandidate(id, text, path, lineNumber, kind, confidence, reason, existing.Contains(text));
    }
    private static bool LooksLikeInternalValue(string value) => value.Length < 2 || value.Contains('/') && !value.Contains(' ') || value.StartsWith('#') || value.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    private static bool IsPythonBlockHeader(string trimmed) =>
        (trimmed.StartsWith("python", StringComparison.Ordinal) ||
         trimmed.StartsWith("init python", StringComparison.Ordinal) ||
         Regex.IsMatch(trimmed, "^init\\s+-?\\d+\\s+python\\b", RegexOptions.CultureInvariant)) &&
        trimmed.EndsWith(':');

    private static (ExtraTextKind, CandidateConfidence, string) ClassifyPythonString(string line, Match match)
    {
        var prefixStart = Math.Max(0, match.Index - 2);
        var prefix = line[prefixStart..match.Index];
        var dynamic = prefix.EndsWith('f') || line.Contains('+') || line.Contains(".format(", StringComparison.Ordinal) ||
                      line.Contains('%');
        return dynamic
            ? (ExtraTextKind.Dynamic, CandidateConfidence.Low, "动态 Python 字符串，需要人工确认")
            : (ExtraTextKind.Python, CandidateConfidence.Low, "Python 静态字符串，需要人工确认");
    }
    private static (ExtraTextKind, CandidateConfidence, string) ClassifyCall(string call, bool inScreen) => call switch
    {
        "Character" => (ExtraTextKind.Character, CandidateConfidence.High, "角色显示名"),
        "renpy.input" => (ExtraTextKind.Input, CandidateConfidence.High, "输入提示"),
        "renpy.notify" => (ExtraTextKind.Notification, CandidateConfidence.High, "通知文本"),
        "_" or "__" or "_p" => (ExtraTextKind.TranslationFunction, CandidateConfidence.High, "Ren’Py 翻译函数"),
        _ when inScreen => (ExtraTextKind.ScreenText, CandidateConfidence.Medium, "screen 调用文本"),
        _ => (ExtraTextKind.Python, CandidateConfidence.Low, "Python 调用字符串，需要人工确认")
    };
    private static (ExtraTextKind, CandidateConfidence, string) ClassifyAssignment(string keyword) => keyword == "define"
        ? (ExtraTextKind.Define, CandidateConfidence.Medium, "define 字符串") : (ExtraTextKind.Default, CandidateConfidence.Medium, "default 字符串");

    [GeneratedRegex("\\b(Character|renpy\\.input|renpy\\.notify|_|__|_p)\\s*\\(\\s*(?:\\\"(?<double>(?:\\\\.|[^\"\\\\])*)\\\"|'(?<single>(?:\\\\.|[^'\\\\])*)')", RegexOptions.Singleline)]
    private static partial Regex CallStringRegex();
    [GeneratedRegex("^\\s*(define|default)\\s+[^=]+?=\\s*(?:\\\"(?<double>(?:\\\\.|[^\"\\\\])*)\\\"|'(?<single>(?:\\\\.|[^'\\\\])*)')", RegexOptions.Multiline)]
    private static partial Regex AssignmentStringRegex();
    [GeneratedRegex("^\\s*(?:text|textbutton|label|tooltip)\\s+(?:\\\"(?<double>(?:\\\\.|[^\"\\\\])*)\\\"|'(?<single>(?:\\\\.|[^'\\\\])*)')")]
    private static partial Regex ScreenStringRegex();
    [GeneratedRegex("(?<![A-Za-z0-9_])(?:[rRuUbBfF]{0,2})?(?:\\\"(?<double>(?:\\\\.|[^\"\\\\])*)\\\"|'(?<single>(?:\\\\.|[^'\\\\])*)')")]
    private static partial Regex PythonStringRegex();
}

public sealed record ReplacementRule(Guid Id, string Source, string Replacement, bool Enabled, int Order, string? Description = null);
public sealed record ReplacementValidationSummary(IReadOnlyList<ReplacementRule> OrderedRules, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyDictionary<Guid, int> MatchCounts);
public sealed record ReplacementValidationRequest(IReadOnlyList<ReplacementRule> Rules, IReadOnlyList<string> IndexedTexts);
public interface IReplacementRuleService
{
    OperationResult<ReplacementValidationSummary> Validate(ReplacementValidationRequest request);
    string GenerateRenPyCode(string language, IReadOnlyList<ReplacementRule> rules);
}

public sealed class ReplacementRuleService : IReplacementRuleService
{
    private readonly TranslationValidator _translationValidator = new();

    public OperationResult<ReplacementValidationSummary> Validate(ReplacementValidationRequest request)
    {
        var diagnostics = new List<Diagnostic>();
        var rules = request.Rules.Where(x => x.Enabled).OrderBy(x => x.Order).ToArray();
        foreach (var rule in rules.Where(x => string.IsNullOrEmpty(x.Source))) diagnostics.Add(PatchDiagnostic("REPLACE_EMPTY_SOURCE", "替换源文本不能为空。"));
        foreach (var group in rules.GroupBy(x => x.Source, StringComparer.Ordinal).Where(x => x.Count() > 1)) diagnostics.Add(PatchDiagnostic("REPLACE_DUPLICATE", $"源文本存在多条规则：{group.Key}"));
        var edges = rules.ToDictionary(rule => rule.Id, _ => new List<Guid>());
        foreach (var rule in rules)
        {
            diagnostics.AddRange(_translationValidator.Validate(rule.Source, rule.Replacement)
                .Select(diagnostic => diagnostic with
                {
                    Code = "REPLACE_" + diagnostic.Code,
                    Category = DiagnosticCategory.Patch,
                    Message = $"规则“{rule.Source}”会改变占位符或文本标签：{diagnostic.Message}"
                }));
            foreach (var next in rules.Where(next => next.Id != rule.Id && !string.IsNullOrEmpty(next.Source) &&
                                                     rule.Replacement.Contains(next.Source, StringComparison.Ordinal)))
            {
                edges[rule.Id].Add(next.Id);
                diagnostics.Add(PatchDiagnostic("REPLACE_CHAIN",
                    $"规则“{rule.Source}”的结果会继续命中“{next.Source}”，执行顺序会改变最终文本。"));
            }
        }
        var rulesById = rules.ToDictionary(rule => rule.Id);
        foreach (var cycle in FindCycles(edges))
            diagnostics.Add(PatchDiagnostic("REPLACE_CYCLE",
                $"替换规则形成循环：{string.Join(" → ", cycle.Select(id => rulesById[id].Source))}", DiagnosticSeverity.Error));
        var counts = rules.ToDictionary(x => x.Id, x => request.IndexedTexts.Count(text => text.Contains(x.Source, StringComparison.Ordinal)));
        var summary = new ReplacementValidationSummary(rules, diagnostics, counts);
        return new(diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? OperationStatus.Failed : diagnostics.Count > 0 ? OperationStatus.SucceededWithWarnings : OperationStatus.Succeeded, summary, diagnostics);
    }

    public string GenerateRenPyCode(string language, IReadOnlyList<ReplacementRule> rules)
    {
        var enabled = rules.Where(x => x.Enabled).OrderBy(x => x.Order).ToArray();
        var lines = new List<string>
        {
            "init python:",
            "    if getattr(config.replace_text, '__name__', '') != 'rls_replace_text':",
            "        _rls_previous_replace_text = config.replace_text",
            "    def rls_replace_text(s):",
            "        previous = getattr(store, '_rls_previous_replace_text', None)",
            "        if previous is not None:",
            "            s = previous(s)",
            $"        if _preferences.language != {Quote(language)}:",
            "            return s"
        };
        lines.AddRange(enabled.Select(x => $"        s = s.replace({Quote(x.Source)}, {Quote(x.Replacement)})"));
        lines.Add("        return s"); lines.Add("    config.replace_text = rls_replace_text");
        return string.Join("\n", lines);
    }
    private static string Quote(string value) => "\"" + TextUtilities.EscapeRenPyString(value) + "\"";
    private static Diagnostic PatchDiagnostic(string code, string message, DiagnosticSeverity severity = DiagnosticSeverity.Warning) => new(severity, code, message, Category: DiagnosticCategory.Patch);

    private static IReadOnlyList<IReadOnlyList<Guid>> FindCycles(IReadOnlyDictionary<Guid, List<Guid>> edges)
    {
        var state = new Dictionary<Guid, int>();
        var stack = new List<Guid>();
        var cycles = new List<IReadOnlyList<Guid>>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);

        void Visit(Guid node)
        {
            state[node] = 1;
            stack.Add(node);
            foreach (var target in edges[node])
            {
                if (!state.TryGetValue(target, out var targetState)) Visit(target);
                else if (targetState == 1)
                {
                    var start = stack.IndexOf(target);
                    var cycle = stack.Skip(start).Append(target).ToArray();
                    var signature = string.Join('|', cycle.Take(cycle.Length - 1).Order());
                    if (signatures.Add(signature)) cycles.Add(cycle);
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (var node in edges.Keys)
            if (!state.ContainsKey(node)) Visit(node);
        return cycles;
    }
}
