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

    [GeneratedRegex("^\\s*(?:(?<speaker>[A-Za-z_][A-Za-z0-9_\\.]*)\\s+)?(?<quote>[\"'])(?<text>(?:\\\\.|(?!\\k<quote>).)*)\\k<quote>(?:\\s+[^#]+)?\\s*(?:#.*)?$")]
    private static partial Regex DialogueRegex();

    [GeneratedRegex("^\\s*(?:python(?:\\s+(?:early|hide|in\\s+[A-Za-z_][A-Za-z0-9_.]*))*|init(?:\\s+-?\\d+)?(?:\\s+python(?:\\s+(?:early|hide|in\\s+[A-Za-z_][A-Za-z0-9_.]*))*)?|screen\\s+[^:]+)\\s*:\\s*(?:#.*)?$")]
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
                document.Graph.Labels[name] = node;
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
                var node = CreateNode(document, FlowNodeKind.Condition, line, indent, $"{type} {condition}", condition: condition, parentId: CurrentParent(contexts));
                contexts.Push(new ParseContext(indent, node.Id, ContextKind.Condition));
                continue;
            }

            if (ElseRegex().IsMatch(line.Content))
            {
                var node = CreateNode(document, FlowNodeKind.Condition, line, indent, "else", condition: "else", parentId: CurrentParent(contexts));
                contexts.Push(new ParseContext(indent, node.Id, ContextKind.Condition));
                continue;
            }

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
                var speaker = dialogueMatch.Groups["speaker"].Success ? dialogueMatch.Groups["speaker"].Value : null;
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
            Indent = indent,
            IsDynamic = isDynamic
        };
        document.Graph.Nodes.Add(node);
        return node;
    }

    private static void BuildEdges(FlowGraph graph)
    {
        foreach (var group in graph.Nodes.GroupBy(node => node.ParentId))
        {
            var children = group.OrderBy(node => node.Region.StartLine).ToList();
            for (var index = 1; index < children.Count; index++)
            {
                var previous = children[index - 1];
                var current = children[index];
                if (previous.Kind is not (FlowNodeKind.Jump or FlowNodeKind.Return or FlowNodeKind.Unresolved) &&
                    current.Kind != FlowNodeKind.Choice)
                {
                    graph.Edges.Add(new FlowEdge(previous.Id, current.Id, FlowEdgeKind.Sequence));
                }
            }
        }

        foreach (var node in graph.Nodes.Where(node => node.ParentId is not null))
        {
            var parent = graph.Nodes.FirstOrDefault(candidate => candidate.Id == node.ParentId);
            if (parent is null)
            {
                continue;
            }

            var edgeKind = node.Kind switch
            {
                FlowNodeKind.Choice => FlowEdgeKind.Choice,
                FlowNodeKind.Condition => FlowEdgeKind.Condition,
                _ => FlowEdgeKind.Contains
            };
            graph.Edges.Add(new FlowEdge(parent.Id, node.Id, edgeKind, node.Condition));
        }

        foreach (var node in graph.Nodes.Where(node => node.Kind is FlowNodeKind.Jump or FlowNodeKind.Call))
        {
            if (node.Target is null || !graph.Labels.TryGetValue(node.Target, out var target))
            {
                continue;
            }

            graph.Edges.Add(new FlowEdge(node.Id, target.Id, node.Kind == FlowNodeKind.Jump ? FlowEdgeKind.Jump : FlowEdgeKind.Call));
        }
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
