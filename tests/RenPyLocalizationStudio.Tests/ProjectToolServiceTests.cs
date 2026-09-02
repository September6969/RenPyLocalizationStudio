using RenPyLocalizationStudio.Core.Services;
using SkiaSharp;

namespace RenPyLocalizationStudio.Tests;

public sealed class ProjectToolServiceTests
{
    [Fact]
    public void ImageCompression_拒绝超大解码尺寸和源文件()
    {
        Assert.False(ImageCompressionService.ValidateResourceLimits(
            ImageCompressionService.MaximumSourceBytes + 1, 1, 1, out var byteReason));
        Assert.False(ImageCompressionService.ValidateResourceLimits(
            1024, 20_000, 20_000, out var pixelReason));
        Assert.True(ImageCompressionService.ValidateResourceLimits(1024, 1920, 1080, out _));
        Assert.Contains("MiB", byteReason);
        Assert.Contains("像素", pixelReason);
    }

    [Fact]
    public async Task PrefixRename_预览冲突且只执行安全项()
    {
        using var project = new TemporaryToolProject();
        await File.WriteAllTextAsync(Path.Combine(project.Images, "x-safe.txt"), "safe");
        await File.WriteAllTextAsync(Path.Combine(project.Images, "x-clash.txt"), "source");
        await File.WriteAllTextAsync(Path.Combine(project.Images, "clash.txt"), "target");
        var fileSystem = new FileSystemService();
        var service = new PrefixRenameService(fileSystem);
        var root = fileSystem.ValidateProjectRoot(project.Root).Value!;

        var plan = await service.PlanAsync(new PrefixRenamePlanRequest(root, "game/images", "x-"),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.True(plan.IsSuccess);
        Assert.Equal(1, plan.Value!.ReadyCount);
        Assert.Equal(1, plan.Value.ConflictCount);

        var execution = await service.ExecuteAsync(new PrefixRenameExecutionRequest(plan.Value),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.True(execution.IsSuccess);
        Assert.True(File.Exists(Path.Combine(project.Images, "safe.txt")));
        Assert.True(File.Exists(Path.Combine(project.Images, "x-clash.txt")));
        Assert.Equal("target", await File.ReadAllTextAsync(Path.Combine(project.Images, "clash.txt")));
    }

    [Fact]
    public async Task PrefixRename_预览后源文件变化会阻止移动()
    {
        using var project = new TemporaryToolProject();
        var source = Path.Combine(project.Images, "x-change.txt");
        await File.WriteAllTextAsync(source, "before");
        var fileSystem = new FileSystemService();
        var service = new PrefixRenameService(fileSystem);
        var root = fileSystem.ValidateProjectRoot(project.Root).Value!;
        var plan = await service.PlanAsync(new PrefixRenamePlanRequest(root, "game/images", "x-"),
            new Progress<ToolOperationProgress>(), CancellationToken.None);
        await File.WriteAllTextAsync(source, "after");

        var execution = await service.ExecuteAsync(new PrefixRenameExecutionRequest(plan.Value!),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, execution.Status);
        Assert.Contains(execution.Diagnostics, x => x.Code == "EXTERNAL_MODIFICATION");
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(project.Images, "change.txt")));
    }

    [Fact]
    public async Task ImageCompression_默认写入独立目录并保留原图()
    {
        using var project = new TemporaryToolProject();
        var source = Path.Combine(project.Images, "large.png");
        await File.WriteAllBytesAsync(source, CreateBloatedPng());
        var original = await File.ReadAllBytesAsync(source);
        var fileSystem = new FileSystemService();
        var service = new ImageCompressionService(fileSystem);
        var root = fileSystem.ValidateProjectRoot(project.Root).Value!;

        var plan = await service.PlanAsync(new ImageCompressionPlanRequest(root, "game/images", 85, 1, false, "game/optimized"),
            new Progress<ToolOperationProgress>(), CancellationToken.None);
        var execution = await service.ExecuteAsync(new ImageCompressionExecutionRequest(plan.Value!),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        var output = Path.Combine(project.Root, "game", "optimized", "large.png");
        Assert.True(plan.IsSuccess);
        Assert.Equal(1, plan.Value!.ReadyCount);
        Assert.True(execution.IsSuccess);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length < original.Length);
    }

    [Fact]
    public async Task ImageCompression_替换原图时创建备份()
    {
        using var project = new TemporaryToolProject();
        var source = Path.Combine(project.Images, "replace.png");
        var original = CreateBloatedPng();
        await File.WriteAllBytesAsync(source, original);
        var fileSystem = new FileSystemService();
        var service = new ImageCompressionService(fileSystem);
        var root = fileSystem.ValidateProjectRoot(project.Root).Value!;
        var plan = await service.PlanAsync(new ImageCompressionPlanRequest(root, "game/images", 85, 1, true),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        var execution = await service.ExecuteAsync(new ImageCompressionExecutionRequest(plan.Value!),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.True(execution.IsSuccess);
        Assert.True(File.Exists(source + ".rls.bak"));
        Assert.Equal(original, await File.ReadAllBytesAsync(source + ".rls.bak"));
        Assert.True(new FileInfo(source).Length < original.Length);
    }

    private static byte[] CreateBloatedPng()
    {
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray().Concat(new byte[50_000]).ToArray();
    }

    private sealed class TemporaryToolProject : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rls-tool-tests", Guid.NewGuid().ToString("N"));
        public string Images => Path.Combine(Root, "game", "images");
        public TemporaryToolProject() => Directory.CreateDirectory(Images);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
