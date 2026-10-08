using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.ViewModels;

public enum TranslationStatusFilter { All, Pending, Completed, Conflict, Modified }
public sealed record TranslationFilterOption(TranslationStatusFilter Value, string Label);
public sealed record TranslationScopeOption(string Id, string Label);

internal sealed class TranslationProgressScopes
{
    private readonly Dictionary<object, HashSet<string>> _scopes;
    private readonly ProjectSnapshot? _snapshot;
    private readonly Dictionary<string, TranslationScopeOption> _options;
    private readonly Dictionary<string, FlowNode[]> _labelsByPath;
    private static object ScopeKey(ContentItem item) => (object?)item.Node ?? (object?)item.SharedString ?? item.Unit ?? (object)item;
    public IReadOnlyList<TranslationScopeOption> Options { get; }

    public TranslationProgressScopes(ProjectSnapshot? snapshot, IReadOnlyList<ContentItem> items, TranslationProgressScopes? previous = null)
    {
        _snapshot = snapshot;
        var reuse = snapshot is not null && ReferenceEquals(snapshot, previous?._snapshot);
        _scopes = reuse ? previous!._scopes : new();
        var options = _options = reuse ? previous!._options : new(StringComparer.OrdinalIgnoreCase);
        // 范围选项来自完整项目，避免修复某文件的最后一个问题后悄悄跳回全部文件。
        if (snapshot is not null && !reuse)
        {
            foreach (var source in snapshot.Sources)
                options.TryAdd("file:" + source.RelativePath, new TranslationScopeOption("file:" + source.RelativePath, "文件 · " + source.RelativePath));
            foreach (var label in snapshot.Graph.Labels.Values)
                options.TryAdd("label:" + label.Id, new TranslationScopeOption("label:" + label.Id, "Label · " + label.LabelName));
            foreach (var unit in snapshot.TranslationUnits.Where(unit => unit.SourcePath is null))
            {
                var path = $"tl/{snapshot.Language}/{unit.RelativeTlPath}";
                options.TryAdd("file:" + path, new TranslationScopeOption("file:" + path, "文件 · " + path));
            }
        }
        var labelsByPath = _labelsByPath = reuse ? previous!._labelsByPath : snapshot?.Graph.Labels.Values.GroupBy(node => node.Region.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Region.StartLine).ToArray(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, FlowNode[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (_scopes.ContainsKey(ScopeKey(item))) continue;
            var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, line) in Locations(item, snapshot?.Language ?? string.Empty))
            {
                var fileId = "file:" + path;
                scopes.Add(fileId);
                options.TryAdd(fileId, new TranslationScopeOption(fileId, "文件 · " + path));
                if (!labelsByPath.TryGetValue(path, out var labels)) continue;
                // 在已排序的 label 中二分定位，避免每个条目重复扫描整个文件的 label。
                var low = 0;
                var high = labels.Length;
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (labels[middle].Region.StartLine <= line) low = middle + 1;
                    else high = middle;
                }
                if (low == 0) continue;
                var label = labels[low - 1];
                var labelId = "label:" + label.Id;
                scopes.Add(labelId);
                options.TryAdd(labelId, new TranslationScopeOption(labelId, "Label · " + label.LabelName));
            }
            _scopes[ScopeKey(item)] = scopes;
        }
        Options = [new("all", "全部文件 / Label"), .. options.Values.OrderBy(option => option.Label, StringComparer.Ordinal)];
    }

    public bool Contains(ContentItem item, string scope) => scope == "all" ||
        (_scopes.TryGetValue(ScopeKey(item), out var scopes) && scopes.Contains(scope));

    private static IEnumerable<(string Path, int Line)> Locations(ContentItem item, string language)
    {
        if (item.Node is { } node) yield return (node.Region.RelativePath, node.Region.StartLine);
        else if (item.SharedString is { } shared)
        {
            foreach (var reference in shared.References)
                yield return (reference.Unit is { SourcePath: null } definition && reference.Node is null
                    ? $"tl/{language}/{definition.RelativeTlPath}" : reference.SourcePath,
                    reference.SourceKind == StringSourceKind.Menu ? reference.SourceLine : 0);
            if (shared.References.Count == 0)
                foreach (var unit in shared.Definitions) yield return ($"tl/{language}/{unit.RelativeTlPath}", unit.HeaderLine);
        }
        else if (item.Unit is { } unit)
            yield return (unit.SourcePath ?? $"tl/{language}/{unit.RelativeTlPath}", unit.SourceLine ?? unit.HeaderLine);
    }
}
