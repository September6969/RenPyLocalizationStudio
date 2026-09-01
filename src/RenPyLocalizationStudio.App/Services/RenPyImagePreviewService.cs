using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace RenPyLocalizationStudio.App.Services;

public sealed record ImagePreviewInfo(string Tag, string Kind, string FilePath, string RelativePath, string Resolution, string FileSizeText);
public sealed record RenPySceneContext(string? SceneStatement, string? SceneTag, IReadOnlyList<ImagePreviewInfo> AvailableImages);
public sealed record RenPyImagePreviewRequest(string ProjectPath, string? RelativeScriptPath, int Line, string? StableNodeId = null);

public interface IRenPyImagePreviewService
{
    Task<RenPySceneContext> ResolveSceneContextAsync(RenPyImagePreviewRequest request, CancellationToken cancellationToken);
    void InvalidateProject(string projectPath);
}

public sealed partial class RenPyImagePreviewService : IRenPyImagePreviewService, IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new([".png", ".webp", ".jpg", ".jpeg", ".avif", ".bmp"], StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProjectAssetIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _indexVersions = new(StringComparer.OrdinalIgnoreCase);
    private int _indexBuildCount;
    private int _disposed;

    internal int IndexBuildCount => Volatile.Read(ref _indexBuildCount);

    [GeneratedRegex(@"^\s*image\s+(?<tag>[A-Za-z0-9_ ]+?)\s*=\s*[""'](?<path>[^""']+)[""']\s*(?:#.*)?$", RegexOptions.Compiled)]
    private static partial Regex ImageDefineRegex();

    [GeneratedRegex(@"^(?<tag>.+?)(?:\s+(?:with|at|as|behind|onlayer|zorder)\b.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ClauseFilterRegex();

    public async Task<RenPySceneContext> ResolveSceneContextAsync(RenPyImagePreviewRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(request.ProjectPath) || string.IsNullOrWhiteSpace(request.RelativeScriptPath) || request.Line <= 0)
            return new RenPySceneContext(null, null, []);

        var index = await GetIndexAsync(request.ProjectPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var scriptPath = ResolveScriptPath(request.ProjectPath, request.RelativeScriptPath);
        if (scriptPath is null || !index.Scripts.TryGetValue(scriptPath, out var script))
            return new RenPySceneContext(null, null, []);

        var state = script.Resolve(request.Line);
        var available = new List<ImagePreviewInfo>();
        AddPreview(state.SceneTag, "scene", "背景", available, request.ProjectPath, index);
        foreach (var tag in state.ShowTags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddPreview(tag, "show", "立绘", available, request.ProjectPath, index);
        }
        return new RenPySceneContext(state.SceneStatement, state.SceneTag, available);
    }

    public void InvalidateProject(string projectPath)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var key = NormalizeFullPath(projectPath);
        _indexVersions.AddOrUpdate(key, 1, static (_, version) => version + 1);
        if (_indexes.TryRemove(key, out var index)) index.Dispose();
        // 活跃请求仍持有自己的 gate；从字典移除即可让项目切换后的新请求使用新 gate，
        // 避免长期保留已离开的项目锁，同时不在并发等待期间误 Dispose。
        _indexLocks.TryRemove(key, out _);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var index in _indexes.Values) index.Dispose();
        _indexes.Clear();
        // 活跃构建可能仍会在 finally 中 Release gate；由 GC 回收 gate，禁止在这里提前 Dispose。
        _indexLocks.Clear();
        _indexVersions.Clear();
    }

    private async Task<ProjectAssetIndex> GetIndexAsync(string projectPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = NormalizeFullPath(projectPath);
        if (_indexes.TryGetValue(key, out var current) && !current.IsDirty)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return current;
        }
        var gate = _indexLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            while (true)
            {
                if (_indexes.TryGetValue(key, out current) && !current.IsDirty)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    return current;
                }
                var version = _indexVersions.GetOrAdd(key, 0);
                current?.Dispose();
                var rebuilt = await Task.Run(() => BuildIndex(key, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    rebuilt.Dispose();
                    throw new ObjectDisposedException(nameof(RenPyImagePreviewService));
                }
                if (version != _indexVersions.GetOrAdd(key, 0))
                {
                    // 构建期间项目被失效，不能把旧目录快照重新放回缓存。
                    rebuilt.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }
                Interlocked.Increment(ref _indexBuildCount);
                _indexes[key] = rebuilt;
                return rebuilt;
            }
        }
        finally { gate.Release(); }
    }

    private static ProjectAssetIndex BuildIndex(string projectPath, CancellationToken cancellationToken)
    {
        var gamePath = Directory.Exists(Path.Combine(projectPath, "game")) ? Path.Combine(projectPath, "game") : projectPath;
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scripts = new Dictionary<string, ScriptSceneIndex>(StringComparer.OrdinalIgnoreCase);
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in Directory.EnumerateFiles(gamePath, "*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = NormalizeFullPath(file);
            var extension = Path.GetExtension(fullPath);
            if (ImageExtensions.Contains(extension)) images.Add(fullPath);
            else if (extension.Equals(".rpy", StringComparison.OrdinalIgnoreCase) && !IsTranslationFile(gamePath, fullPath))
                scripts[fullPath] = ScriptSceneIndex.Parse(File.ReadLines(fullPath), definitions);
        }
        return new ProjectAssetIndex(gamePath, images.Order(StringComparer.OrdinalIgnoreCase).ToArray(), scripts, definitions);
    }

    private static bool IsTranslationFile(string gamePath, string fullPath) =>
        fullPath.StartsWith(Path.Combine(gamePath, "tl") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void AddPreview(string? tag, string command, string kind, List<ImagePreviewInfo> output, string projectPath, ProjectAssetIndex index)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        var file = FindMatchingImageFile(projectPath, tag, index);
        if (file is not null && output.All(item => !item.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase)))
            output.Add(CreatePreviewInfo($"{command} {tag}", kind, file, projectPath));
    }

    private static string? FindMatchingImageFile(string projectPath, string tag, ProjectAssetIndex index)
    {
        if (index.Definitions.TryGetValue(tag, out var definedPath))
        {
            var explicitPath = ResolveExplicitPath(projectPath, definedPath);
            if (explicitPath is not null && index.ImageSet.Contains(explicitPath)) return explicitPath;
        }
        var clean = tag.Trim();
        foreach (var key in CandidateImageKeys(clean))
            if (index.ImageLookup.TryGetValue(key, out var file)) return file;
        return null;
    }

    private static string Compact(string value) => value.Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string? ResolveScriptPath(string projectPath, string relativeScriptPath)
    {
        var normalized = relativeScriptPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var direct = NormalizeFullPath(Path.Combine(projectPath, normalized));
        if (File.Exists(direct)) return direct;
        var inGame = NormalizeFullPath(Path.Combine(projectPath, "game", normalized));
        return File.Exists(inGame) ? inGame : null;
    }

    private static string? ResolveExplicitPath(string projectPath, string path)
    {
        IEnumerable<string> candidates = Path.IsPathRooted(path) ? [path] :
            [Path.Combine(projectPath, "game", path), Path.Combine(projectPath, "game", "images", path), Path.Combine(projectPath, path)];
        return candidates.Select(NormalizeFullPath).FirstOrDefault(File.Exists);
    }

    private static ImagePreviewInfo CreatePreviewInfo(string tag, string kind, string filePath, string projectPath)
    {
        string resolution;
        try
        {
            using var stream = File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);
            resolution = codec is null ? "未知分辨率" : $"{codec.Info.Width} × {codec.Info.Height}";
        }
        catch { resolution = "未知分辨率"; }
        string size;
        try
        {
            var length = new FileInfo(filePath).Length;
            size = length switch { > 1048576 => $"{(double)length / 1048576:0.1} MB", > 1024 => $"{length / 1024} KB", _ => $"{length} B" };
        }
        catch { size = "未知大小"; }
        return new ImagePreviewInfo(tag, kind, filePath, Path.GetRelativePath(projectPath, filePath), resolution, size);
    }

    private static bool IsCommand(string line, string command) => line.StartsWith(command, StringComparison.OrdinalIgnoreCase) &&
        (line.Length == command.Length || char.IsWhiteSpace(line[command.Length]) || line[command.Length] == ':');

    private static string? ExtractImageTag(string line, string command)
    {
        var rest = line.Split('#', 2)[0].Trim()[command.Length..].Trim();
        var colon = rest.IndexOf(':');
        if (colon >= 0) rest = rest[..colon].Trim();
        var match = ClauseFilterRegex().Match(rest);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups["tag"].Value) ? match.Groups["tag"].Value.Trim() : null;
    }

    private static string NormalizeFullPath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class ProjectAssetIndex : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        public ProjectAssetIndex(string gamePath, IReadOnlyList<string> images, IReadOnlyDictionary<string, ScriptSceneIndex> scripts, IReadOnlyDictionary<string, string> definitions)
        {
            Images = images;
            ImageSet = images.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var image in images)
            {
                var name = Path.GetFileNameWithoutExtension(image);
                var relative = Path.ChangeExtension(Path.GetRelativePath(gamePath, image), null)?.Replace('\\', '/') ?? name;
                foreach (var key in CandidateImageKeys(name).Concat(CandidateImageKeys(relative)))
                    lookup.TryAdd(key, image);
            }
            ImageLookup = lookup;
            Scripts = scripts;
            Definitions = definitions;
            _watcher = new FileSystemWatcher(gamePath) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Changed += MarkDirty; _watcher.Created += MarkDirty; _watcher.Deleted += MarkDirty; _watcher.Renamed += MarkDirty;
            _watcher.Error += MarkDirty;
            _watcher.EnableRaisingEvents = true;
        }
        public IReadOnlyList<string> Images { get; }
        public HashSet<string> ImageSet { get; }
        public IReadOnlyDictionary<string, string> ImageLookup { get; }
        public IReadOnlyDictionary<string, ScriptSceneIndex> Scripts { get; }
        public IReadOnlyDictionary<string, string> Definitions { get; }
        private int _isDirty;
        public bool IsDirty => Volatile.Read(ref _isDirty) != 0;
        private void MarkDirty(object sender, FileSystemEventArgs args) => Interlocked.Exchange(ref _isDirty, 1);
        private void MarkDirty(object sender, ErrorEventArgs args) => Interlocked.Exchange(ref _isDirty, 1);
        public void Dispose() => _watcher.Dispose();
    }

    private sealed record SceneEvent(int Line, string Command, string? Tag, string Statement);
    private sealed record SceneState(string? SceneStatement, string? SceneTag, IReadOnlyList<string> ShowTags);

    private sealed class ScriptSceneIndex
    {
        private readonly IReadOnlyList<SceneEvent> _events;
        private readonly IReadOnlyList<SceneState> _states;
        private ScriptSceneIndex(IReadOnlyList<SceneEvent> events)
        {
            _events = events;
            var states = new List<SceneState>(events.Count);
            string? scene = null;
            string? sceneTag = null;
            var shows = new List<string>();
            foreach (var item in events)
            {
                if (item.Command == "scene") { scene = item.Statement; sceneTag = item.Tag; shows.Clear(); }
                else if (item.Tag is not null)
                {
                    var primary = item.Tag.Split(' ', '_')[0];
                    shows.RemoveAll(tag => tag.Split(' ', '_')[0].Equals(primary, StringComparison.OrdinalIgnoreCase));
                    if (item.Command == "show") shows.Add(item.Tag);
                }
                states.Add(new SceneState(scene, sceneTag, shows.ToArray()));
            }
            _states = states;
        }
        public static ScriptSceneIndex Parse(IEnumerable<string> lines, IDictionary<string, string> definitions)
        {
            var events = new List<SceneEvent>();
            var lineNumber = 0;
            foreach (var line in lines)
            {
                lineNumber++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var definition = ImageDefineRegex().Match(line);
                if (definition.Success) { definitions[definition.Groups["tag"].Value.Trim()] = definition.Groups["path"].Value.Trim(); continue; }
                foreach (var command in new[] { "scene", "show", "hide" })
                    if (IsCommand(trimmed, command)) { events.Add(new SceneEvent(lineNumber, command, ExtractImageTag(trimmed, command), trimmed)); break; }
            }
            return new ScriptSceneIndex(events);
        }
        public SceneState Resolve(int line)
        {
            var low = 0;
            var high = _events.Count - 1;
            var found = -1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                if (_events[middle].Line <= line) { found = middle; low = middle + 1; }
                else high = middle - 1;
            }
            return found < 0 ? new SceneState(null, null, []) : _states[found];
        }
    }

    private static IEnumerable<string> CandidateImageKeys(string value)
    {
        var normalized = value.Trim().Replace('\\', '/');
        yield return normalized;
        yield return normalized.Replace(' ', '_');
        yield return normalized.Replace('_', ' ');
        yield return Compact(normalized);
        var fileName = Path.GetFileName(normalized);
        if (!fileName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return fileName;
            yield return fileName.Replace(' ', '_');
            yield return fileName.Replace('_', ' ');
            yield return Compact(fileName);
        }
    }
}
