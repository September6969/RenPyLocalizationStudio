using System.IO;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed record SavedBookmark(string NodeId, string Path, string? Label, string Text, FlowNodeKind Kind);
public sealed record TranslationWorkspaceState(string ProjectPath, string Language, string? ItemId,
    string ViewMode, string GroupingMode, string SearchText = "", string StatusFilter = "All",
    string Scope = "all", IReadOnlyList<SavedBookmark>? Bookmarks = null,
    IReadOnlyList<GlossaryTerm>? Glossary = null, IReadOnlyDictionary<string, ReviewRecord>? Reviews = null,
    TranslationSearchField SearchField = TranslationSearchField.All, bool SearchCaseSensitive = false,
    bool SearchRegex = false, ReviewStatus? ReviewFilter = null);

public static class WorkspaceStateIdentity
{
    public static string Key(string project, string language) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)).ToUpperInvariant() + "\u001f" + language.ToUpperInvariant();

    public static SavedBookmark Bookmark(ProjectSnapshot snapshot, FlowNode node) => new(node.Id,
        node.Region.RelativePath, snapshot.Graph.Labels.Values.Where(label =>
            label.Region.RelativePath.Equals(node.Region.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            label.Region.StartLine <= node.Region.StartLine).MaxBy(label => label.Region.StartLine)?.LabelName,
        node.DisplayText, node.Kind);

    public static FlowNode? Resolve(ProjectSnapshot snapshot, SavedBookmark saved)
    {
        bool Matches(FlowNode node) => node.Kind == saved.Kind && node.DisplayText == saved.Text &&
            node.Region.RelativePath.Equals(saved.Path, StringComparison.OrdinalIgnoreCase) && Bookmark(snapshot, node).Label == saved.Label;
        var exact = snapshot.Graph.Nodes.FirstOrDefault(node => node.Id == saved.NodeId && Matches(node));
        if (exact is not null) return exact;
        var candidates = snapshot.Graph.Nodes.Where(Matches).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
