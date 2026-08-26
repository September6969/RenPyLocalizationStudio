using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class ProjectServiceTests
{
    [Fact]
    public async Task Catalog_从内存文件系统发现语言()
    {
        var fileSystem = CreateProject();
        fileSystem.AddText("game/tl/japanese/script.rpy", "translate japanese strings:\n");
        var result = await new ProjectCatalogService(fileSystem).ExecuteAsync(
            new ProjectLanguageDiscoveryRequest("C:\\project"),
            new Progress<ToolOperationProgress>(),
            CancellationToken.None);

        Assert.Equal(["japanese", "schinese"], result.Value);
    }

    [Fact]
    public async Task Analysis_无效Utf8收敛为诊断且继续分析()
    {
        var fileSystem = CreateProject();
        fileSystem.AddBytes("game/broken.rpy", [0xff, 0xfe]);
        var result = await new ProjectAnalysisService(fileSystem).ExecuteAsync(
            new ProjectAnalysisRequest("C:\\project", "schinese"),
            new Progress<ToolOperationProgress>(),
            CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Contains(result.Value.Diagnostics, diagnostic => diagnostic.Code == "UTF8_READ_FAILED");
    }

    [Fact]
    public async Task Analysis_取消后返回Cancelled而不抛异常()
    {
        var fileSystem = CreateProject();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new ProjectAnalysisService(fileSystem).ExecuteAsync(
            new ProjectAnalysisRequest("C:\\project", "schinese"),
            new Progress<ToolOperationProgress>(),
            cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(0, fileSystem.WriteCount);
    }

    [Fact]
    public void FileSystem_拒绝项目外路径()
    {
        var fileSystem = CreateProject();
        var root = Assert.IsType<ProjectRoot>(fileSystem.ValidateProjectRoot("C:\\project").Value);
        var result = fileSystem.ValidateProjectPath(root, "../outside.rpy");

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PATH_TRAVERSAL");
    }

    private static InMemoryFileSystem CreateProject()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddText("game/script.rpy", "label start:\n    \"Hello\"\n");
        fileSystem.AddText("game/tl/schinese/script.rpy", "# game/script.rpy:2\ntranslate schinese start_a:\n    # \"Hello\"\n    \"你好\"\n");
        return fileSystem;
    }
}
