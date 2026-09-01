using System.Text.RegularExpressions;

namespace RenPyLocalizationStudio.Core;

public sealed partial class RenPySourceParser
{
    [GeneratedRegex("^\\s*label\\s+(?<name>\\.?[A-Za-z_][A-Za-z0-9_\\.]*)[^:]*:\\s*(?:#.*)?$")]
    private static partial Regex LabelRegex();

    [GeneratedRegex("^\\s*menu(?:\\s+[^:]*)?:\\s*(?:#.*)?$")]
    private static partial Regex MenuRegex();

    [GeneratedRegex("^\\s*(?<quote>[\"'])(?<text>(?:\\\\.|(?!\\k<quote>).)*)\\k<quote>\\s*(?:if\\s+(?<condition>.+?))?:\\s*(?:#.*)?$")]
    private static partial Regex ChoiceRegex();

    [GeneratedRegex("^\\s*(?<type>if|elif)\\s+(?<condition>.+):\\s*(?:#.*)?$")]
    private static partial Regex ConditionRegex();

    [GeneratedRegex("^\\s*else\\s*:\\s*(?:#.*)?$")]
    private static partial Regex ElseRegex();

    [GeneratedRegex("^\\s*(?<kind>jump|call)\\s+(?<target>.+?)\\s*(?:#.*)?$")]
    private static partial Regex TransferRegex();

    [GeneratedRegex("^\\s*return(?:\\s+.+)?\\s*(?:#.*)?$")]
    private static partial Regex ReturnRegex();

    [GeneratedRegex("^\\s*(?:(?<speaker>[A-Za-z_][A-Za-z0-9_\\.]*)(?<attributes>(?:\\s+[A-Za-z_][A-Za-z0-9_\\.]*)*)\\s+)?(?<quote>[\"'])(?<text>(?:\\\\.|(?!\\k<quote>).)*)\\k<quote>(?:\\s+[^#]+)?\\s*(?:#.*)?$")]
    private static partial Regex DialogueRegex();

    [GeneratedRegex("^\\s*(?:python(?:\\s+(?:early|hide|in\\s+[A-Za-z_][A-Za-z0-9_.]*))*|init(?:\\s+-?\\d+)?(?:\\s+python(?:\\s+(?:early|hide|in\\s+[A-Za-z_][A-Za-z0-9_.]*))*)?|screen\\s+[^:]+|transform\\s+[^:]+|layeredimage\\s+[^:]+|image\\s+[^:=]+|style\\s+[^:]+|translate\\s+[^:]+)\\s*:\\s*(?:#.*)?$")]
    private static partial Regex OpaqueBlockRegex();

    [GeneratedRegex("(?:_|text|textbutton|tooltip)\\s*\\(?\\s*(?<quote>[\"'])(?<text>(?:\\\\.|(?!\\k<quote>).)*)\\k<quote>")]
    private static partial Regex ScreenStringRegex();

    [GeneratedRegex("^\\s*init(?:\\s+-?\\d+)?\\s*:\\s*(?:#.*)?$")]
    private static partial Regex InitBlockRegex();

