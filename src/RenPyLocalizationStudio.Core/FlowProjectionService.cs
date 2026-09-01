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
        var projectedNodes = mode == FlowGroupingMode.StoryPath
            ? TraverseStory(snapshot.Graph)
            : snapshot.Graph.Nodes
                .OrderBy(x => x.Region.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Region.StartLine)
                .ToArray();
        foreach (var node in projectedNodes)
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

    private static IReadOnlyList<FlowNode> TraverseStory(FlowGraph graph)
    {
        var byId = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var outgoing = graph.Edges
            .Where(edge => edge.Kind != FlowEdgeKind.Contains)
            .GroupBy(edge => edge.FromId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(edge => EdgePriority(edge.Kind))
                    .ThenBy(edge => byId.TryGetValue(edge.ToId, out var node) ? node.Region.RelativePath : string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(edge => byId.TryGetValue(edge.ToId, out var node) ? node.Region.StartLine : int.MaxValue)
                    .ToArray(),
                StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FlowNode>(graph.Nodes.Count);
        var incoming = graph.Edges.Select(edge => edge.ToId).ToHashSet(StringComparer.Ordinal);
        var mergePredecessors = graph.Edges
            .Where(edge => edge.Kind is FlowEdgeKind.Sequence or FlowEdgeKind.End or FlowEdgeKind.Return)
            .GroupBy(edge => edge.ToId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.FromId).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        void Visit(FlowNode startNode)
        {
            var pending = new Stack<FlowNode>();
            var deferred = new Queue<FlowNode>();
            pending.Push(startNode);
            while (pending.Count > 0 || deferred.Count > 0)
            {
                if (pending.Count == 0)
                {
                    var deferredCount = deferred.Count;
                    var resumed = false;
                    for (var index = 0; index < deferredCount; index++)
                    {
                        var candidate = deferred.Dequeue();
                        if (CanVisitMerge(candidate.Id, mergePredecessors, visited))
                        {
                            pending.Push(candidate);
                            resumed = true;
                        }
                        else
                        {
                            deferred.Enqueue(candidate);
                        }
                    }

                    // 循环回边可能使汇合点永远无法满足全部前驱；交给最后的兜底遍历处理。
                    if (!resumed) break;
                }

                var node = pending.Pop();
                if (visited.Contains(node.Id)) continue;
                if (!CanVisitMerge(node.Id, mergePredecessors, visited))
                {
                    deferred.Enqueue(node);
                    continue;
                }

                visited.Add(node.Id);
                result.Add(node);
                if (!outgoing.TryGetValue(node.Id, out var edges)) continue;

                // 逆序压栈以保持边优先级定义的前序遍历，同时避免超长剧情栈溢出。
                for (var index = edges.Length - 1; index >= 0; index--)
                    if (byId.TryGetValue(edges[index].ToId, out var target)) pending.Push(target);
            }
        }

        if (graph.Labels.TryGetValue("start", out var start)) Visit(start);
        foreach (var root in graph.Nodes
                     .Where(node => !incoming.Contains(node.Id) && node.Kind != FlowNodeKind.EndOfFile)
                     .OrderBy(node => node.Region.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(node => node.Region.StartLine))
            Visit(root);
        foreach (var node in graph.Nodes.OrderBy(node => node.Region.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.Region.StartLine))
            Visit(node);
        return result;
    }

    private static bool CanVisitMerge(
        string nodeId,
        IReadOnlyDictionary<string, string[]> mergePredecessors,
        IReadOnlySet<string> visited)
        => !mergePredecessors.TryGetValue(nodeId, out var predecessors) ||
           predecessors.Length <= 1 ||
           predecessors.All(visited.Contains);

    private static int EdgePriority(FlowEdgeKind kind) => kind switch
    {
        FlowEdgeKind.Sequence => 0,
        FlowEdgeKind.Choice or FlowEdgeKind.Condition => 1,
        FlowEdgeKind.Call or FlowEdgeKind.Jump => 2,
        FlowEdgeKind.Return => 3,
        _ => 4
    };
}
