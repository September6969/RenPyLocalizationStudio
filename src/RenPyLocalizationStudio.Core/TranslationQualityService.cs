namespace RenPyLocalizationStudio.Core;

public sealed record TranslationQualityIssue(
    TranslationUnit? Unit, SharedStringEntry? SharedString, string Original, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>检查当前内存译文；每个对白单元或共享字符串只生成一条可编辑的问题记录。</summary>
public sealed class TranslationQualityService
{
    private readonly TranslationValidator _validator = new();

    public IReadOnlyList<TranslationQualityIssue> Check(ProjectSnapshot snapshot)
    {
        return Check(TranslationReadSnapshot.Capture(snapshot));
    }

    public IReadOnlyList<TranslationQualityIssue> Check(IReadOnlyList<TranslationReadEntry> captured,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = captured.Select(row => new Entry(row)).ToArray();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Row.Raw)
            {
                entry.Add("RAW_BLOCK_REVIEW", "复杂原始块：请人工核对译文与脚本结构。", DiagnosticSeverity.Info);
                continue;
            }
            if (entry.Row.Conflict)
                entry.Add("SHARED_TRANSLATION_CONFLICT", "相同 old 存在冲突译文，请核对后统一。", DiagnosticSeverity.Error);

            var translations = entry.Row.Conflict
                ? entry.Row.Definitions.Distinct(StringComparer.Ordinal).ToArray()
                : [entry.Translation];
            if (translations.Any(string.IsNullOrWhiteSpace))
                entry.Add("EMPTY_TRANSLATION", "译文为空，请补充翻译。", DiagnosticSeverity.Warning);
            if (entry.Original is not null)
            {
                // 空译文仅报待译，避免同时产生一长串缺失占位符的重复提示。
                foreach (var translation in translations.Where(text => !string.IsNullOrWhiteSpace(text)))
                    entry.Diagnostics.AddRange(_validator.Validate(entry.Original, translation, entry.Path, entry.Line));
            }
        }

        foreach (var group in entries.Where(entry => entry.Original is not null && !entry.Row.Raw &&
                     !entry.Row.Conflict && !string.IsNullOrWhiteSpace(entry.Translation))
                     .GroupBy(entry => entry.Original!, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Select(entry => entry.Translation).Distinct(StringComparer.Ordinal).Take(2).Count() < 2) continue;
            foreach (var entry in group)
                entry.Add("INCONSISTENT_TRANSLATION", "同原文存在不同译法，请结合语境核对（不一定是错误）。", DiagnosticSeverity.Info);
        }
        return entries.Where(entry => entry.Diagnostics.Count > 0)
            .OrderByDescending(entry => entry.Diagnostics.Max(diagnostic => diagnostic.Severity))
            .ThenBy(entry => entry.Path, StringComparer.Ordinal).ThenBy(entry => entry.Line)
            .Select(entry => new TranslationQualityIssue(entry.Unit, entry.Shared, entry.Original ?? "复杂翻译块",
                entry.Diagnostics.DistinctBy(diagnostic => (diagnostic.Code, diagnostic.Message)).ToArray()))
            .ToArray();
    }

    private sealed class Entry(TranslationReadEntry row)
    {
        public TranslationReadEntry Row { get; } = row;
        public TranslationUnit? Unit => Row.Unit;
        public SharedStringEntry? Shared => Row.Shared;
        public string? Original => Row.Original;
        public string Translation => Row.Translation;
        public string? Path => Row.Path;
        public int? Line => Row.Line;
        public List<Diagnostic> Diagnostics { get; } = [];
        public void Add(string code, string message, DiagnosticSeverity severity)
            => Diagnostics.Add(new Diagnostic(severity, code, message, Path, Line, DiagnosticCategory.Translation));
    }
}
