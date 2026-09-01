namespace RenPyLocalizationStudio.Core.Services;

/// <summary>
/// 统一处理“选择项目根”与“直接选择 game 目录”两种布局。
/// </summary>
public static class ProjectLayout
{
    public static bool RootIsGameDirectory(ProjectRoot root) =>
        string.Equals(Path.GetFileName(root.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            "game", StringComparison.OrdinalIgnoreCase);

    public static string GameDirectory(ProjectRoot root) => RootIsGameDirectory(root) ? "." : "game";

    public static string UnderGame(ProjectRoot root, params string[] segments)
    {
        var relative = Path.Combine(segments);
        return RootIsGameDirectory(root) ? relative : Path.Combine("game", relative);
    }

    public static string TlDirectory(ProjectRoot root, string language) =>
        UnderGame(root, "tl", language);

    public static string ResolveProjectRelativePath(ProjectRoot root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (!RootIsGameDirectory(root)) return normalized;
        if (normalized.Equals("game", StringComparison.OrdinalIgnoreCase)) return ".";
        return normalized.StartsWith("game/", StringComparison.OrdinalIgnoreCase)
            ? normalized["game/".Length..]
            : normalized;
    }
}
