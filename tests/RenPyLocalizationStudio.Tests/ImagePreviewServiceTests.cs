using RenPyLocalizationStudio.App.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class ImagePreviewServiceTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task Index_重叠目录不重复且同一项目只构建一次()
    {
        await using var project = await PreviewProject.CreateAsync();
        using var service = new RenPyImagePreviewService();
        var request = new RenPyImagePreviewRequest(project.Root, "game/script.rpy", 4, "node-a");

        var first = await service.ResolveSceneContextAsync(request, CancellationToken.None);
        var second = await service.ResolveSceneContextAsync(request, CancellationToken.None);

        Assert.Equal(2, first.AvailableImages.Count);
        Assert.Equal(2, first.AvailableImages.Select(item => item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(first.AvailableImages, item => Assert.Equal("1 × 1", item.Resolution));
        Assert.Equal(first.SceneStatement, second.SceneStatement);
        Assert.Equal(first.AvailableImages.Select(item => item.FilePath), second.AvailableImages.Select(item => item.FilePath));
        Assert.Equal(1, service.IndexBuildCount);
    }

    [Fact]
    public async Task Index_文件变化后自动失效并重建()
    {
        await using var project = await PreviewProject.CreateAsync();
        using var service = new RenPyImagePreviewService();
        var request = new RenPyImagePreviewRequest(project.Root, "game/script.rpy", 4);
        await service.ResolveSceneContextAsync(request, CancellationToken.None);

        await File.AppendAllTextAsync(Path.Combine(project.Root, "game", "script.rpy"), "\n# changed");
        for (var attempt = 0; attempt < 20 && service.IndexBuildCount == 1; attempt++)
        {
            await Task.Delay(25);
            await service.ResolveSceneContextAsync(request, CancellationToken.None);
        }

        Assert.Equal(2, service.IndexBuildCount);
    }

    [Fact]
    public async Task Resolve_取消请求不会返回旧结果()
    {
        await using var project = await PreviewProject.CreateAsync();
        using var service = new RenPyImagePreviewService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResolveSceneContextAsync(
            new RenPyImagePreviewRequest(project.Root, "game/script.rpy", 4), cancellation.Token));
    }

    private sealed record PreviewProject(string Root) : IAsyncDisposable
    {
        public static async Task<PreviewProject> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RlsPreviewTests", Guid.NewGuid().ToString("N"));
            var images = Path.Combine(root, "game", "images");
            Directory.CreateDirectory(images);
            await File.WriteAllBytesAsync(Path.Combine(images, "bg_room.png"), OnePixelPng);
            await File.WriteAllBytesAsync(Path.Combine(images, "alice_happy.png"), OnePixelPng);
            await File.WriteAllTextAsync(Path.Combine(root, "game", "script.rpy"), """
image bg room = "images/bg_room.png"
label start:
    scene bg room with dissolve
    show alice happy at center
""");
            return new PreviewProject(root);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
