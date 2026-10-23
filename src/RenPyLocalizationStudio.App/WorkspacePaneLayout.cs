namespace RenPyLocalizationStudio.App;

internal readonly record struct WorkspacePaneLayout(double SidebarWidth, double InspectorWidth)
{
    public const double SidebarMin = 200, SidebarMax = 420, InspectorMin = 280, InspectorMax = 560;
    public static WorkspacePaneLayout Calculate(double width, bool inspectorVisible, double preferredSidebar = 260, double preferredInspector = 360)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 1500;
        var sidebar = width >= 1180 || width >= 900 && !inspectorVisible ? Math.Clamp(double.IsFinite(preferredSidebar) ? preferredSidebar : 260, SidebarMin, SidebarMax) : 0;
        var inspector = inspectorVisible ? Math.Clamp(double.IsFinite(preferredInspector) ? preferredInspector : 360, InspectorMin, InspectorMax) : 0;
        var mainMin = width < 900 ? 360 : 420;
        var overflow = 48 + (sidebar > 0 ? 6 : 0) + (inspector > 0 ? 6 : 0) + sidebar + inspector + mainMin - width;
        // 先收缩检查器，再收缩侧栏；空间不足时收起导航，并保留用户的首选宽度。
        if (overflow > 0 && inspector > 0) { var cut = Math.Min(overflow, inspector - InspectorMin); inspector -= cut; overflow -= cut; }
        if (overflow > 0 && sidebar > 0) { var cut = Math.Min(overflow, sidebar - SidebarMin); sidebar -= cut; overflow -= cut; }
        if (overflow > 0 && sidebar > 0) sidebar = 0;
        return new(sidebar, inspector);
    }
}