    public SourceDocument Parse(Utf8TextFile file, string relativePath)
    {
        var document = new SourceDocument
        {
            File = file,
            RelativePath = TextUtilities.NormalizePath(relativePath)
        };
        var lines = TextUtilities.SliceLines(file.Text);
        var contexts = new Stack<ParseContext>();
        var conditionalChains = new Dictionary<(string? ParentId, int Indent), string>();
        string? currentGlobalLabel = null;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Content.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = TextUtilities.GetIndent(line.Content);
            if (line.Content.TakeWhile(character => character is ' ' or '\t').Contains('\t'))
            {
                document.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "SOURCE_TAB_INDENT",
                    "检测到制表符缩进，分析时按四个空格处理。",
                    document.RelativePath,
                    line.Number));
            }

            while (contexts.Count > 0 && indent <= contexts.Peek().HeaderIndent)
            {
                contexts.Pop();
            }

            if (IsOneLinePython(trimmed))
            {
                continue;
            }

            if (OpaqueBlockRegex().IsMatch(line.Content) || InitBlockRegex().IsMatch(line.Content))
            {
                var isScreen = trimmed.StartsWith("screen ", StringComparison.Ordinal);
                var endIndex = FindOpaqueBlockEnd(lines, index, indent);
                if (isScreen)
                {
                    var region = new SourceRegion(document.RelativePath, line.Number, lines[Math.Max(index, endIndex - 1)].Number);
                    document.ScreenRegions.Add(region);
                    IndexScreenStrings(document, lines, index + 1, endIndex);
                }

                index = Math.Max(index, endIndex - 1);
                continue;
            }

            var labelMatch = LabelRegex().Match(line.Content);
            if (labelMatch.Success)
            {
                var rawName = labelMatch.Groups["name"].Value;
                var name = ResolveLocalLabel(rawName, currentGlobalLabel);
                if (!rawName.StartsWith(".", StringComparison.Ordinal))
                {
                    currentGlobalLabel = rawName;
                }

                var node = CreateNode(document, FlowNodeKind.Label, line, indent, $"label {name}", labelName: name);
                if (!document.Graph.Labels.TryAdd(name, node))
                {
                    document.Diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "DUPLICATE_LABEL",
                        $"label {name} 在同一文件中存在重复定义。",
                        document.RelativePath,
                        line.Number,
                        DiagnosticCategory.Parsing));
                }
                contexts.Push(new ParseContext(indent, node.Id, ContextKind.Label));
                continue;
            }

            if (MenuRegex().IsMatch(line.Content))
            {
                var node = CreateNode(document, FlowNodeKind.Menu, line, indent, "选择菜单", parentId: CurrentParent(contexts));
                contexts.Push(new ParseContext(indent, node.Id, ContextKind.Menu));
                continue;
            }

            var choiceMatch = ChoiceRegex().Match(line.Content);
            if (choiceMatch.Success && contexts.Any(context => context.Kind == ContextKind.Menu))
            {
                while (contexts.Count > 0 && contexts.Peek().Kind == ContextKind.Choice && indent <= contexts.Peek().HeaderIndent)
                {
                    contexts.Pop();
                }

                var rawText = choiceMatch.Groups["text"].Value;
                var text = TextUtilities.UnescapeRenPyString(rawText);
                var condition = choiceMatch.Groups["condition"].Success ? choiceMatch.Groups["condition"].Value.Trim() : null;
                var menu = contexts.First(context => context.Kind == ContextKind.Menu);
                var node = CreateNode(
                    document,
                    FlowNodeKind.Choice,
                    line,
                    indent,
                    condition is null ? $"选项：{text}" : $"选项：{text}  [if {condition}]",
                    originalText: text,
                    condition: condition,
                    parentId: menu.NodeId);
                contexts.Push(new ParseContext(indent, node.Id, ContextKind.Choice));
                continue;
            }

            var conditionMatch = ConditionRegex().Match(line.Content);
            if (conditionMatch.Success)
            {
                var condition = conditionMatch.Groups["condition"].Value.Trim();
                var type = conditionMatch.Groups["type"].Value;
                var parentId = CurrentParent(contexts);
                var key = (parentId, indent);
                var branchGroupId = type == "if" || !conditionalChains.TryGetValue(key, out var existingGroup)
                    ? $"{document.RelativePath}:{line.Number}:condition-chain"
                    : existingGroup;
                conditionalChains[key] = branchGroupId;
                var node = CreateNode(document, FlowNodeKind.Condition, line, indent, $"{type} {condition}", condition: condition,
                    parentId: parentId, branchGroupId: branchGroupId);
                contexts.Push(new ParseContext(indent, node.Id, ContextKind.Condition));
                continue;
            }

            if (ElseRegex().IsMatch(line.Content))
            {
                var parentId = CurrentParent(contexts);
                var key = (parentId, indent);
                var branchGroupId = conditionalChains.TryGetValue(key, out var existingGroup)
                    ? existingGroup
                    : $"{document.RelativePath}:{line.Number}:condition-chain";
                conditionalChains[key] = branchGroupId;
                var node = CreateNode(document, FlowNodeKind.Condition, line, indent, "else", condition: "else",
                    parentId: parentId, branchGroupId: branchGroupId);
                contexts.Push(new ParseContext(indent, node.Id, ContextKind.Condition));
                continue;
            }

            conditionalChains.Remove((CurrentParent(contexts), indent));

            var transferMatch = TransferRegex().Match(line.Content);
            if (transferMatch.Success)
            {
                var kind = transferMatch.Groups["kind"].Value;
                var rawTarget = transferMatch.Groups["target"].Value.Trim();
                if (kind == "call" && rawTarget.StartsWith("screen ", StringComparison.Ordinal))
                {
                    continue;
                }

                var expression = rawTarget.StartsWith("expression ", StringComparison.Ordinal);
                var target = expression
                    ? TrimCallArguments(rawTarget["expression ".Length..].Trim())
                    : ResolveLocalLabel(TrimCallArguments(rawTarget), currentGlobalLabel);
                CreateNode(
                    document,
                    expression ? FlowNodeKind.Unresolved : kind == "jump" ? FlowNodeKind.Jump : FlowNodeKind.Call,
                    line,
                    indent,
                    expression ? $"{kind} expression（动态目标）" : $"{kind} → {target}",
                    target: target,
                    parentId: CurrentParent(contexts),
                    isDynamic: expression);
                if (expression)
                {
                    document.Diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        "DYNAMIC_TRANSFER",
                        $"无法静态解析 {kind} expression：{target}",
                        document.RelativePath,
                        line.Number));
                }

                continue;
            }

            if (ReturnRegex().IsMatch(line.Content))
            {
                CreateNode(document, FlowNodeKind.Return, line, indent, "return", parentId: CurrentParent(contexts));
                continue;
            }

            var dialogueMatch = DialogueRegex().Match(line.Content);
            if (dialogueMatch.Success)
            {
                var text = TextUtilities.UnescapeRenPyString(dialogueMatch.Groups["text"].Value);
                var speaker = dialogueMatch.Groups["speaker"].Success
                    ? (dialogueMatch.Groups["speaker"].Value + dialogueMatch.Groups["attributes"].Value).Trim()
                    : null;
                CreateNode(
                    document,
                    FlowNodeKind.Dialogue,
                    line,
                    indent,
                    speaker is null ? text : $"{speaker}: {text}",
                    speaker: speaker,
                    originalText: text,
                    parentId: CurrentParent(contexts));
            }
        }

        var endLine = lines.Count == 0 ? 1 : lines[^1].Number;
        CreateNode(document, FlowNodeKind.EndOfFile, new LineSlice(endLine, file.Text.Length, 0, string.Empty), 0, "文件结束");
        BuildEdges(document.Graph);
        return document;
    }

    private static FlowNode CreateNode(
        SourceDocument document,
        FlowNodeKind kind,
        LineSlice line,
        int indent,
        string displayText,
        string? labelName = null,
        string? speaker = null,
        string? target = null,
        string? condition = null,
        string? originalText = null,
        string? parentId = null,
        string? branchGroupId = null,
        bool isDynamic = false)
    {
        var node = new FlowNode
        {
            Id = $"{document.RelativePath}:{line.Number}:{kind}",
            Kind = kind,
            DisplayText = displayText,
            Region = new SourceRegion(document.RelativePath, line.Number, line.Number),
            LabelName = labelName,
            Speaker = speaker,
            Target = target,
            Condition = condition,
            OriginalText = originalText,
            ParentId = parentId,
            BranchGroupId = branchGroupId,
            Indent = indent,
            IsDynamic = isDynamic
        };
        document.Graph.Nodes.Add(node);
        return node;
    }

    private static void BuildEdges(FlowGraph graph)
    {
        var byId = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var children = graph.Nodes
            .Where(node => node.ParentId is not null)
            .GroupBy(node => node.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Region.StartLine).ToArray(), StringComparer.Ordinal);
        IReadOnlyList<FlowNode> BuildNode(FlowNode node)
        {
            if (node.Kind is FlowNodeKind.Jump or FlowNodeKind.Return or FlowNodeKind.Unresolved)
                return [];
            if (!children.TryGetValue(node.Id, out var nested) || nested.Length == 0)
                return [node];

            if (node.Kind == FlowNodeKind.Menu)
            {
                var exits = new List<FlowNode>();
                var firstChoiceIndex = Array.FindIndex(nested, child => child.Kind == FlowNodeKind.Choice);
                var captions = firstChoiceIndex > 0 ? nested[..firstChoiceIndex] : [];
                var choiceSourceExits = new List<FlowNode> { node };
                if (captions.Length > 0)
                {
                    ConnectSequence(node, captions);
                    choiceSourceExits = GetSequenceExits(captions).ToList();
                }

                var choices = nested.Where(child => child.Kind == FlowNodeKind.Choice).ToArray();
                foreach (var choice in choices)
                {
                    foreach (var source in choiceSourceExits)
                        graph.Edges.Add(new FlowEdge(source.Id, choice.Id, FlowEdgeKind.Choice, choice.Condition));
                    exits.AddRange(BuildNode(choice));
                }
                return choices.Length == 0 ? choiceSourceExits : exits;
            }

            ConnectSequence(node, nested);
            return GetSequenceExits(nested);
        }

        void ConnectExit(FlowNode from, FlowNode to, FlowEdgeKind kind = FlowEdgeKind.Sequence, string? caption = null)
        {
            if (from.Kind == FlowNodeKind.Call)
            {
                graph.CallContinuations[from.Id] = to.Id;
                return;
            }
            if (from.Kind is FlowNodeKind.Jump or FlowNodeKind.Return or FlowNodeKind.Unresolved) return;
            graph.Edges.Add(new FlowEdge(from.Id, to.Id, kind, caption));
        }

        IReadOnlyList<FlowNode> GetSequenceExits(IReadOnlyList<FlowNode> sequence)
        {
            if (sequence.Count == 0) return [];
            var last = sequence[^1];
            if (last.Kind == FlowNodeKind.Condition && last.BranchGroupId is not null)
            {
                var branchGroup = sequence.Where(node => node.BranchGroupId == last.BranchGroupId).ToArray();
                var exits = branchGroup.SelectMany(BuildNode).ToList();
                if (!branchGroup.Any(node => node.Condition == "else")) exits.Add(branchGroup[^1]);
                return exits;
            }
            return BuildNode(last);
        }

        void ConnectSequence(FlowNode parent, IReadOnlyList<FlowNode> sequence)
        {
            var previousExits = new List<FlowNode> { parent };
            for (var index = 0; index < sequence.Count;)
            {
                var current = sequence[index];
                if (current.Kind == FlowNodeKind.Condition && current.BranchGroupId is not null)
                {
                    var branchGroup = sequence.Skip(index)
                        .TakeWhile(node => node.Kind == FlowNodeKind.Condition && node.BranchGroupId == current.BranchGroupId)
                        .ToArray();
                    foreach (var branch in branchGroup)
                    {
                        foreach (var previous in previousExits)
                            ConnectExit(previous, branch, FlowEdgeKind.Condition, branch.Condition);
                    }
                    previousExits = branchGroup.SelectMany(BuildNode).ToList();
                    if (!branchGroup.Any(node => node.Condition == "else")) previousExits.Add(branchGroup[^1]);
                    index += branchGroup.Length;
                    continue;
                }

                foreach (var previous in previousExits) ConnectExit(previous, current);
                previousExits = BuildNode(current).ToList();
                index++;
            }
        }

        var labelExits = new Dictionary<string, IReadOnlyList<FlowNode>>(StringComparer.Ordinal);
        foreach (var label in graph.Nodes.Where(node => node.Kind == FlowNodeKind.Label))
        {
            if (children.TryGetValue(label.Id, out var labelChildren))
            {
                ConnectSequence(label, labelChildren);
                labelExits[label.Id] = GetSequenceExits(labelChildren);
            }
            else
            {
                labelExits[label.Id] = [label];
            }
        }

        // 同一文件内按物理顺序自然落入下一个 label；文件末尾统一落到 EndOfFile。
        foreach (var fileGroup in graph.Nodes.GroupBy(node => node.Region.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var labels = fileGroup.Where(node => node.Kind == FlowNodeKind.Label).OrderBy(node => node.Region.StartLine).ToArray();
            var end = fileGroup.Single(node => node.Kind == FlowNodeKind.EndOfFile);
            for (var index = 0; index < labels.Length; index++)
            {
                var continuation = index + 1 < labels.Length ? labels[index + 1] : end;
                foreach (var exit in labelExits[labels[index].Id]) ConnectExit(exit, continuation,
                    continuation.Kind == FlowNodeKind.EndOfFile ? FlowEdgeKind.End : FlowEdgeKind.Sequence);
            }
        }

        foreach (var rootGroup in graph.Nodes.Where(node => node.ParentId is null && node.Kind != FlowNodeKind.Label)
                     .GroupBy(node => node.Region.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var roots = rootGroup.OrderBy(node => node.Region.StartLine).ToArray();
            for (var index = 1; index < roots.Length; index++) ConnectExit(roots[index - 1], roots[index]);
        }

        foreach (var node in graph.Nodes.Where(node => node.Kind is FlowNodeKind.Jump or FlowNodeKind.Call))
        {
            if (node.Target is null || !graph.Labels.TryGetValue(node.Target, out var target))
            {
                continue;
            }

            graph.Edges.Add(new FlowEdge(node.Id, target.Id, node.Kind == FlowNodeKind.Jump ? FlowEdgeKind.Jump : FlowEdgeKind.Call));
        }

        foreach (var (callId, continuationId) in graph.CallContinuations)
        {
            if (!byId.TryGetValue(callId, out var call) || call.Target is null || !graph.Labels.TryGetValue(call.Target, out var targetLabel)) continue;
            foreach (var returnNode in graph.Nodes.Where(node => node.Kind == FlowNodeKind.Return && IsDescendantOf(node, targetLabel.Id, byId)))
                graph.Edges.Add(new FlowEdge(returnNode.Id, continuationId, FlowEdgeKind.Return, $"return → {continuationId}"));
        }
    }

    private static bool IsDescendantOf(FlowNode node, string ancestorId, IReadOnlyDictionary<string, FlowNode> byId)
    {
        var parentId = node.ParentId;
        while (parentId is not null && byId.TryGetValue(parentId, out var parent))
        {
            if (parent.Id == ancestorId) return true;
            parentId = parent.ParentId;
        }
        return false;
    }

    private static int FindOpaqueBlockEnd(IReadOnlyList<LineSlice> lines, int headerIndex, int headerIndent)
    {
        var lexicalState = new PythonLexicalState();
        for (var index = headerIndex + 1; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Content.Trim();
            var indent = TextUtilities.GetIndent(line.Content);
            if (lexicalState.IsNeutral && trimmed.Length > 0 && !trimmed.StartsWith('#') && indent <= headerIndent)
            {
                return index;
            }

            lexicalState.Consume(line.Content);
        }

        return lines.Count;
    }

    private static void IndexScreenStrings(SourceDocument document, IReadOnlyList<LineSlice> lines, int startIndex, int endIndex)
    {
        for (var index = startIndex; index < endIndex; index++)
        {
            foreach (Match match in ScreenStringRegex().Matches(lines[index].Content))
            {
                document.StringOccurrences.Add(new SourceStringOccurrence(
                    TextUtilities.UnescapeRenPyString(match.Groups["text"].Value),
                    new SourceRegion(document.RelativePath, lines[index].Number, lines[index].Number),
                    StringSourceKind.Screen));
            }
        }
    }

    private static bool IsOneLinePython(string trimmed) => trimmed.StartsWith('$') && !trimmed.StartsWith("$$", StringComparison.Ordinal);

    private static string? CurrentParent(Stack<ParseContext> contexts) => contexts.Count == 0 ? null : contexts.Peek().NodeId;

    private static string ResolveLocalLabel(string name, string? currentGlobalLabel)
        => name.StartsWith(".", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(currentGlobalLabel)
            ? currentGlobalLabel + name
            : name;

    private static string TrimCallArguments(string target)
    {
        var fromIndex = target.IndexOf(" from ", StringComparison.Ordinal);
        if (fromIndex >= 0)
        {
            target = target[..fromIndex];
        }

        var argumentIndex = target.IndexOf('(');
        return (argumentIndex >= 0 ? target[..argumentIndex] : target).Trim();
    }

    private sealed record ParseContext(int HeaderIndent, string NodeId, ContextKind Kind);

    private enum ContextKind
    {
        Label,
        Menu,
        Choice,
        Condition
    }

    private sealed class PythonLexicalState
    {
        private char? _quote;
        private bool _triple;
        private bool _escaped;
        private int _bracketDepth;

        public bool IsNeutral => _quote is null && _bracketDepth == 0;

        public void Consume(string line)
        {
            var inComment = false;
            for (var index = 0; index < line.Length && !inComment; index++)
            {
                var character = line[index];
                if (_quote is not null)
                {
                    if (_escaped)
                    {
                        _escaped = false;
                        continue;
                    }

                    if (character == '\\')
                    {
                        _escaped = true;
                        continue;
                    }

                    if (_triple && character == _quote && index + 2 < line.Length && line[index + 1] == _quote && line[index + 2] == _quote)
                    {
                        _quote = null;
                        _triple = false;
                        index += 2;
                    }
                    else if (!_triple && character == _quote)
                    {
                        _quote = null;
                    }

                    continue;
                }

                if (character == '#')
                {
                    inComment = true;
                }
                else if (character is '\'' or '"')
                {
                    _quote = character;
                    _triple = index + 2 < line.Length && line[index + 1] == character && line[index + 2] == character;
                    if (_triple)
                    {
                        index += 2;
                    }
                }
                else if (character is '(' or '[' or '{')
                {
                    _bracketDepth++;
                }
                else if (character is ')' or ']' or '}')
                {
                    _bracketDepth = Math.Max(0, _bracketDepth - 1);
                }
            }

            if (_quote is null && line.TrimEnd().EndsWith('\\'))
            {
                _bracketDepth = Math.Max(1, _bracketDepth);
            }
            else if (_quote is null && _bracketDepth == 1 && !line.TrimEnd().EndsWith('\\') &&
                     !line.Any(character => character is '(' or '[' or '{'))
            {
                _bracketDepth = 0;
            }
        }
    }
}
