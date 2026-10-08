namespace RenPyLocalizationStudio.Core;

/// <summary>在界面线程捕获译文值；后台计算仅使用这些不可变字段，不读取编辑对象。</summary>
public sealed record TranslationReadEntry(int Id, string? Original, string Translation, bool Conflict,
    bool Raw, bool Writable, string? Path, int? Line, string Impact,
    IReadOnlyList<string> Definitions, TranslationUnit? Unit, SharedStringEntry? Shared);

public static class TranslationReadSnapshot
{
    public static IReadOnlyList<TranslationReadEntry> Capture(ProjectSnapshot snapshot)
    {
        var entries = new List<TranslationReadEntry>();
        foreach (var unit in snapshot.TranslationUnits.Where(u => u.Kind == TranslationUnitKind.Dialogue && u.Language == snapshot.Language))
            entries.Add(new(entries.Count, TranslationMemoryIndex.GetOriginal(unit), unit.TranslationText, false,
                unit.IsRawMode, !unit.IsRawMode && unit.TranslationValueSpan is not null, unit.RelativeTlPath, unit.HeaderLine,
                $"tl/{snapshot.Language}/{unit.RelativeTlPath}:{unit.HeaderLine}", [], unit, null));
        foreach (var shared in snapshot.SharedStrings.Where(s => s.Language == snapshot.Language))
            entries.Add(new(entries.Count, shared.OldText, shared.Translation, shared.HasConflict, false, shared.Definitions.Count > 0,
                shared.Definitions.FirstOrDefault()?.RelativeTlPath, shared.Definitions.FirstOrDefault()?.HeaderLine,
                string.Join(" · ", shared.Definitions.Select(u => $"tl/{snapshot.Language}/{u.RelativeTlPath}:{u.HeaderLine}")),
                Array.AsReadOnly(shared.Definitions.Select(u => u.TranslationText).ToArray()), null, shared));
        return entries.AsReadOnly();
    }

    /// <summary>同一快照只更新可变值，复用原文解析结果和位置字符串。</summary>
    public static IReadOnlyList<TranslationReadEntry> Refresh(IReadOnlyList<TranslationReadEntry> structure) =>
        Array.AsReadOnly(structure.Select(row => row with
        {
            Translation = row.Shared?.Translation ?? row.Unit!.TranslationText,
            Conflict = row.Shared?.HasConflict == true,
            Definitions = row.Shared is null ? row.Definitions : Array.AsReadOnly(row.Shared.Definitions.Select(u => u.TranslationText).ToArray())
        }).ToArray());

    public static bool Matches(IReadOnlyList<TranslationReadEntry> before, IReadOnlyList<TranslationReadEntry> after) =>
        before.Count == after.Count && before.Zip(after).All(pair =>
            ReferenceEquals(pair.First.Unit, pair.Second.Unit) && ReferenceEquals(pair.First.Shared, pair.Second.Shared) &&
            pair.First.Original == pair.Second.Original && pair.First.Translation == pair.Second.Translation &&
            pair.First.Conflict == pair.Second.Conflict && pair.First.Raw == pair.Second.Raw &&
            pair.First.Writable == pair.Second.Writable && pair.First.Definitions.SequenceEqual(pair.Second.Definitions));
}

public sealed record BulkTranslationProposal(TranslationReadEntry Target, string? Translation, string Source, string? SkipReason)
{
    public bool CanApply => SkipReason is null && Translation is not null;
}

/// <summary>一次按原文分组并校验，避免每个空条目重复扫描所有相同原文。</summary>
public sealed class BulkTranslationPlanner
{
    public IReadOnlyList<BulkTranslationProposal> Plan(IReadOnlyList<TranslationReadEntry> entries,
        IReadOnlySet<int>? targetIds = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validator = new TranslationValidator();
        var donors = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Original is null || !entry.Writable || entry.Raw || entry.Conflict || string.IsNullOrWhiteSpace(entry.Translation)) continue;
            if (!donors.TryGetValue(entry.Original, out var translations)) donors[entry.Original] = translations = new(StringComparer.Ordinal);
            if (!translations.ContainsKey(entry.Translation) && validator.Validate(entry.Original, entry.Translation).Count == 0)
                translations[entry.Translation] = entry.Impact;
        }
        var result = new List<BulkTranslationProposal>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targetIds is not null && !targetIds.Contains(entry.Id)) continue;
            string? reason = entry.Raw || !entry.Writable ? "复杂或不可编辑条目" : entry.Conflict ? "共享译文存在冲突"
                : !string.IsNullOrWhiteSpace(entry.Translation) ? "已有译文" : entry.Original is null ? "缺少原文" : null;
            var candidates = entry.Original is null ? null : donors.GetValueOrDefault(entry.Original);
            reason ??= candidates?.Count switch { 1 => null, > 1 => "存在多种有效译法", _ => "无校验通过的同原文译文" };
            result.Add(new(entry, reason is null ? candidates!.Keys.Single() : null,
                reason is null ? candidates!.Values.Single() : string.Empty, reason));
        }
        return result;
    }
}
