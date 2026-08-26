using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed record NavigationItem(string Name, string Location, string NodeId);

public static class TranslationNavigationBuilder
{
    public static IReadOnlyList<NavigationItem> Build(ProjectSnapshot snapshot, FlowGroupingMode mode)
    {
        if (mode == FlowGroupingMode.SourceFile)
            return FlowProjectionService.ProjectGroups(snapshot, mode)
                .Select(group => new NavigationItem(
                    Path.GetFileName(group.Title),
                    $"{group.Title} · {group.Rows.Count:N0} 个节点",
                    $"group:{mode}:{group.Key}"))
                .ToArray();

        if (mode == FlowGroupingMode.Label)
            return FlowProjectionService.ProjectGroups(snapshot, mode)
                .Select(group =>
                {
                    var label = snapshot.Graph.Labels.GetValueOrDefault(group.Key);
                    var location = label is null ? $"{group.Rows.Count:N0} 个节点" : $"{label.Region.RelativePath}:{label.Region.StartLine} · {group.Rows.Count:N0} 个节点";
                    return new NavigationItem(group.Title, location, $"group:{mode}:{group.Key}");
                })
                .ToArray();

        var incomingLabels = snapshot.Graph.Nodes
            .Where(node => node.Kind is FlowNodeKind.Jump or FlowNodeKind.Call && !string.IsNullOrWhiteSpace(node.Target))
            .Select(node => node.Target!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = snapshot.Graph.Labels.Values
            .Where(label => label.LabelName is not null && !incomingLabels.Contains(label.LabelName))
            .OrderBy(label => label.Region.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(label => label.Region.StartLine)
            .ToArray();
        if (entries.Length == 0)
            entries = snapshot.Graph.Labels.Values.OrderBy(label => label.Region.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(label => label.Region.StartLine).ToArray();
        return entries.Select(label => new NavigationItem(label.LabelName ?? label.DisplayText,
            $"剧情入口 · {label.Region.RelativePath}:{label.Region.StartLine}", label.Id)).ToArray();
    }
}

public enum ContentPresentationKind { Dialogue, Label, Choice, Transfer, Structure, GroupHeader, String, Translation, Diagnostic, ExtraText }
public enum TranslationStatusKind { None, Bound, SharedImpact, Pending, Unbound, Conflict, Missing, Dynamic, Information, Warning, Error }
public sealed record TranslationCoverage(int TranslatedCount, int EditableCount, double Percentage)
{
    public bool HasCoverage => EditableCount > 0;
}

public static class TranslationCoverageCalculator
{
    public static TranslationCoverage Calculate(IEnumerable<ContentItem> items)
    {
        var editable = items.Where(x => x.IsEditable).ToArray();
        var translated = editable.Count(x => x.IsTranslationComplete);
        return new TranslationCoverage(translated, editable.Length, editable.Length == 0 ? 0 : translated * 100d / editable.Length);
    }
}

public sealed class ContentItem : ObservableObject
{
    private string _subtitle = string.Empty;
    private string _badge = string.Empty;
    private TranslationStatusKind _statusKind;
    private bool _isStatusVisible;

    public required string Key { get; init; }
    public required string Title { get; init; }
    public string SpeakerText { get; init; } = string.Empty;
    public string SpeakerPrefix => string.IsNullOrWhiteSpace(SpeakerText) ? string.Empty : SpeakerText + ": ";
    public required string BodyText { get; init; }
    public required ContentPresentationKind PresentationKind { get; init; }
    public string Subtitle { get => _subtitle; set => SetProperty(ref _subtitle, value); }
    public string Badge { get => _badge; set => SetProperty(ref _badge, value); }
    public TranslationStatusKind StatusKind { get => _statusKind; private set => SetProperty(ref _statusKind, value); }
    public bool IsStatusVisible { get => _isStatusVisible; private set => SetProperty(ref _isStatusVisible, value); }
    public required Brush Accent { get; init; }
    public required Thickness IndentMargin { get; init; }
    public required string SearchText { get; init; }
    public TranslationUnit? Unit { get; init; }
    public SharedStringEntry? SharedString { get; init; }
    public FlowNode? Node { get; init; }
    public ExtraTextCandidate? ExtraText { get; init; }
    public Diagnostic? Diagnostic { get; init; }
    public bool IsEditable => SharedString is not null || Unit is not null;
    public bool IsLabel => PresentationKind == ContentPresentationKind.Label;
    public bool IsDialogue => PresentationKind == ContentPresentationKind.Dialogue;
    public bool IsGroupHeader => PresentationKind == ContentPresentationKind.GroupHeader;
    public bool IsTranslationComplete => SharedString is not null
        ? !SharedString.HasConflict && !string.IsNullOrWhiteSpace(SharedString.Translation)
        : Unit is not null && !string.IsNullOrWhiteSpace(Unit.IsRawMode ? Unit.RawBodyText : Unit.TranslationText);

    public static ContentItem FromFlow(FlowNode node, TranslationUnit? unit, SharedStringEntry? sharedString, int depth) => new ContentItem()
    {
        Key = node.Id,
        Title = node.DisplayText,
        SpeakerText = node.Speaker ?? string.Empty,
        BodyText = node.Kind == FlowNodeKind.Dialogue ? node.OriginalText ?? node.DisplayText : node.DisplayText,
        PresentationKind = Presentation(node.Kind),
        Subtitle = $"{node.Region.RelativePath}:{node.Region.StartLine}",
        Accent = Brush(node.Kind switch
        {
            FlowNodeKind.Label => Color.FromRgb(126, 105, 230),
            FlowNodeKind.Choice => Color.FromRgb(224, 143, 68),
            FlowNodeKind.Jump or FlowNodeKind.Call => Color.FromRgb(54, 150, 151),
            FlowNodeKind.Unresolved => Color.FromRgb(248, 81, 73),
            _ => Color.FromRgb(139, 148, 158)
        }),
        IndentMargin = new Thickness(Math.Min(depth, 8) * 14, 0, 0, 0),
        SearchText = $"{node.DisplayText} {node.Region.RelativePath} {sharedString?.Translation ?? unit?.TranslationText}",
        Unit = unit,
        SharedString = sharedString,
        Node = node
    }.WithCurrentStatus();

    public static ContentItem FromGroupHeader(string key, string title, FlowGroupingMode mode) => new ContentItem()
    {
        Key = $"group:{mode}:{key}",
        Title = title,
        BodyText = title,
        PresentationKind = ContentPresentationKind.GroupHeader,
        Subtitle = mode == FlowGroupingMode.SourceFile ? "源文件" : "Label 分组",
        Accent = Brush(Color.FromRgb(209, 107, 165)),
        IndentMargin = new Thickness(0),
        SearchText = title
    }.WithCurrentStatus();

    public static ContentItem FromSharedString(SharedStringEntry entry) => new ContentItem()
    {
        Key = "string:" + entry.OldText,
        Title = entry.OldText,
        BodyText = entry.OldText,
        PresentationKind = ContentPresentationKind.String,
        Subtitle = entry.Translation,
        Accent = Brush(entry.HasConflict ? Color.FromRgb(248, 81, 73) : Color.FromRgb(126, 105, 230)),
        IndentMargin = new Thickness(0),
        SearchText = $"{entry.OldText} {entry.Translation} {string.Join(' ', entry.References.Select(x => x.SourcePath))}",
        SharedString = entry
    }.WithCurrentStatus();

    public static ContentItem FromTranslation(TranslationUnit unit) => new ContentItem()
    {
        Key = $"unbound:{unit.RelativeTlPath}:{unit.HeaderLine}",
        Title = unit.Identifier ?? unit.OldText ?? "未知翻译条目",
        BodyText = unit.Identifier ?? unit.OldText ?? "未知翻译条目",
        PresentationKind = ContentPresentationKind.Translation,
        Subtitle = $"{unit.RelativeTlPath}:{unit.HeaderLine}",
        Accent = Brush(Color.FromRgb(224, 143, 68)),
        IndentMargin = new Thickness(0),
        SearchText = $"{unit.Identifier} {unit.OldText} {unit.TranslationText} {unit.RelativeTlPath}",
        Unit = unit
    }.WithCurrentStatus();

    public static ContentItem FromDiagnostic(Diagnostic diagnostic) => new ContentItem()
    {
        Key = $"diagnostic:{diagnostic.Code}:{diagnostic.RelativePath}:{diagnostic.Line}:{diagnostic.Message}",
        Title = diagnostic.Message,
        BodyText = diagnostic.Message,
        PresentationKind = ContentPresentationKind.Diagnostic,
        Subtitle = $"{diagnostic.Code}  {diagnostic.RelativePath}:{diagnostic.Line}",
        Badge = diagnostic.Severity switch { DiagnosticSeverity.Error => "错误", DiagnosticSeverity.Warning => "警告", _ => "信息" },
        Accent = Brush(diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => Color.FromRgb(248, 81, 73),
            DiagnosticSeverity.Warning => Color.FromRgb(210, 153, 34),
            _ => Color.FromRgb(88, 166, 255)
        }),
        IndentMargin = new Thickness(0),
        SearchText = $"{diagnostic.Code} {diagnostic.Message} {diagnostic.RelativePath}",
        Diagnostic = diagnostic
    }.WithCurrentStatus();

    public static ContentItem FromExtraText(ExtraTextCandidate candidate) => new ContentItem()
    {
        Key = candidate.Id,
        Title = candidate.Text,
        BodyText = candidate.Text,
        PresentationKind = ContentPresentationKind.ExtraText,
        Subtitle = $"{candidate.Kind} · {candidate.RelativePath}:{candidate.Line} · {candidate.Reason}",
        Badge = candidate.AlreadyTranslated ? "已存在" : candidate.Confidence switch { CandidateConfidence.High => "高置信", CandidateConfidence.Medium => "待确认", _ => "低置信" },
        Accent = Brush(candidate.AlreadyTranslated ? Color.FromRgb(75, 85, 99) : Color.FromRgb(209, 107, 165)),
        IndentMargin = new Thickness(0),
        SearchText = $"{candidate.Text} {candidate.RelativePath} {candidate.Kind} {candidate.Reason}",
        ExtraText = candidate
    }.WithCurrentStatus();

    public void RefreshStatus()
    {
        var (status, text, visible) = ResolveStatus();
        StatusKind = status;
        Badge = text;
        IsStatusVisible = visible;
        OnPropertyChanged(nameof(IsTranslationComplete));
    }

    private ContentItem WithCurrentStatus()
    {
        RefreshStatus();
        return this;
    }

    private (TranslationStatusKind Kind, string Text, bool Visible) ResolveStatus()
    {
        if (Diagnostic is not null)
            return Diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => (TranslationStatusKind.Error, "错误", true),
                DiagnosticSeverity.Warning => (TranslationStatusKind.Warning, "警告", true),
                _ => (TranslationStatusKind.Information, "信息", true)
            };
        if (ExtraText is not null)
            return (TranslationStatusKind.Information, ExtraText.AlreadyTranslated ? "已存在" : ExtraText.Confidence switch
            {
                CandidateConfidence.High => "高置信",
                CandidateConfidence.Medium => "待确认",
                _ => "低置信"
            }, true);
        if (SharedString is not null)
            return SharedString.HasConflict
                ? (TranslationStatusKind.Conflict, "冲突", true)
                : (TranslationStatusKind.SharedImpact, $"影响 {SharedString.References.Count} 处", true);
        if (Unit is not null)
        {
            if (Unit.MissingNew) return (TranslationStatusKind.Missing, "缺少 new", true);
            if (Unit.IsUnboundFlowTranslation) return (TranslationStatusKind.Unbound, "未绑定", true);
            if (!IsTranslationComplete) return (TranslationStatusKind.Pending, "待翻译", true);
            return (TranslationStatusKind.Bound, string.Empty, false);
        }
        if (Node?.Kind == FlowNodeKind.Unresolved) return (TranslationStatusKind.Dynamic, "动态目标", true);
        return (TranslationStatusKind.None, string.Empty, false);
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static ContentPresentationKind Presentation(FlowNodeKind kind) => kind switch
    {
        FlowNodeKind.Dialogue => ContentPresentationKind.Dialogue,
        FlowNodeKind.Label => ContentPresentationKind.Label,
        FlowNodeKind.Choice => ContentPresentationKind.Choice,
        FlowNodeKind.Jump or FlowNodeKind.Call => ContentPresentationKind.Transfer,
        _ => ContentPresentationKind.Structure
    };
}

public sealed class EditorTarget
{
    private readonly TranslationUnit? _unit;
    private EditorTarget(TranslationUnit? unit, SharedStringEntry? sharedString)
    {
        _unit = unit;
        SharedString = sharedString;
    }

    public SharedStringEntry? SharedString { get; }
    public string Key => SharedString is null ? $"unit:{_unit?.RelativeTlPath}:{_unit?.HeaderLine}" : "shared:" + SharedString.OldText;
    public string Title => SharedString is null
        ? _unit?.IsRawMode == true ? $"原始块模式 · {_unit.Identifier}" : _unit?.Identifier ?? "翻译条目"
        : "共享字符串";
    public string Location => SharedString is null
        ? $"{_unit?.RelativeTlPath}:{_unit?.HeaderLine}"
        : string.Join(Environment.NewLine, SharedString.References.Take(6).Select(x => $"{x.SourceKind} · {x.SourcePath}:{x.SourceLine}"));
    public string Source => SharedString?.OldText ?? _unit?.OriginalStatement ?? _unit?.BoundNode?.OriginalText ?? _unit?.OldText ?? string.Empty;
    public string Translation => SharedString?.Translation ?? (_unit?.IsRawMode == true ? _unit.RawBodyText : _unit?.TranslationText) ?? string.Empty;
    public string Impact => SharedString is not null
        ? $"此译文影响 {SharedString.References.Count} 处；编辑后所有引用会同步。"
        : _unit?.IsRawMode == true ? "复杂翻译块：仅块体可编辑，translate 头保持不变。" : string.Empty;
    public bool HasConflict => SharedString?.HasConflict == true;

    public static EditorTarget ForUnit(TranslationUnit unit) => new(unit, null);
    public static EditorTarget ForSharedString(SharedStringEntry entry) => new(null, entry);

    public void SetTranslation(string value)
    {
        if (SharedString is not null) SharedString.Translation = value;
        else if (_unit is not null)
        {
            if (_unit.IsRawMode) _unit.RawBodyText = value;
            else _unit.TranslationText = value;
            _unit.IsDirty = true;
        }
    }
}

public sealed record TextSelectionRequest(int Start, int Length, long Version);
public sealed record ScrollAnchor(string ItemId, string? ParentGroupId = null, double RelativeOffset = 0, int FallbackIndex = 0);
