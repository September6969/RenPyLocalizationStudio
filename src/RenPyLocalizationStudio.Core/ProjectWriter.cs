using System.Text;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Core;

/// <summary>
/// 翻译文件保存请求。ForceOverwrite 只跳过外部修改哈希比对；翻译结构校验、路径校验和原子备份始终保留。
/// </summary>
public sealed record ProjectSaveRequest(
    ProjectSnapshot Snapshot,
    bool RefreshAnnotations,
    bool AllowWarnings,
    bool ForceOverwrite = false);
public sealed record SaveSummary(int SavedFiles);
public interface IProjectSaveService : IAsyncOperationService<ProjectSaveRequest, SaveSummary>
{
    IReadOnlyList<Diagnostic> Validate(ProjectSnapshot snapshot);
}

public sealed class ProjectWriter : IProjectSaveService
{
    private const string BeginMarker = "# RFT-FLOW-BEGIN";
    private const string EndMarker = "# RFT-FLOW-END";
    private readonly TlParser _tlParser = new();
    private readonly TranslationValidator _validator = new();
    private readonly IFileSystemService _fileSystem;

    public ProjectWriter(IFileSystemService fileSystem) => _fileSystem = fileSystem;

    public async Task<OperationResult<SaveSummary>> ExecuteAsync(
        ProjectSaveRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.Validating, "正在验证翻译文件"));
            return await SaveCoreAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<SaveSummary>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "SAVE_CANCELLED", "保存已取消。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return OperationResult<SaveSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "SAVE_FAILED", ex.Message, Category: DiagnosticCategory.FileSystem));
        }
    }

    private async Task<OperationResult<SaveSummary>> SaveCoreAsync(
        ProjectSaveRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var snapshot = request.Snapshot;
        var diagnostics = Validate(snapshot).ToList();
        if (snapshot.SharedStrings.Any(entry => entry.HasConflict))
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "UNRESOLVED_STRING_CONFLICT", "仍有共享字符串译文冲突，保存已取消。"));
        }

        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
            (!request.AllowWarnings && diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)))
        {
            return new OperationResult<SaveSummary>(OperationStatus.Failed, null, diagnostics);
        }

        var rootResult = _fileSystem.ValidateProjectRoot(snapshot.ProjectRoot);
        if (!rootResult.IsSuccess || rootResult.Value is null)
        {
            return new OperationResult<SaveSummary>(OperationStatus.Failed, null, rootResult.Diagnostics);
        }

        var savedFiles = 0;
        foreach (var document in snapshot.TlDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasDirtyUnits = document.Units.Any(unit => unit.IsDirty);
            if (!hasDirtyUnits && !request.RefreshAnnotations)
            {
                continue;
            }

            var relativeTarget = Path.GetRelativePath(rootResult.Value.FullPath, document.File.FullPath);
            var targetResult = _fileSystem.ValidateProjectPath(rootResult.Value, relativeTarget);
            if (!targetResult.IsSuccess || targetResult.Value is null)
            {
                diagnostics.AddRange(targetResult.Diagnostics);
                continue;
            }

            var cleanText = RemoveManagedSections(document.File.Text);
            var cleanFile = CloneWithText(document.File, cleanText);
            var reparsed = _tlParser.Parse(cleanFile, document.RelativePath, snapshot.Language);
            var editedText = ApplyTranslations(cleanText, document, reparsed, document.File.NewLine);
            if (request.RefreshAnnotations)
            {
                var annotatedFile = CloneWithText(document.File, editedText);
                var annotatedDocument = _tlParser.Parse(annotatedFile, document.RelativePath, snapshot.Language);
                editedText = ApplyAnnotations(editedText, annotatedDocument, snapshot, document.File.NewLine);
            }

            var validationFile = CloneWithText(document.File, editedText);
            _tlParser.Parse(validationFile, document.RelativePath, snapshot.Language);
            var write = await _fileSystem.AtomicWriteAsync(
                new AtomicWriteRequest(
                    targetResult.Value,
                    document.File.Encode(editedText),
                    request.ForceOverwrite ? null : document.File.Sha256),
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                diagnostics.AddRange(write.Diagnostics.Select(diagnostic => diagnostic.Code == "EXTERNAL_MODIFICATION"
                    ? diagnostic with { Code = "EXTERNAL_FILE_CHANGE" }
                    : diagnostic));
                continue;
            }
            savedFiles++;
        }

        var status = diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)
            ? OperationStatus.Failed
            : diagnostics.Any(x => x.Severity == DiagnosticSeverity.Warning)
                ? OperationStatus.SucceededWithWarnings
                : OperationStatus.Succeeded;
        progress.Report(ToolOperationProgress.Create(ToolOperationStage.Completed, $"已保存 {savedFiles} 个文件", savedFiles, savedFiles));
        return new OperationResult<SaveSummary>(status, new SaveSummary(savedFiles), diagnostics);
    }

    public IReadOnlyList<Diagnostic> Validate(ProjectSnapshot snapshot)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var unit in snapshot.TranslationUnits.Where(unit => unit.IsDirty))
        {
            if (unit.IsRawMode)
            {
                if (!ValidateRawBody(unit.RawBodyText))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "RAW_BLOCK_STRUCTURE_INVALID",
                        $"原始块 {unit.Identifier} 含有未缩进内容或新的 translate 声明。",
                        unit.RelativeTlPath,
                        unit.HeaderLine));
                }

                continue;
            }

            var source = unit.Kind == TranslationUnitKind.String
                ? unit.OldText
                : ExtractSourceText(unit.OriginalStatement) ?? unit.BoundNode?.OriginalText;
            if (source is not null)
            {
                diagnostics.AddRange(_validator.Validate(source, unit.TranslationText, unit.RelativeTlPath, unit.HeaderLine));
            }
        }

        return diagnostics;
    }

    private static string ApplyTranslations(string text, TlDocument original, TlDocument reparsed, string newLine)
    {
        var edits = new List<TextEdit>();
        foreach (var dirty in original.Units.Where(unit => unit.IsDirty))
        {
            TranslationUnit? target;
            if (dirty.Kind == TranslationUnitKind.Dialogue)
            {
                target = reparsed.Units.FirstOrDefault(unit => unit.Kind == TranslationUnitKind.Dialogue && unit.Identifier == dirty.Identifier);
            }
            else
            {
                var occurrence = original.Units.Where(unit => unit.Kind == TranslationUnitKind.String && unit.OldText == dirty.OldText).TakeWhile(unit => unit != dirty).Count();
                target = reparsed.Units.Where(unit => unit.Kind == TranslationUnitKind.String && unit.OldText == dirty.OldText).Skip(occurrence).FirstOrDefault();
            }

            if (target is null)
            {
                throw new InvalidDataException($"保存时无法重新定位翻译条目：{dirty.Identifier ?? dirty.OldText}");
            }

            var escaped = TextUtilities.EscapeRenPyString(dirty.TranslationText);
            if (target.TranslationValueSpan is not null)
            {
                edits.Add(new TextEdit(target.TranslationValueSpan.Start, target.TranslationValueSpan.Length, escaped));
            }
            else if (target.Kind == TranslationUnitKind.String && target.OldLineSpan is not null)
            {
                var oldLineText = text.Substring(target.OldLineSpan.Start, target.OldLineSpan.Length);
                var indent = oldLineText[..(oldLineText.Length - oldLineText.TrimStart(' ', '\t').Length)];
                edits.Add(new TextEdit(target.OldLineSpan.End, 0, indent + "new \"" + escaped + "\"" + newLine));
            }
            else if (dirty.IsRawMode && target.RawBodySpan is not null)
            {
                edits.Add(new TextEdit(target.RawBodySpan.Start, target.RawBodySpan.Length, dirty.RawBodyText));
            }
            else
            {
                throw new InvalidDataException($"翻译块不支持结构化写回：{dirty.Identifier}");
            }
        }

        return ApplyEdits(text, edits);
    }

    private static string ApplyAnnotations(string text, TlDocument document, ProjectSnapshot snapshot, string newLine)
    {
        var edits = new List<TextEdit>();
        var sourcePaths = document.Units.Where(unit => unit.SourcePath is not null).Select(unit => TextUtilities.NormalizePath(unit.SourcePath!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodes = snapshot.Graph.Nodes.Where(node => sourcePaths.Contains(node.Region.RelativePath) && IsBoundary(node.Kind)).ToList();
        var insertions = new Dictionary<int, List<string>>();

        foreach (var node in nodes)
        {
            var sameSourceUnits = document.Units
                .Where(unit => unit.SourcePath is not null && string.Equals(TextUtilities.NormalizePath(unit.SourcePath), node.Region.RelativePath, StringComparison.OrdinalIgnoreCase) && unit.SourceLine is not null)
                .ToList();
            TranslationUnit? anchor = node.Kind is FlowNodeKind.Jump or FlowNodeKind.Call or FlowNodeKind.Return or FlowNodeKind.Unresolved
                ? sameSourceUnits.Where(unit => unit.SourceLine <= node.Region.StartLine).OrderByDescending(unit => unit.SourceLine).FirstOrDefault()
                : sameSourceUnits.Where(unit => unit.SourceLine >= node.Region.StartLine).OrderBy(unit => unit.SourceLine).FirstOrDefault();
            var offset = anchor is null
                ? text.Length
                : node.Kind is FlowNodeKind.Jump or FlowNodeKind.Call or FlowNodeKind.Return or FlowNodeKind.Unresolved
                    ? anchor.BlockSpan.End
                    : anchor.BlockSpan.Start;
            if (!insertions.TryGetValue(offset, out var annotations))
            {
                annotations = [];
                insertions[offset] = annotations;
            }

            annotations.Add(FormatAnnotation(node, newLine));
        }

        foreach (var (offset, annotations) in insertions)
        {
            var unique = annotations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var replacement = string.Join(string.Empty, unique);
            if (offset == text.Length && text.Length > 0 && !text.EndsWith('\n'))
            {
                replacement = newLine + replacement;
            }

            edits.Add(new TextEdit(offset, 0, replacement));
        }

        return ApplyEdits(text, edits);
    }

    private static string FormatAnnotation(FlowNode node, string newLine)
    {
        var detail = node.Kind switch
        {
            FlowNodeKind.Label => $"label：{node.LabelName}",
            FlowNodeKind.Choice => node.Condition is null ? node.DisplayText : $"{node.DisplayText}；条件：{node.Condition}",
            FlowNodeKind.Condition => $"条件：{node.Condition}",
            FlowNodeKind.Jump => $"跳转：jump → {node.Target}",
            FlowNodeKind.Call => $"调用：call → {node.Target}",
            FlowNodeKind.Return => "返回：return",
            FlowNodeKind.Unresolved => $"动态流程：{node.DisplayText}",
            _ => node.DisplayText
        };
        return BeginMarker + newLine + $"# [流程] {node.Region.RelativePath}:{node.Region.StartLine} {detail}" + newLine + EndMarker + newLine;
    }

    private static string RemoveManagedSections(string text)
    {
        var lines = TextUtilities.SliceLines(text);
        var builder = new StringBuilder(text.Length);
        var inManaged = false;
        var lexicalState = new RenPyStringState();
        foreach (var line in lines)
        {
            var trimmed = line.Content.Trim();
            if (!inManaged && lexicalState.IsNeutral && trimmed == BeginMarker)
            {
                inManaged = true;
                continue;
            }

            if (inManaged)
            {
                if (lexicalState.IsNeutral && trimmed == EndMarker)
                {
                    inManaged = false;
                }

                continue;
            }

            builder.Append(text, line.Start, line.Length);
            lexicalState.Consume(line.Content);
        }

        return builder.ToString();
    }

    private static bool IsBoundary(FlowNodeKind kind)
        => kind is FlowNodeKind.Label or FlowNodeKind.Choice or FlowNodeKind.Condition or FlowNodeKind.Jump or FlowNodeKind.Call or FlowNodeKind.Return or FlowNodeKind.Unresolved;

    private static string ApplyEdits(string text, IEnumerable<TextEdit> edits)
    {
        var builder = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            builder.Remove(edit.Start, edit.Length);
            builder.Insert(edit.Start, edit.Replacement);
        }

        return builder.ToString();
    }

    private static Utf8TextFile CloneWithText(Utf8TextFile original, string text) => new()
    {
        FullPath = original.FullPath,
        Text = text,
        HasBom = original.HasBom,
        NewLine = original.NewLine,
        HasFinalNewLine = original.HasFinalNewLine,
        Sha256 = original.Sha256
    };

    private static string? ExtractSourceText(string? statement)
    {
        if (statement is null)
        {
            return null;
        }

        var last = statement.LastIndexOf('"');
        var first = last > 0 ? statement.LastIndexOf('"', last - 1) : -1;
        return first >= 0 ? TextUtilities.UnescapeRenPyString(statement[(first + 1)..last]) : null;
    }

    private static bool ValidateRawBody(string body)
    {
        var lexicalState = new RenPyStringState();
        foreach (var line in TextUtilities.SliceLines(body))
        {
            var trimmed = line.Content.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (lexicalState.IsNeutral && (trimmed.StartsWith("translate ", StringComparison.Ordinal) || TextUtilities.GetIndent(line.Content) == 0))
            {
                return false;
            }

            lexicalState.Consume(line.Content);
        }

        return lexicalState.IsNeutral;
    }

    private sealed record TextEdit(int Start, int Length, string Replacement);

    private sealed class RenPyStringState
    {
        private char? _quote;
        private bool _triple;
        private bool _escaped;

        public bool IsNeutral => _quote is null;

        public void Consume(string line)
        {
            for (var index = 0; index < line.Length; index++)
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
                    break;
                }

                if (character is '\'' or '"')
                {
                    _quote = character;
                    _triple = index + 2 < line.Length && line[index + 1] == character && line[index + 2] == character;
                    if (_triple)
                    {
                        index += 2;
                    }
                }
            }

            if (!_triple)
            {
                _quote = null;
                _escaped = false;
            }
        }
    }
}
