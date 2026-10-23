namespace RenPyLocalizationStudio.App.Services;

public sealed record UiLayoutSettings(double SidebarWidth = 260, double InspectorWidth = 360,
    bool PreviewCollapsed = true, double PreviewFraction = 0.4)
{
    public UiLayoutSettings Normalize() => this with
    {
        SidebarWidth = double.IsFinite(SidebarWidth) ? Math.Clamp(SidebarWidth, 200, 420) : 260,
        InspectorWidth = double.IsFinite(InspectorWidth) ? Math.Clamp(InspectorWidth, 280, 560) : 360,
        PreviewFraction = double.IsFinite(PreviewFraction) ? Math.Clamp(PreviewFraction, 0.15, 0.65) : 0.4
    };
}
