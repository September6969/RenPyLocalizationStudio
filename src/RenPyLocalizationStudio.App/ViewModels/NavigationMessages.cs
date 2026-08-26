using CommunityToolkit.Mvvm.Messaging.Messages;

namespace RenPyLocalizationStudio.App.ViewModels;

public enum NavigationFocusTarget { None, FlowList, TranslationEditor, Inspector }

public sealed record NavigateToSourceRequest(
    string? NodeId,
    string? RelativePath,
    int? Line,
    string? Label,
    string TargetView = "Flow",
    bool ExpandParents = true,
    bool OpenInspector = true,
    NavigationFocusTarget FocusTarget = NavigationFocusTarget.TranslationEditor);

public sealed class NavigateToSourceRequestMessage(NavigateToSourceRequest value)
    : ValueChangedMessage<NavigateToSourceRequest>(value);
