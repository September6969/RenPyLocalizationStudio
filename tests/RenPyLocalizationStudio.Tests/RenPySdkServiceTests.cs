using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class RenPySdkServiceTests
{
    [Fact]
    public async Task Execute_使用独立参数数组并返回Fake进程结果()
    {
        var process = new FakeProcessRunner();
        var service = new RenPySdkService(new InMemoryFileSystem(), process);
        var request = new SdkTranslationRequest(
            "C:\\project", "schinese", new SdkInstallation("C:\\tools", "C:\\tools\\renpy.exe", "8.5.2"),
            false, true, false, true);

        var result = await service.ExecuteAsync(request, new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(["C:\\project", "translate", "schinese", "--empty", "--no-todo"], process.LastPlan?.Arguments);
        Assert.Equal("1", process.LastPlan?.Environment["PYTHONUTF8"]);
        Assert.True(process.LastPlan?.Environment.ContainsKey("SYSTEMROOT"));
        Assert.True(process.LastPlan?.Environment.ContainsKey("windir"));
        Assert.True(process.LastPlan?.Environment.ContainsKey("TEMP"));
        Assert.True(process.LastPlan?.Environment.ContainsKey("TMP"));
        Assert.Equal($"C:\\tools{Path.PathSeparator}C:\\tools\\lib\\py3-windows-x86_64", process.LastPlan?.Environment["PATH"]);
        Assert.DoesNotContain(process.LastPlan!.Environment.Keys,
            key => key.Contains("PROXY", StringComparison.OrdinalIgnoreCase) || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Discover_优先识别用户指定Sdk且不依赖固定盘符()
    {
        var service = new RenPySdkService(new InMemoryFileSystem(), new FakeProcessRunner());

        var result = await service.DiscoverAsync(new SdkDiscoveryRequest("C:\\tools"),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        var installation = Assert.Single(result.Value!);
        Assert.Equal("C:\\tools\\renpy.exe", installation.ExecutablePath);
    }

    [Fact]
    public async Task Execute_取消完整传播到进程适配器()
    {
        var process = new FakeProcessRunner();
        var service = new RenPySdkService(new InMemoryFileSystem(), process);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await service.ExecuteAsync(new SdkTranslationRequest(
            "C:\\project", "schinese", new SdkInstallation("C:\\tools", "C:\\tools\\renpy.exe", "8.5.2"),
            true, false, false, false), new Progress<ToolOperationProgress>(), cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.True(process.ObservedCancellation);
    }

    [Fact]
    public async Task Execute_选择Game目录时向Sdk传递项目父目录()
    {
        var process = new FakeProcessRunner();
        var service = new RenPySdkService(new InMemoryFileSystem("C:\\project\\game"), process);
        var request = new SdkTranslationRequest(
            "C:\\project\\game", "schinese", new SdkInstallation("C:\\tools", "C:\\tools\\renpy.exe", "8.5.2"),
            false, false, false, false);

        var result = await service.ExecuteAsync(request, new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal("C:\\project", process.LastPlan?.Arguments[0]);
        Assert.Equal(["C:\\project", "translate", "schinese"], process.LastPlan?.Arguments);
    }

    private sealed class FakeProcessRunner : IProcessRunnerService
    {
        public ProcessExecutionPlan? LastPlan { get; private set; }
        public bool ObservedCancellation { get; private set; }

        public Task<OperationResult<ProcessExecutionSummary>> ExecuteAsync(
            ProcessExecutionPlan request,
            IProgress<ToolOperationProgress> progress,
            CancellationToken cancellationToken)
        {
            LastPlan = request;
            ObservedCancellation = cancellationToken.IsCancellationRequested;
            return Task.FromResult(ObservedCancellation
                ? OperationResult<ProcessExecutionSummary>.Cancelled(new Diagnostic(DiagnosticSeverity.Info, "PROCESS_CANCELLED", "cancelled"))
                : OperationResult<ProcessExecutionSummary>.Success(new ProcessExecutionSummary(0, "ok", "", TimeSpan.Zero, false)));
        }
    }
}
