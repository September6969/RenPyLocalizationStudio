namespace RenPyLocalizationStudio.Core;

public sealed record TranslationSuggestion(string Translation, int Occurrences, string ExampleLocation)
{
    public string Description => $"匹配 {Occurrences} 条记录 · {ExampleLocation}";
}

/// <summary>只索引当前快照的精确原文；读取实时译文，不修改项目或跨项目共享数据。</summary>
public sealed class TranslationMemoryIndex
{
    private readonly string _language;
    private readonly ILookup<string, Source> _sources;
    private readonly HashSet<TranslationUnit> _units;
    private readonly HashSet<SharedStringEntry> _sharedStrings;
    private readonly TranslationValidator _validator = new();

    public TranslationMemoryIndex(ProjectSnapshot snapshot)
    {
        _language = snapshot.Language;
        _units = snapshot.TranslationUnits.ToHashSet();
        _sharedStrings = snapshot.SharedStrings.ToHashSet();
        _sources = _units
            .Where(unit => unit.Kind == TranslationUnitKind.Dialogue && !unit.IsRawMode && unit.TranslationValueSpan is not null)
            .Select(unit => new Source(GetOriginal(unit), unit, null))
            .Concat(snapshot.SharedStrings.Select(entry => new Source(entry.OldText, null, entry)))
            .Where(source => source.Original is not null && source.Language == _language)
            .ToLookup(source => source.Original!, StringComparer.Ordinal);
    }

    public IReadOnlyList<TranslationSuggestion> Find(TranslationUnit? unit, SharedStringEntry? shared = null)
    {
        // 即便调用方保留了旧条目引用，也不能跨快照或跨项目查询候选。
        if (shared is not null ? !_sharedStrings.Contains(shared) : unit is null || !_units.Contains(unit)) return [];
        if (shared is null && (unit is null || unit.IsRawMode || unit.TranslationValueSpan is null)) return [];
        if ((shared?.Language ?? unit?.Language) != _language) return [];
        var original = shared?.OldText ?? GetOriginal(unit!);
        if (original is null) return [];

        // 保留不同译法供用户判断上下文，绝不自动覆盖；冲突、空白和占位符错误不进入候选。
        return _sources[original]
            .Where(source => (unit is null || !ReferenceEquals(source.Unit, unit)) &&
                             (shared is null || !ReferenceEquals(source.Shared, shared)) &&
                             !(unit is not null && source.Shared?.Definitions.Contains(unit) == true) &&
                             source.Shared?.HasConflict != true)
            .Select(source => (source.Translation, source.Location))
            .Where(source => !string.IsNullOrWhiteSpace(source.Translation) &&
                             _validator.Validate(original, source.Translation).Count == 0)
            .GroupBy(source => source.Translation, StringComparer.Ordinal)
            .Select(group => new TranslationSuggestion(group.Key, group.Count(),
                group.Select(source => source.Location).Order(StringComparer.Ordinal).First()))
            .OrderByDescending(suggestion => suggestion.Occurrences)
            .ThenBy(suggestion => suggestion.Translation, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string? GetOriginal(TranslationUnit unit) => unit.Kind == TranslationUnitKind.String
        ? unit.OldText
        : unit.OriginalStatement is not null
            ? TextUtilities.ExtractLastQuotedString(unit.OriginalStatement)
            : unit.BoundNode?.OriginalText;

    private sealed record Source(string? Original, TranslationUnit? Unit, SharedStringEntry? Shared)
    {
        public string Language => Shared?.Language ?? Unit!.Language;
        public string Translation => Shared?.Translation ?? Unit!.TranslationText;
        public string Location => Unit is not null ? $"tl/{Language}/{Unit.RelativeTlPath}:{Unit.HeaderLine}"
            : Shared!.Definitions.FirstOrDefault() is { } definition
                ? $"tl/{Language}/{definition.RelativeTlPath}:{definition.HeaderLine}" : "共享字符串";
    }
}
