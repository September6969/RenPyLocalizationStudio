using RenPyLocalizationStudio.App.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class WorkspaceTaskCoordinatorTests
{
    [Fact]
    public async Task RunLatest_快速替换只允许最终任务完成()
    {
        var completed = new List<string>();
        using var coordinator = new WorkspaceTaskCoordinator(_ => { });
        var first = coordinator.RunLatestAsync("workspace", async token =>
        {
            await Task.Delay(500, token);
            completed.Add("first");
        });
        var second = coordinator.RunLatestAsync("workspace", async token =>
        {
            await Task.Delay(10, token);
            completed.Add("second");
        });

        await Task.WhenAll(first, second);
        Assert.Equal(["second"], completed);
    }

    [Fact]
    public async Task RunLatest_后台异常被观察并上报()
    {
        Exception? observed = null;
        using var coordinator = new WorkspaceTaskCoordinator(exception => observed = exception);

        await coordinator.RunLatestAsync("settings", _ => throw new IOException("disk full"));

        Assert.IsType<IOException>(observed);
        Assert.Equal("disk full", observed.Message);
    }

    [Fact]
    public async Task Cancel_等待任务退出且不遗留异常()
    {
        using var coordinator = new WorkspaceTaskCoordinator(_ => { });
        var running = coordinator.RunLatestAsync("preview", token => Task.Delay(TimeSpan.FromMinutes(1), token));

        await coordinator.CancelAsync("preview");
        await running;
    }
}
