using System.Collections.ObjectModel;

namespace RenPyLocalizationStudio.Core;

public enum FlowNodeKind
{
    Label,
    Menu,
    Choice,
    Condition,
    Dialogue,
    Jump,
    Call,
    Return,
    EndOfFile,
    Unresolved
}

public enum FlowEdgeKind
{
    Sequence,
    Choice,
    Condition,
    Jump,
    Call,
    Contains,
    End
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum DiagnosticCategory
{
    General,
    FileSystem,
    Encoding,
    Process,
    Sdk,
    Parsing,
    Translation,
    Security,
    Patch
}

public enum FlowGroupingMode
{
    StoryPath,
    SourceFile,
    Label
}

public enum TranslationUnitKind
{
    Dialogue,
    String
}

public enum StringSourceKind
{
    Menu,
    Screen,
    Other,
    Unknown
}

public sealed record SourceRegion(string RelativePath, int StartLine, int EndLine)
{
    public bool Contains(int line) => line >= StartLine && line <= EndLine;
}

public sealed record SourceStringOccurrence(string Text, SourceRegion Region, StringSourceKind SourceKind);

public sealed class FlowNode
{
    public required string Id { get; init; }
    public required FlowNodeKind Kind { get; init; }
    public required string DisplayText { get; init; }
    public required SourceRegion Region { get; init; }
    public string? LabelName { get; init; }
    public string? Speaker { get; init; }
    public string? Target { get; init; }
    public string? Condition { get; init; }
    public string? OriginalText { get; init; }
    public string? ParentId { get; init; }
    public int Indent { get; init; }
    public bool IsDynamic { get; init; }
}

public sealed record FlowEdge(string FromId, string ToId, FlowEdgeKind Kind, string? Caption = null);

public sealed class FlowGraph
{
    public List<FlowNode> Nodes { get; } = [];
    public List<FlowEdge> Edges { get; } = [];
    public Dictionary<string, FlowNode> Labels { get; } = new(StringComparer.Ordinal);
}

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? RelativePath = null,
    int? Line = null,
    DiagnosticCategory Category = DiagnosticCategory.General,
    string? SuggestedAction = null,
    string? TechnicalDetails = null);

public sealed class SourceDocument
{
    public required Utf8TextFile File { get; init; }
    public required string RelativePath { get; init; }
    public FlowGraph Graph { get; } = new();
    public List<SourceRegion> ScreenRegions { get; } = [];
    public List<SourceStringOccurrence> StringOccurrences { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
}

public sealed record TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

public sealed class TranslationUnit
{
    public required TranslationUnitKind Kind { get; init; }
    public required string Language { get; init; }
    public required string FilePath { get; init; }
    public required string RelativeTlPath { get; init; }
    public required TextSpan BlockSpan { get; init; }
    public required int HeaderLine { get; init; }
    public string? Identifier { get; init; }
    public string? SourcePath { get; init; }
    public int? SourceLine { get; init; }
    public string? OriginalStatement { get; init; }
    public string? OldText { get; init; }
    public string TranslationText { get; set; } = string.Empty;
    public TextSpan? TranslationValueSpan { get; init; }
    public TextSpan? RawBodySpan { get; init; }
    public string RawBodyText { get; set; } = string.Empty;
    public TextSpan? OldLineSpan { get; init; }
    public bool MissingNew { get; init; }
    public bool IsDirty { get; set; }
    public bool IsRawMode => Kind == TranslationUnitKind.Dialogue && TranslationValueSpan is null && RawBodySpan is not null;
    public bool IsUnboundFlowTranslation =>
        Kind == TranslationUnitKind.Dialogue &&
        BoundNode is null &&
        (SourcePath is not null || OriginalStatement is not null);
    public FlowNode? BoundNode { get; set; }
    public StringSourceKind SourceKind { get; set; } = StringSourceKind.Unknown;
}

public sealed class TlDocument
{
    public required Utf8TextFile File { get; init; }
    public required string Language { get; init; }
    public required string RelativePath { get; init; }
    public List<TranslationUnit> Units { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
}

public sealed class SharedStringEntry
{
    private string _translation = string.Empty;

    public required string Language { get; init; }
    public required string OldText { get; init; }
    public List<TranslationUnit> Definitions { get; } = [];
    public List<TranslationBinding> References { get; } = [];
    public bool HasConflict { get; set; }

    public string Translation
    {
        get => _translation;
        set
        {
            _translation = value;
            foreach (var definition in Definitions)
            {
                definition.TranslationText = value;
                definition.IsDirty = true;
            }
        }
    }

    public void LoadTranslation(string value) => _translation = value;

    public void Unify(string value)
    {
        HasConflict = false;
        Translation = value;
    }
}

public sealed record TranslationBinding(
    TranslationUnit? Unit,
    FlowNode? Node,
    string SourcePath,
    int SourceLine,
    StringSourceKind SourceKind);

public sealed class ProjectSnapshot
{
    public required string ProjectRoot { get; init; }
    public required string GameDirectory { get; init; }
    public required string Language { get; init; }
    public ObservableCollection<SourceDocument> Sources { get; } = [];
    public ObservableCollection<TlDocument> TlDocuments { get; } = [];
    public ObservableCollection<SharedStringEntry> SharedStrings { get; } = [];
    public ObservableCollection<Diagnostic> Diagnostics { get; } = [];
    public FlowGraph Graph { get; } = new();

    public IEnumerable<TranslationUnit> TranslationUnits => TlDocuments.SelectMany(d => d.Units);
}
