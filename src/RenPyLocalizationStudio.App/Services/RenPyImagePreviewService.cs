using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace RenPyLocalizationStudio.App.Services;

public sealed record ImagePreviewInfo(
    string Tag,
    string Kind,
    string FilePath,
    string RelativePath,
    string Resolution,
    string FileSizeText);

public sealed record RenPySceneContext(
    string? SceneStatement,
    string? SceneTag,
    IReadOnlyList<ImagePreviewInfo> AvailableImages);

public interface IRenPyImagePreviewService
{
    Task<RenPySceneContext> ResolveSceneContextAsync(string projectPath, string? relativeScriptPath, int line);
}

public sealed partial class RenPyImagePreviewService : IRenPyImagePreviewService
{
    private static readonly string[] ImageExtensions = [".png", ".webp", ".jpg", ".jpeg", ".avif", ".bmp"];
    private static readonly ConcurrentDictionary<string, (DateTime ScannedAt, List<string> Files)> ProjectImageCache = new();

    [GeneratedRegex(@"^\s*image\s+(?<tag>[A-Za-z0-9_ ]+?)\s*=\s*[""'](?<path>[^""']+)[""']\s*(?:#.*)?$", RegexOptions.Compiled)]
    private static partial Regex ImageDefineRegex();

    [GeneratedRegex(@"^(?<tag>.+?)(?:\s+(?:with|at|as|behind|onlayer|zorder)\b.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ClauseFilterRegex();

    public async Task<RenPySceneContext> ResolveSceneContextAsync(string projectPath, string? relativeScriptPath, int line)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(relativeScriptPath) || line <= 0)
        {
            return new RenPySceneContext(null, null, []);
        }

        return await Task.Run(() =>
        {
            try
            {
                var fullScriptPath = ResolveScriptPath(projectPath, relativeScriptPath);
                if (!File.Exists(fullScriptPath))
                {
                    return new RenPySceneContext(null, null, []);
                }

                var allImageFiles = GetAllProjectImageFiles(projectPath);

                var lines = File.ReadAllLines(fullScriptPath);
                var targetLineIndex = Math.Min(line - 1, lines.Length - 1);

                string? lastSceneTag = null;
                string? lastSceneStatement = null;
                var activeShowTags = new List<string>();
                var imageDefinitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i <= targetLineIndex; i++)
                {
                    var currentLine = lines[i];
                    var trimmed = currentLine.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                    var defMatch = ImageDefineRegex().Match(currentLine);
                    if (defMatch.Success)
                    {
                        var tag = defMatch.Groups["tag"].Value.Trim();
                        var path = defMatch.Groups["path"].Value.Trim();
                        imageDefinitions[tag] = path;
                        continue;
                    }

                    if (IsCommand(trimmed, "scene"))
                    {
                        var tag = ExtractImageTag(trimmed, "scene");
                        lastSceneTag = tag;
                        lastSceneStatement = trimmed;
                        activeShowTags.Clear();
                        continue;
                    }

                    if (IsCommand(trimmed, "show"))
                    {
                        var tag = ExtractImageTag(trimmed, "show");
                        if (!string.IsNullOrWhiteSpace(tag))
                        {
                            var primaryTag = tag.Split(' ', '_')[0];
                            activeShowTags.RemoveAll(x => x.Split(' ', '_')[0].Equals(primaryTag, StringComparison.OrdinalIgnoreCase));
                            activeShowTags.Add(tag);
                        }
                        continue;
                    }

                    if (IsCommand(trimmed, "hide"))
                    {
                        var tag = ExtractImageTag(trimmed, "hide");
                        if (!string.IsNullOrWhiteSpace(tag))
                        {
                            var primaryTag = tag.Split(' ', '_')[0];
                            activeShowTags.RemoveAll(x => x.Split(' ', '_')[0].Equals(primaryTag, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }

                var availableImages = new List<ImagePreviewInfo>();

                if (!string.IsNullOrWhiteSpace(lastSceneTag))
                {
                    var bgFile = FindMatchingImageFile(projectPath, lastSceneTag, imageDefinitions, allImageFiles);
                    if (bgFile is not null)
                    {
                        availableImages.Add(CreatePreviewInfo($"scene {lastSceneTag}", "背景", bgFile, projectPath));
                    }
                }

                foreach (var showTag in activeShowTags)
                {
                    var spriteFile = FindMatchingImageFile(projectPath, showTag, imageDefinitions, allImageFiles);
                    if (spriteFile is not null)
                    {
                        availableImages.Add(CreatePreviewInfo($"show {showTag}", "立绘", spriteFile, projectPath));
                    }
                }

                return new RenPySceneContext(lastSceneStatement, lastSceneTag, availableImages);
            }
            catch
            {
                return new RenPySceneContext(null, null, []);
            }
        });
    }

    private static bool IsCommand(string line, string command)
    {
        if (!line.StartsWith(command, StringComparison.OrdinalIgnoreCase)) return false;
        if (line.Length == command.Length) return true;
        var nextChar = line[command.Length];
        return char.IsWhiteSpace(nextChar) || nextChar == ':';
    }

    private static string? ExtractImageTag(string line, string command)
    {
        var trimmed = line.Trim();
        var commentIdx = trimmed.IndexOf('#');
        if (commentIdx >= 0) trimmed = trimmed[..commentIdx].Trim();
        if (trimmed.Length == 0) return null;

        if (!trimmed.StartsWith(command, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = trimmed[command.Length..].Trim();
        if (rest.Length == 0) return null;

        var colonIdx = rest.IndexOf(':');
        if (colonIdx >= 0) rest = rest[..colonIdx].Trim();

        var match = ClauseFilterRegex().Match(rest);
        if (match.Success)
        {
            var tag = match.Groups["tag"].Value.Trim();
            return string.IsNullOrWhiteSpace(tag) ? null : tag;
        }

        return rest.Trim();
    }

    private static List<string> GetAllProjectImageFiles(string projectPath)
    {
        if (ProjectImageCache.TryGetValue(projectPath, out var cached) &&
            (DateTime.UtcNow - cached.ScannedAt).TotalSeconds < 10)
        {
            return cached.Files;
        }

        var results = new List<string>();
        var searchRoots = new[]
        {
            Path.Combine(projectPath, "game", "images"),
            Path.Combine(projectPath, "game"),
            Path.Combine(projectPath, "images"),
            Path.Combine(projectPath, "game", "gui")
        };

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root) || !visited.Add(root)) continue;

            try
            {
                var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file);
                    if (ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                    }
                }
            }
            catch { }
        }

        ProjectImageCache[projectPath] = (DateTime.UtcNow, results);
        return results;
    }

