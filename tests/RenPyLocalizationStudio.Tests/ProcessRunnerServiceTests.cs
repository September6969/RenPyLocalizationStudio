using System.Collections.Concurrent;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class ProcessRunnerServiceTests
{
    [Fact]
    public async Task Execute_并发排空Stdout和Stderr不会死锁()
    {
        var result = await ExecuteAsync("flood", TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Contains("out-999", result.Value?.StandardOutput);
        Assert.Contains("err-999", result.Value?.StandardError);
    }

    [Fact]
    public async Task Execute_非零退出返回结构化诊断()
    {
        var result = await ExecuteAsync("fail", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(17, result.Value?.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PROCESS_NON_ZERO_EXIT");
    }

    [Fact]
    public async Task Execute_取消和超时都会终止进程()
    {
        using var cancellation = new CancellationTokenSource(150);
        var cancelled = await ExecuteAsync("sleep", TimeSpan.FromSeconds(10), cancellation.Token);
        var timedOut = await ExecuteAsync("sleep", TimeSpan.FromMilliseconds(150), CancellationToken.None);

        Assert.Equal(OperationStatus.Cancelled, cancelled.Status);
        Assert.Equal(OperationStatus.Failed, timedOut.Status);
        Assert.True(timedOut.Value?.TimedOut);
        Assert.Contains(timedOut.Diagnostics, diagnostic => diagnostic.Code == "PROCESS_TIMEOUT");
    }

    [Fact]
    public async Task Execute_无输出达到阈值生成警告()
    {
        var progress = new CollectingProgress();
        var result = await ExecuteAsync("idle", TimeSpan.FromSeconds(10), CancellationToken.None, progress, TimeSpan.FromMilliseconds(100));

        Assert.Equal(OperationStatus.SucceededWithWarnings, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PROCESS_IDLE");
        Assert.Contains(progress.Items, item => item.Level == ToolLogLevel.Warning);
    }

    [Fact]
    public async Task Execute_大量输出只保留有界尾部()
    {
        var result = await ExecuteAsync("flood", TimeSpan.FromSeconds(15), CancellationToken.None, maxCapturedOutputCharacters: 4096);

        Assert.Equal(OperationStatus.SucceededWithWarnings, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PROCESS_OUTPUT_TRUNCATED");
        Assert.DoesNotContain("out-0\n", result.Value!.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("out-999", result.Value.StandardOutput);
    }

    [Fact]
    public async Task Execute_只向子进程传递白名单环境变量()
    {
        Environment.SetEnvironmentVariable("RLS_SHOULD_NOT_LEAK", "secret");
        try
        {
            var result = await ExecuteAsync("environment", TimeSpan.FromSeconds(10), CancellationToken.None,
                environment: new Dictionary<string, string?> { ["RLS_ALLOWED"] = "allowed" });

            Assert.True(result.IsSuccess);
            var lines = result.Value!.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(["allowed", "missing-secret"], lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RLS_SHOULD_NOT_LEAK", null);
        }
    }

    private static Task<OperationResult<ProcessExecutionSummary>> ExecuteAsync(
        string mode,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IProgress<ToolOperationProgress>? progress = null,
        TimeSpan? idleWarningThreshold = null,
        int maxCapturedOutputCharacters = 1_000_000,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "C:\\Program Files\\dotnet", "dotnet.exe");
        var helper = Path.Combine(AppContext.BaseDirectory, "RenPyLocalizationStudio.ProcessTestHelper.dll");
        var plan = new ProcessExecutionPlan(
            new ValidatedExecutablePath(dotnet),
            AppContext.BaseDirectory,
            [helper, mode],
            environment ?? new Dictionary<string, string?>(),
            timeout,
            idleWarningThreshold ?? TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(50),
            MaxCapturedOutputCharacters: maxCapturedOutputCharacters);
        return new ProcessRunnerService().ExecuteAsync(plan, progress ?? new CollectingProgress(), cancellationToken);
    }

    private sealed class CollectingProgress : IProgress<ToolOperationProgress>
    {
        public ConcurrentQueue<ToolOperationProgress> Items { get; } = new();
        public void Report(ToolOperationProgress value) => Items.Enqueue(value);
    }
}
