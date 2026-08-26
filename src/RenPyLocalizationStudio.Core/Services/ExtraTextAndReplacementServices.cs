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
        var files = await _fileSystem.EnumerateFilesAsync(request.Project, "game", "*.rpy", cancellationToken).ConfigureAwait(false);
        if (!files.IsSuccess || files.Value is null) return new(files.Status, null, files.Diagnostics);
        var sourceFiles = files.Value.Where(x => !x.RelativePath.Replace('\\', '/').StartsWith("game/tl/", StringComparison.OrdinalIgnoreCase)).ToArray();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tl in files.Value.Where(x => x.RelativePath.Replace('\\', '/').StartsWith($"game/tl/{request.Language}/", StringComparison.OrdinalIgnoreCase)))
        {
            var read = await _fileSystem.ReadUtf8Async(tl, cancellationToken).ConfigureAwait(false);
            if (read.Value is null) continue;
            foreach (Match match in OldStringRegex().Matches(read.Value.Text)) existing.Add(TextUtilities.UnescapeRenPyString(match.Groups[1].Value));
        }

        var candidates = new Dictionary<string, ExtraTextCandidate>(StringComparer.Ordinal);
        for (var index = 0; index < sourceFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = sourceFiles[index];
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Scanning, "扫描额外文本", index + 1, sourceFiles.Length, path.RelativePath));
            var read = await _fileSystem.ReadUtf8Async(path, cancellationToken).ConfigureAwait(false);
            if (read.Value is null) continue;
            var lines = read.Value.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var inScreen = false;
            var screenIndent = 0;
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                var trimmed = line.TrimStart();
                var indent = line.Length - trimmed.Length;
                if (trimmed.StartsWith("screen ", StringComparison.Ordinal) && trimmed.EndsWith(':')) { inScreen = true; screenIndent = indent; }
                else if (inScreen && trimmed.Length > 0 && indent <= screenIndent) inScreen = false;
                if (trimmed.StartsWith('#') || trimmed.Length == 0) continue;

                AddMatches(CallStringRegex(), line, path.RelativePath, lineIndex + 1, match => ClassifyCall(match.Groups[1].Value, inScreen), existing, candidates);
                AddMatches(AssignmentStringRegex(), line, path.RelativePath, lineIndex + 1, match => ClassifyAssignment(match.Groups[1].Value), existing, candidates);
                if (inScreen) AddMatches(ScreenStringRegex(), line, path.RelativePath, lineIndex + 1, _ => (ExtraTextKind.ScreenText, CandidateConfidence.Medium, "screen 文本可能未被官方提取"), existing, candidates);
            }
        }
        return OperationResult<ExtraTextScanSummary>.Success(new ExtraTextScanSummary(candidates.Values.OrderBy(x => x.RelativePath).ThenBy(x => x.Line).ToArray()));
    }

    private static void AddMatches(Regex regex, string line, string path, int lineNumber,
        Func<Match, (ExtraTextKind Kind, CandidateConfidence Confidence, string Reason)> classifier,
        HashSet<string> existing, Dictionary<string, ExtraTextCandidate> output)
    {
        foreach (Match match in regex.Matches(line))
        {
            var textGroup = match.Groups[^1];
            var text = TextUtilities.UnescapeRenPyString(textGroup.Value);
            if (string.IsNullOrWhiteSpace(text) || LooksLikeInternalValue(text)) continue;
            var (kind, confidence, reason) = classifier(match);
            var id = $"{path}:{lineNumber}:{match.Index}:{text}";
            output[id] = new ExtraTextCandidate(id, text, path, lineNumber, kind, confidence, reason, existing.Contains(text));
        }
    }
    private static bool LooksLikeInternalValue(string value) => value.Length < 2 || value.Contains('/') && !value.Contains(' ') || value.StartsWith('#') || value.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
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

    [GeneratedRegex("\\bold\\s+\\\"((?:\\\\.|[^\"\\\\])*)\\\"")]
    private static partial Regex OldStringRegex();
    [GeneratedRegex("\\b(Character|renpy\\.input|renpy\\.notify|_|__|_p)\\s*\\(\\s*\\\"((?:\\\\.|[^\"\\\\])*)\\\"")]
    private static partial Regex CallStringRegex();
    [GeneratedRegex("^\\s*(define|default)\\s+[^=]+?=\\s*\\\"((?:\\\\.|[^\"\\\\])*)\\\"")]
    private static partial Regex AssignmentStringRegex();
    [GeneratedRegex("^\\s*(?:text|textbutton|label|tooltip)\\s+\\\"((?:\\\\.|[^\"\\\\])*)\\\"")]
    private static partial Regex ScreenStringRegex();
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
    public OperationResult<ReplacementValidationSummary> Validate(ReplacementValidationRequest request)
    {
        var diagnostics = new List<Diagnostic>();
        var rules = request.Rules.Where(x => x.Enabled).OrderBy(x => x.Order).ToArray();
        foreach (var rule in rules.Where(x => string.IsNullOrEmpty(x.Source))) diagnostics.Add(PatchDiagnostic("REPLACE_EMPTY_SOURCE", "替换源文本不能为空。"));
        foreach (var group in rules.GroupBy(x => x.Source, StringComparer.Ordinal).Where(x => x.Count() > 1)) diagnostics.Add(PatchDiagnostic("REPLACE_DUPLICATE", $"源文本存在多条规则：{group.Key}"));
        foreach (var rule in rules)
        {
            if (rules.Any(next => next.Id != rule.Id && rule.Replacement.Contains(next.Source, StringComparison.Ordinal) && next.Replacement.Contains(rule.Source, StringComparison.Ordinal)))
                diagnostics.Add(PatchDiagnostic("REPLACE_CYCLE", $"替换规则可能形成循环：{rule.Source}", DiagnosticSeverity.Error));
        }
        var counts = rules.ToDictionary(x => x.Id, x => request.IndexedTexts.Count(text => text.Contains(x.Source, StringComparison.Ordinal)));
        var summary = new ReplacementValidationSummary(rules, diagnostics, counts);
        return new(diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? OperationStatus.Failed : diagnostics.Count > 0 ? OperationStatus.SucceededWithWarnings : OperationStatus.Succeeded, summary, diagnostics);
    }

    public string GenerateRenPyCode(string language, IReadOnlyList<ReplacementRule> rules)
    {
        var enabled = rules.Where(x => x.Enabled).OrderBy(x => x.Order).ToArray();
        var lines = new List<string> { "init python:", "    def rls_replace_text(s):", $"        if _preferences.language != {Quote(language)}:", "            return s" };
        lines.AddRange(enabled.Select(x => $"        s = s.replace({Quote(x.Source)}, {Quote(x.Replacement)})"));
        lines.Add("        return s"); lines.Add("    config.replace_text = rls_replace_text");
        return string.Join("\n", lines);
    }
    private static string Quote(string value) => "\"" + TextUtilities.EscapeRenPyString(value) + "\"";
    private static Diagnostic PatchDiagnostic(string code, string message, DiagnosticSeverity severity = DiagnosticSeverity.Warning) => new(severity, code, message, Category: DiagnosticCategory.Patch);
}