    private static string? FindMatchingImageFile(string projectPath, string tag, Dictionary<string, string> imageDefinitions, List<string> allImageFiles)
    {
        if (imageDefinitions.TryGetValue(tag, out var definedPath))
        {
            var resolved = ResolveExplicitPath(projectPath, definedPath);
            if (resolved is not null) return resolved;
        }

        var cleanTag = tag.Trim();
        var tagUnderscore = cleanTag.Replace(' ', '_');
        var tagSpace = cleanTag.Replace('_', ' ');
        var tagCompact = cleanTag.Replace(" ", "").Replace("_", "").ToLowerInvariant();

        // 1. Exact match on filename without extension (case-insensitive)
        foreach (var file in allImageFiles)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(name, cleanTag, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, tagUnderscore, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, tagSpace, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        // 2. Compact match (ignoring separators, spaces, underscores, case)
        foreach (var file in allImageFiles)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var nameCompact = name.Replace(" ", "").Replace("_", "").ToLowerInvariant();
            if (nameCompact == tagCompact)
            {
                return file;
            }
        }

        // 3. Match relative path under images folder (e.g. tag "bg room" -> "images/bg/room.png" or "images/bg_room.jpg")
        foreach (var file in allImageFiles)
        {
            var relPath = file.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase)
                ? file[projectPath.Length..].Replace('\\', '/').TrimStart('/')
                : file.Replace('\\', '/');

            var relNoExt = Path.ChangeExtension(relPath, null) ?? relPath;
            var relTokens = relNoExt.Split('/', StringSplitOptions.RemoveEmptyEntries);

            var combinedRel = string.Join(" ", relTokens).Replace('_', ' ');
            if (combinedRel.EndsWith(tagSpace, StringComparison.OrdinalIgnoreCase) ||
                combinedRel.Contains(tagSpace, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    private static string ResolveScriptPath(string projectPath, string relativeScriptPath)
    {
        var normalized = relativeScriptPath.Replace('\\', '/').TrimStart('/');
        var direct = Path.Combine(projectPath, normalized);
        if (File.Exists(direct)) return direct;

        var inGame = Path.Combine(projectPath, "game", normalized);
        if (File.Exists(inGame)) return inGame;

        return direct;
    }

    private static string? ResolveExplicitPath(string projectPath, string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute) && File.Exists(relativeOrAbsolute)) return relativeOrAbsolute;

        var inGame = Path.Combine(projectPath, "game", relativeOrAbsolute);
        if (File.Exists(inGame)) return inGame;

        var inImages = Path.Combine(projectPath, "game", "images", relativeOrAbsolute);
        if (File.Exists(inImages)) return inImages;

        var inRoot = Path.Combine(projectPath, relativeOrAbsolute);
        if (File.Exists(inRoot)) return inRoot;

        return null;
    }

    private static ImagePreviewInfo CreatePreviewInfo(string tag, string kind, string filePath, string projectPath)
    {
        var relPath = filePath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase)
            ? filePath[projectPath.Length..].TrimStart('\\', '/')
            : Path.GetFileName(filePath);

        string resolution = string.Empty;
        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            resolution = $"{frame.PixelWidth} × {frame.PixelHeight}";
        }
        catch
        {
            resolution = "未知分辨率";
        }

        string sizeText = string.Empty;
        try
        {
            var length = new FileInfo(filePath).Length;
            sizeText = length switch
            {
                > 1024 * 1024 => $"{(double)length / (1024 * 1024):0.1} MB",
                > 1024 => $"{length / 1024} KB",
                _ => $"{length} B"
            };
        }
        catch
        {
            sizeText = string.Empty;
        }

        return new ImagePreviewInfo(tag, kind, filePath, relPath, resolution, sizeText);
    }
}
