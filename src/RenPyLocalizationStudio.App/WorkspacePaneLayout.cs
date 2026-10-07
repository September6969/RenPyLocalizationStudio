namespace RenPyLocalizationStudio.App;

internal readonly record struct WorkspacePaneLayout(double SidebarWidth, double InspectorWidth)
{
    public static WorkspacePaneLayout Calculate(double width, bool inspectorVisible)
    {
        // 小窗口优先保留编辑器和正文，关闭检查器后让出空间给项目导航。
        var sidebar = width >= 1180 || (width >= 900 && !inspectorVisible) ? 260 : 0;
        var inspector = inspectorVisible ? width >= 1180 ? 360 : 300 : 0;
        return new WorkspacePaneLayout(sidebar, inspector);
    }
}
