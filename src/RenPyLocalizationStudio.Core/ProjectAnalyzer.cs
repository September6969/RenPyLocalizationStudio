namespace RenPyLocalizationStudio.Core;

public sealed class ProjectAnalyzer
{
    private readonly RenPySourceParser _sourceParser = new();
    private readonly TlParser _tlParser = new();

    internal ProjectSnapshot Analyze(
        string projectRoot,
        string gameDirectory,
        string language,
        IReadOnlyList<(Utf8TextFile File, string RelativePath)> sourceFiles,
        IReadOnlyList<(Utf8TextFile File, string RelativePath)> translationFiles)
    {
        var snapshot = new ProjectSnapshot
        {
            ProjectRoot = projectRoot,
            GameDirectory = gameDirectory,
            Language = language
        };

        foreach (var source in sourceFiles)
        {
            try
            {
                snapshot.Sources.Add(_sourceParser.Parse(source.File, source.RelativePath));
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                snapshot.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "SOURCE_PARSE_FAILED",
                    exception.Message,
                    TextUtilities.NormalizePath(source.RelativePath)));
            }
        }

        foreach (var translation in translationFiles)
        {
            try
            {
                snapshot.TlDocuments.Add(_tlParser.Parse(translation.File, translation.RelativePath, language));
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                snapshot.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "TL_PARSE_FAILED",
                    exception.Message,
                    TextUtilities.NormalizePath(translation.RelativePath)));
            }
        }

        MergeGraphs(snapshot);
        BindTranslations(snapshot);
        foreach (var diagnostic in snapshot.Sources.SelectMany(source => source.Diagnostics)
                     .Concat(snapshot.TlDocuments.SelectMany(document => document.Diagnostics)))
        {
            snapshot.Diagnostics.Add(diagnostic);
        }

        return snapshot;
    }

    private static void MergeGraphs(ProjectSnapshot snapshot)
    {
        foreach (var source in snapshot.Sources)
        {
            snapshot.Graph.Nodes.AddRange(source.Graph.Nodes);
            snapshot.Graph.Edges.AddRange(source.Graph.Edges);
            foreach (var (name, node) in source.Graph.Labels)
            {
                if (!snapshot.Graph.Labels.TryAdd(name, node))
                {
                    snapshot.Diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "DUPLICATE_LABEL",
                        $"label {name} 存在重复定义。",
                        node.Region.RelativePath,
                        node.Region.StartLine));
                }
            }
        }

        var existing = snapshot.Graph.Edges.Select(edge => (edge.FromId, edge.ToId, edge.Kind)).ToHashSet();
        foreach (var node in snapshot.Graph.Nodes.Where(node => node.Kind is FlowNodeKind.Jump or FlowNodeKind.Call))
        {
            if (node.Target is not null && snapshot.Graph.Labels.TryGetValue(node.Target, out var target))
            {
                var kind = node.Kind == FlowNodeKind.Jump ? FlowEdgeKind.Jump : FlowEdgeKind.Call;
                if (existing.Add((node.Id, target.Id, kind)))
                {
                    snapshot.Graph.Edges.Add(new FlowEdge(node.Id, target.Id, kind));
                }
            }
            else
            {
                snapshot.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "UNRESOLVED_LABEL",
                    $"找不到跳转目标：{node.Target}",
                    node.Region.RelativePath,
                    node.Region.StartLine));
            }
        }
    }

    private static void BindTranslations(ProjectSnapshot snapshot)
    {
        var sourcesByPath = snapshot.Sources.ToDictionary(source => source.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var unit in snapshot.TranslationUnits)
        {
            if (unit.SourcePath is null || unit.SourceLine is null)
            {
                continue;
            }

            var normalizedPath = NormalizeSourcePath(unit.SourcePath, snapshot.ProjectRoot);
            if (!sourcesByPath.TryGetValue(normalizedPath, out var source))
            {
                continue;
            }

            var candidates = source.Graph.Nodes.Where(node => node.Region.StartLine == unit.SourceLine).ToList();
            FlowNode? bound = unit.Kind == TranslationUnitKind.String
                ? candidates.FirstOrDefault(node => node.Kind == FlowNodeKind.Choice && node.OriginalText == unit.OldText)
                : candidates.FirstOrDefault(node => node.Kind == FlowNodeKind.Dialogue);
            if (bound is null && unit.Kind == TranslationUnitKind.String && unit.OldText is not null)
            {
                var fallback = source.Graph.Nodes
                    .Where(node => node.Kind == FlowNodeKind.Choice && node.OriginalText == unit.OldText)
                    .ToList();
                if (fallback.Count == 1)
                {
                    bound = fallback[0];
                }
                else if (fallback.Count > 1)
                {
                    snapshot.Diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        "AMBIGUOUS_BINDING",
                        $"字符串条目 {unit.OldText} 的行号已失效，且同一源码文件内存在多个菜单候选。",
                        unit.RelativeTlPath,
                        unit.HeaderLine));
                }
            }

            if (bound is null && unit.Kind == TranslationUnitKind.Dialogue && unit.OriginalStatement is not null)
            {
                var originalText = ExtractLastQuotedValue(unit.OriginalStatement);
                var fallback = source.Graph.Nodes
                    .Where(node => node.Kind == FlowNodeKind.Dialogue && node.OriginalText == originalText)
                    .ToList();
                if (fallback.Count == 1)
                {
                    bound = fallback[0];
                }
                else if (fallback.Count > 1)
                {
                    snapshot.Diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        "AMBIGUOUS_BINDING",
                        $"翻译块 {unit.Identifier} 的行号已失效，且原文存在多个候选位置。",
                        unit.RelativeTlPath,
                        unit.HeaderLine));
                }
            }

            unit.BoundNode = bound;
            unit.SourceKind = source.ScreenRegions.Any(region => region.Contains(unit.SourceLine.Value))
                ? StringSourceKind.Screen
                : bound?.Kind == FlowNodeKind.Choice ? StringSourceKind.Menu : StringSourceKind.Other;
        }

        var stringUnits = snapshot.TranslationUnits.Where(unit => unit.Kind == TranslationUnitKind.String && unit.OldText is not null);
        foreach (var group in stringUnits.GroupBy(unit => unit.OldText!, StringComparer.Ordinal))
        {
            var entry = new SharedStringEntry
            {
                Language = snapshot.Language,
                OldText = group.Key
            };
            entry.Definitions.AddRange(group);
            var translations = group.Select(unit => unit.TranslationText).Distinct(StringComparer.Ordinal).ToList();
            entry.HasConflict = translations.Count > 1;
            entry.LoadTranslation(translations.FirstOrDefault() ?? string.Empty);

            foreach (var definition in entry.Definitions)
            {
                entry.References.Add(new TranslationBinding(
                    definition,
                    definition.BoundNode,
                    definition.SourcePath ?? definition.RelativeTlPath,
                    definition.SourceLine ?? definition.HeaderLine,
                    definition.SourceKind));
            }

            foreach (var choice in snapshot.Graph.Nodes.Where(node => node.Kind == FlowNodeKind.Choice && node.OriginalText == group.Key))
            {
                if (entry.References.All(reference => reference.Node?.Id != choice.Id))
                {
                    entry.References.Add(new TranslationBinding(null, choice, choice.Region.RelativePath, choice.Region.StartLine, StringSourceKind.Menu));
                }
            }

            foreach (var occurrence in snapshot.Sources.SelectMany(source => source.StringOccurrences).Where(occurrence => occurrence.Text == group.Key))
            {
                if (entry.References.All(reference => reference.SourcePath != occurrence.Region.RelativePath || reference.SourceLine != occurrence.Region.StartLine))
                {
                    entry.References.Add(new TranslationBinding(null, null, occurrence.Region.RelativePath, occurrence.Region.StartLine, occurrence.SourceKind));
                }
            }

            if (entry.HasConflict)
            {
                snapshot.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "DUPLICATE_STRING_CONFLICT",
                    $"相同 old 存在冲突译文：{group.Key}"));
            }

            snapshot.SharedStrings.Add(entry);
        }

        foreach (var unit in snapshot.TranslationUnits.Where(unit => unit.IsUnboundFlowTranslation))
        {
            snapshot.Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Info,
                "UNBOUND_TRANSLATION",
                $"翻译块未绑定：{unit.Identifier}",
                unit.RelativeTlPath,
                unit.HeaderLine));
        }
    }

    private static string NormalizeSourcePath(string sourcePath, string projectRoot)
    {
        var normalized = TextUtilities.NormalizePath(sourcePath);
        if (normalized.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var rootName = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var marker = rootName + "/game/";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? normalized[(index + rootName.Length + 1)..] : normalized;
    }

    private static string? ExtractLastQuotedValue(string statement)
    {
        var lastQuote = statement.LastIndexOf('"');
        if (lastQuote <= 0)
        {
            return null;
        }

        var firstQuote = statement.LastIndexOf('"', lastQuote - 1);
        return firstQuote >= 0 ? TextUtilities.UnescapeRenPyString(statement[(firstQuote + 1)..lastQuote]) : null;
    }

}
