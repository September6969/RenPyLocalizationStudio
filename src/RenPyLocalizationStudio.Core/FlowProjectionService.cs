namespace RenPyLocalizationStudio.Core;

public sealed record FlowRow(string Key, string GroupKey, string GroupTitle, FlowNode Node, int Depth, bool IsGroupHeader = false);
public sealed record FlowProjectionGroup(string Key, string Title, IReadOnlyList<FlowRow> Rows);

public static class FlowProjectionService
{
    public static IReadOnlyList<FlowRow> Project(ProjectSnapshot snapshot, FlowGroupingMode mode)
    {
        var byId = snapshot.Graph.Nodes.ToDictionary(x => x.Id);
        var labelsByFile = snapshot.Graph.Labels.Values.GroupBy(x => x.Region.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Region.StartLine).ToArray(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<FlowRow>();
        foreach (var node in snapshot.Graph.Nodes.OrderBy(x => x.Region.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Region.StartLine))
        {
            var depth = GetDepth(node, byId);
            var groupKey = mode switch
            {
                FlowGroupingMode.SourceFile => node.Region.RelativePath,
                FlowGroupingMode.Label => FindContainingLabel(node, labelsByFile)?.LabelName ?? "（文件入口）",
                _ => "story"
            };
            var groupTitle = mode switch
            {
                FlowGroupingMode.SourceFile => node.Region.RelativePath,
                FlowGroupingMode.Label => groupKey,
                _ => "剧情路径"
            };
            rows.Add(new FlowRow(node.Id, groupKey, groupTitle, node, depth));
        }
        return rows;
    }

    public static IReadOnlyList<FlowProjectionGroup> ProjectGroups(ProjectSnapshot snapshot, FlowGroupingMode mode) =>
        Project(snapshot, mode)
            .GroupBy(row => row.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FlowProjectionGroup(group.Key, group.First().GroupTitle, group.ToArray()))
            .ToArray();
    private static int GetDepth(FlowNode node, IReadOnlyDictionary<string, FlowNode> byId)
    {
        var depth = 0; var parent = node.ParentId;
        while (parent is not null && depth < 12 && byId.TryGetValue(parent, out var value)) { depth++; parent = value.ParentId; }
        return depth;
    }
    private static FlowNode? FindContainingLabel(FlowNode node, IReadOnlyDictionary<string, FlowNode[]> labelsByFile) =>
        labelsByFile.TryGetValue(node.Region.RelativePath, out var labels) ? labels.LastOrDefault(x => x.Region.StartLine <= node.Region.StartLine) : null;
}
