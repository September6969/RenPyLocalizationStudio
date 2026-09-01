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
public enum SaveFileStatus
{
    Saved,
    Failed,
    Cancelled
}

public sealed record SaveFileCommit(
    string RelativePath,
    SaveFileStatus Status,
    string? Sha256,
    bool BackupCreated,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record SaveSummary(IReadOnlyList<SaveFileCommit> Files)
{
    public int SavedFiles => Files.Count(file => file.Status == SaveFileStatus.Saved);
}
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

        var commits = new List<SaveFileCommit>();
        foreach (var document in snapshot.TlDocuments)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "SAVE_CANCELLED", "保存已取消；已完成文件的提交结果仍然有效。"));
                return new OperationResult<SaveSummary>(OperationStatus.Cancelled, new SaveSummary(commits), diagnostics);
            }
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
                commits.Add(new SaveFileCommit(document.RelativePath, SaveFileStatus.Failed, null, false, targetResult.Diagnostics));
                continue;
            }

            var baselineText = string.IsNullOrEmpty(document.BaselineText) ? document.File.Text : document.BaselineText;
            var expectedSha256 = string.IsNullOrEmpty(document.BaselineSha256) ? document.File.Sha256 : document.BaselineSha256;
            if (!TryRemoveManagedSections(baselineText, out var cleanText))
            {
                var markerDiagnostic = new Diagnostic(
                    DiagnosticSeverity.Error,
                    "RFT_FLOW_MARKER_UNBALANCED",
                    "受管流程注释的 BEGIN/END 标记不配对，已拒绝保存以避免截断文件。",
                    document.RelativePath,
                    Category: DiagnosticCategory.Parsing);
                diagnostics.Add(markerDiagnostic);
                commits.Add(new SaveFileCommit(document.RelativePath, SaveFileStatus.Failed, null, false, [markerDiagnostic]));
                continue;
            }
            var cleanFile = CloneWithText(document.File, cleanText);
            var reparsed = _tlParser.Parse(cleanFile, document.RelativePath, snapshot.Language);
            var savedStates = document.Units.ToDictionary(
                unit => unit,
                unit => new SavedUnitState(unit.TranslationText, unit.RawBodyText, unit.IsDirty));
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
                    request.ForceOverwrite ? null : expectedSha256),
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                var fileDiagnostics = write.Diagnostics.Select(diagnostic => diagnostic.Code == "EXTERNAL_MODIFICATION"
                    ? diagnostic with { Code = "EXTERNAL_FILE_CHANGE" }
                    : diagnostic).ToArray();
                diagnostics.AddRange(fileDiagnostics);
                commits.Add(new SaveFileCommit(
                    document.RelativePath,
                    write.Status == OperationStatus.Cancelled ? SaveFileStatus.Cancelled : SaveFileStatus.Failed,
                    null,
                    false,
                    fileDiagnostics));
                if (write.Status == OperationStatus.Cancelled)
                {
                    return new OperationResult<SaveSummary>(OperationStatus.Cancelled, new SaveSummary(commits), diagnostics);
                }
                continue;
            }
            document.BaselineText = editedText;
            document.BaselineSha256 = write.Value!.Sha256;
            foreach (var (unit, savedState) in savedStates)
            {
                // I/O 等待期间仍允许编辑；只有与本次写入快照完全一致的条目才可清除脏标记。
                if (savedState.WasDirty &&
                    unit.TranslationText == savedState.TranslationText &&
                    unit.RawBodyText == savedState.RawBodyText)
                {
                    unit.IsDirty = false;
                }
            }
            commits.Add(new SaveFileCommit(document.RelativePath, SaveFileStatus.Saved, write.Value.Sha256, write.Value.BackupCreated, []));
        }

        var status = diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)
            ? OperationStatus.Failed
            : diagnostics.Any(x => x.Severity == DiagnosticSeverity.Warning)
                ? OperationStatus.SucceededWithWarnings
                : OperationStatus.Succeeded;
        var summary = new SaveSummary(commits);
        progress.Report(ToolOperationProgress.Create(ToolOperationStage.Completed, $"已保存 {summary.SavedFiles} 个文件", summary.SavedFiles, commits.Count));
        return new OperationResult<SaveSummary>(status, summary, diagnostics);
    }

    public IReadOnlyList<Diagnostic> Validate(ProjectSnapshot snapshot)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var group in snapshot.TranslationUnits
                     .Where(unit => unit.Kind == TranslationUnitKind.Dialogue && !string.IsNullOrWhiteSpace(unit.Identifier))
                     .GroupBy(unit => unit.Identifier!, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            diagnostics.AddRange(group.Select(unit => new Diagnostic(
                DiagnosticSeverity.Error,
                "DUPLICATE_DIALOGUE_ID",
                $"dialogue 翻译 ID {group.Key} 存在重复定义，无法安全保存。",
                unit.RelativeTlPath,
                unit.HeaderLine,
                DiagnosticCategory.Parsing)));
        }
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
                var occurrence = original.Units
                    .Where(unit => unit.Kind == TranslationUnitKind.Dialogue && unit.Identifier == dirty.Identifier)
                    .TakeWhile(unit => unit != dirty)
                    .Count();
                target = reparsed.Units
                    .Where(unit => unit.Kind == TranslationUnitKind.Dialogue && unit.Identifier == dirty.Identifier)
                    .Skip(occurrence)
                    .FirstOrDefault();
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

    private static bool TryRemoveManagedSections(string text, out string cleanedText)
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

            if (!inManaged && lexicalState.IsNeutral && trimmed == EndMarker)
            {
                cleanedText = text;
                return false;
            }

            if (inManaged)
            {
                if (lexicalState.IsNeutral && trimmed == BeginMarker)
                {
                    cleanedText = text;
                    return false;
                }
                if (lexicalState.IsNeutral && trimmed == EndMarker)
                {
                    inManaged = false;
                }

                continue;
            }

            builder.Append(text, line.Start, line.Length);
            lexicalState.Consume(line.Content);
        }

        if (inManaged)
        {
            cleanedText = text;
            return false;
        }

        cleanedText = builder.ToString();
        return true;
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
        => statement is null ? null : TextUtilities.ExtractLastQuotedString(statement);

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
    private sealed record SavedUnitState(string TranslationText, string RawBodyText, bool WasDirty);

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
