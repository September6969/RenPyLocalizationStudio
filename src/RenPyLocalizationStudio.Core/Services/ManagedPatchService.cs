using System.Text;
using System.Text.RegularExpressions;

namespace RenPyLocalizationStudio.Core.Services;

public enum PatchModuleKind { DefaultLanguage, PreferencesLanguage, FontOverride, ReplacementRules, CustomCode, ExtraStrings }
public sealed record PatchModule(PatchModuleKind Kind, string Content, bool Enabled = true);
public sealed record ManagedPatchRequest(
    ProjectRoot Project,
    string RelativePath,
    IReadOnlyList<PatchModule> Modules,
    string? ExpectedSha256 = null,
    bool RequireTargetMissing = false);
public sealed record ManagedPatchPreview(
    string RelativePath,
    string OriginalText,
    string UpdatedText,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool HasBom,
    string NewLine,
    string? OriginalSha256 = null,
    bool TargetExists = false);
public sealed record ManagedPatchWriteRequest(ManagedPatchRequest Patch, bool Confirmed);
public sealed record ManagedPatchWriteSummary(string RelativePath, string Sha256, bool BackupCreated);

public interface IManagedPatchService
{
    Task<OperationResult<ManagedPatchPreview>> PreviewAsync(ManagedPatchRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken);
    Task<OperationResult<ManagedPatchWriteSummary>> ExecuteAsync(ManagedPatchWriteRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken);
    PatchModule CreateDefaultLanguageModule(string language);
    PatchModule CreatePreferencesLanguageModule(string language, string nativeName);
    PatchModule CreateFontModule(string language, string fontPath);
    PatchModule CreateCustomCodeModule(string code);
    PatchModule CreateExtraStringsModule(string language, IEnumerable<(string Old, string New)> entries);
}

public sealed class ManagedPatchService : IManagedPatchService
{
    private readonly IFileSystemService _fileSystem;
    public ManagedPatchService(IFileSystemService fileSystem) => _fileSystem = fileSystem;

    public async Task<OperationResult<ManagedPatchPreview>> PreviewAsync(ManagedPatchRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        var targetResult = _fileSystem.ValidateProjectPath(request.Project, request.RelativePath);
        if (!targetResult.IsSuccess || targetResult.Value is null) return new(OperationStatus.Failed, null, targetResult.Diagnostics);
        var original = string.Empty;
        var newLine = Environment.NewLine;
        var hasBom = false;
        string? originalSha256 = null;
        var exists = await _fileSystem.ExistsAsync(targetResult.Value, cancellationToken).ConfigureAwait(false);
        if (!exists.IsSuccess) return new(exists.Status, null, exists.Diagnostics);
        var targetExists = exists.Value;
        if (targetExists)
        {
            var read = await _fileSystem.ReadUtf8Async(targetResult.Value, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess || read.Value is null) return new(read.Status, null, read.Diagnostics);
            original = read.Value.Text;
            newLine = read.Value.NewLine;
            hasBom = read.Value.HasBom;
            originalSha256 = read.Value.Sha256;
        }
        var diagnostics = new List<Diagnostic>();
        var updated = original;
        diagnostics.AddRange(ValidateManagedMarkers(original, request.RelativePath));
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            var invalidPreview = new ManagedPatchPreview(request.RelativePath, original, original, diagnostics, hasBom, newLine, originalSha256, targetExists);
            return new OperationResult<ManagedPatchPreview>(OperationStatus.Failed, invalidPreview, diagnostics);
        }
        foreach (var module in request.Modules)
        {
            if (cancellationToken.IsCancellationRequested)
                return OperationResult<ManagedPatchPreview>.Cancelled(
                    new Diagnostic(DiagnosticSeverity.Info, "PATCH_PREVIEW_CANCELLED", "补丁预览已取消。", request.RelativePath, Category: DiagnosticCategory.Patch));
            var name = module.Kind.ToString().ToUpperInvariant();
            var begin = $"# RLS-ZZZ-BEGIN {name}";
            var end = $"# RLS-ZZZ-END {name}";
            var block = begin + newLine + NormalizeNewLines(module.Content, newLine).TrimEnd('\r', '\n') + newLine + end;
            var pattern = $"(?m)^[ \\t]*{Regex.Escape(begin)}[ \\t]*\\r?$.*?^[ \\t]*{Regex.Escape(end)}[ \\t]*\\r?$";
            var matches = Regex.Matches(updated, pattern, RegexOptions.Singleline);
            if (matches.Count > 1)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "PATCH_DUPLICATE_MANAGED_BLOCK", $"受管区块 {name} 出现多次。", request.RelativePath, Category: DiagnosticCategory.Patch));
                continue;
            }
            if (!module.Enabled)
            {
                // 向导取消某个模块时只删除该模块的受管区块，标记外内容保持不变。
                if (matches.Count == 1) updated = Regex.Replace(updated, pattern, string.Empty, RegexOptions.Singleline);
                continue;
            }
            updated = matches.Count == 1 ? Regex.Replace(updated, pattern, _ => block, RegexOptions.Singleline)
                : AppendBlock(updated, block, newLine);
        }
        progress.Report(ToolOperationProgress.Create(ToolOperationStage.Planning, "补丁差异已生成", relativePath: request.RelativePath));
        var preview = new ManagedPatchPreview(request.RelativePath, original, updated, diagnostics, hasBom, newLine, originalSha256, targetExists);
        return new(diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? OperationStatus.Failed : OperationStatus.Succeeded, preview, diagnostics);
    }

    public async Task<OperationResult<ManagedPatchWriteSummary>> ExecuteAsync(ManagedPatchWriteRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!request.Confirmed) return OperationResult<ManagedPatchWriteSummary>.Failure(new Diagnostic(DiagnosticSeverity.Error, "PATCH_CONFIRMATION_REQUIRED", "写入补丁前必须确认差异。", request.Patch.RelativePath, Category: DiagnosticCategory.Patch));
        var preview = await PreviewAsync(request.Patch, progress, cancellationToken).ConfigureAwait(false);
        if (!preview.IsSuccess || preview.Value is null) return new(preview.Status, null, preview.Diagnostics);
        var target = _fileSystem.ValidateProjectPath(request.Patch.Project, request.Patch.RelativePath);
        if (!target.IsSuccess || target.Value is null) return new(OperationStatus.Failed, null, target.Diagnostics);
        var payload = new UTF8Encoding(false, true).GetBytes(preview.Value.UpdatedText);
        var bytes = preview.Value.HasBom ? Encoding.UTF8.GetPreamble().Concat(payload).ToArray() : payload;
        var expectedSha256 = request.Patch.ExpectedSha256 ?? preview.Value.OriginalSha256;
        var requireTargetMissing = request.Patch.RequireTargetMissing ||
                                    (!preview.Value.TargetExists && request.Patch.ExpectedSha256 is null);
        var write = await _fileSystem.AtomicWriteAsync(new AtomicWriteRequest(
            target.Value,
            bytes,
            expectedSha256,
            RequireTargetMissing: requireTargetMissing), progress, cancellationToken).ConfigureAwait(false);
        return write.Value is null ? new(write.Status, null, write.Diagnostics) : new(write.Status, new ManagedPatchWriteSummary(write.Value.RelativePath, write.Value.Sha256, write.Value.BackupCreated), write.Diagnostics);
    }

    public PatchModule CreateDefaultLanguageModule(string language) => new(PatchModuleKind.DefaultLanguage, $"define config.default_language = \"{TextUtilities.EscapeRenPyString(language)}\"");
    public PatchModule CreatePreferencesLanguageModule(string language, string nativeName) => new(PatchModuleKind.PreferencesLanguage,
        $"screen rls_language_selector():\n    hbox:\n        spacing 12\n        textbutton \"English\" action Language(None)\n        textbutton \"{TextUtilities.EscapeRenPyString(nativeName)}\" action Language(\"{TextUtilities.EscapeRenPyString(language)}\")");
    public PatchModule CreateFontModule(string language, string fontPath) => new(PatchModuleKind.FontOverride,
        $"translate {language} python:\n    gui.text_font = \"{TextUtilities.EscapeRenPyString(fontPath)}\"\n    gui.interface_text_font = \"{TextUtilities.EscapeRenPyString(fontPath)}\"\n    gui.name_text_font = \"{TextUtilities.EscapeRenPyString(fontPath)}\"");
    public PatchModule CreateCustomCodeModule(string code) => new(PatchModuleKind.CustomCode, code);
    public PatchModule CreateExtraStringsModule(string language, IEnumerable<(string Old, string New)> entries)
    {
        var builder = new StringBuilder($"translate {language} strings:\n");
        foreach (var entry in entries.DistinctBy(x => x.Old, StringComparer.Ordinal))
            builder.Append("\n    old \"").Append(TextUtilities.EscapeRenPyString(entry.Old)).Append("\"\n    new \"").Append(TextUtilities.EscapeRenPyString(entry.New)).Append("\"\n");
        return new PatchModule(PatchModuleKind.ExtraStrings, builder.ToString().TrimEnd());
    }
    private static string AppendBlock(string text, string block, string newLine) => text.Length == 0 ? block + newLine : text.TrimEnd('\r', '\n') + newLine + newLine + block + (text.EndsWith('\n') ? newLine : string.Empty);
    private static string NormalizeNewLines(string text, string newLine) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal);

    private static IReadOnlyList<Diagnostic> ValidateManagedMarkers(string text, string relativePath)
    {
        var diagnostics = new List<Diagnostic>();
        var stack = new Stack<(string Name, int Line)>();
        var marker = new Regex("^[ \\t]*# RLS-ZZZ-(?<kind>BEGIN|END) (?<name>[A-Z_]+)[ \\t]*$", RegexOptions.CultureInvariant);
        var lines = TextUtilities.SliceLines(text);
        foreach (var line in lines)
        {
            var match = marker.Match(line.Content.TrimEnd('\r', '\n'));
            if (!match.Success) continue;
            var kind = match.Groups["kind"].Value;
            var name = match.Groups["name"].Value;
            if (kind == "BEGIN")
            {
                if (stack.Count > 0)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "PATCH_NESTED_MANAGED_MARKER",
                        $"受管区块 {name} 嵌套在 {stack.Peek().Name} 内，无法安全更新。", relativePath, line.Number, DiagnosticCategory.Patch));
                }
                stack.Push((name, line.Number));
                continue;
            }

            if (stack.Count == 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "PATCH_ORPHAN_END_MARKER",
                    $"发现没有 BEGIN 的 END 标记：{name}。", relativePath, line.Number, DiagnosticCategory.Patch));
                continue;
            }
            var opened = stack.Pop();
            if (!opened.Name.Equals(name, StringComparison.Ordinal))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "PATCH_MISMATCHED_MARKER",
                    $"受管区块标记不匹配：BEGIN {opened.Name}，END {name}。", relativePath, line.Number, DiagnosticCategory.Patch));
            }
        }
        foreach (var opened in stack)
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "PATCH_ORPHAN_BEGIN_MARKER",
                $"受管区块 {opened.Name} 缺少 END 标记。", relativePath, opened.Line, DiagnosticCategory.Patch));
        return diagnostics;
    }
}
