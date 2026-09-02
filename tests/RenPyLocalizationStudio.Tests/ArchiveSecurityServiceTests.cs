using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class ArchiveSecurityServiceTests
{
    [Fact]
    public async Task DecompilePlan_Rpymc使用Rpym扩展并包含状态指纹()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddBytes("game/sample.rpymc", [1, 2, 3]);
        var root = fileSystem.ValidateProjectRoot("C:\\project").Value!;
        var service = new ScriptDecompilerService(fileSystem, new UnexpectedProcessRunner());

        var result = await service.PlanAsync(new ScriptDecompileRequest(root,
                new ValidatedToolPath("C:\\tools\\renpy.exe"), new ValidatedToolPath("C:\\tools\\unrpyc.py"),
                ["game/sample.rpymc"], false), new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("game\\sample.rpym", Assert.Single(result.Value!.Items).OutputRelativePath);
        Assert.Matches("^[0-9A-F]{64}$", result.Value.Fingerprint);
    }

    [Fact]
    public async Task DecompileExecute_拒绝缺少计划指纹的确认()
    {
        var fileSystem = new InMemoryFileSystem();
        var root = fileSystem.ValidateProjectRoot("C:\\project").Value!;
        var service = new ScriptDecompilerService(fileSystem, new UnexpectedProcessRunner());

        var result = await service.ExecuteAsync(new ScriptDecompileRequest(root,
                new ValidatedToolPath("C:\\tools\\renpy.exe"), new ValidatedToolPath("C:\\tools\\unrpyc.py"),
                ["game/sample.rpyc"], true), new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DECOMPILE_CONFIRMATION_REQUIRED");
    }

    [Fact]
    public async Task DecompilePlan_无效项仅跳过且保留其余安全项()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddBytes("game/safe.rpyc", [1, 2, 3]);
        var root = fileSystem.ValidateProjectRoot("C:\\project").Value!;
        var service = new ScriptDecompilerService(fileSystem, new UnexpectedProcessRunner());

        var result = await service.PlanAsync(new ScriptDecompileRequest(root,
                new ValidatedToolPath("C:\\tools\\renpy.exe"), new ValidatedToolPath("C:\\tools\\unrpyc.py"),
                ["../escape.rpyc", "game/safe.rpyc"], false),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Equal(OperationStatus.SucceededWithWarnings, result.Status);
        Assert.Equal(ToolPlanDisposition.Error, result.Value!.Items[0].Disposition);
        Assert.Equal(ToolPlanDisposition.Ready, result.Value.Items[1].Disposition);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code.StartsWith("DECOMPILE_SKIPPED_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArchivePlan_解析完整逐项计划并生成确认指纹()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddBytes("game/archive.rpa", [1, 2, 3]);
        var root = fileSystem.ValidateProjectRoot("C:\\project").Value!;
        const string archiveHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var runner = new PlanProcessRunner(string.Join('\n',
            $"ARCHIVE\t{archiveHash}",
            "PLAN\tWRITE\tdir/a.txt\tC:\\project\\game\\dir\\a.txt\t3",
            "SUMMARY\t1\tBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"));
        var service = new ArchiveExtractionService(fileSystem, runner);

        var result = await service.PlanAsync(new ArchiveExtractionRequest(root,
                new ValidatedToolPath("C:\\tools\\renpy.exe"), new ValidatedToolPath("C:\\tools\\safe_rpa_extract.py"),
                ["game/archive.rpa"], "game", false),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(ToolPlanDisposition.Ready, item.Disposition);
        Assert.Equal("game\\dir\\a.txt", item.OutputRelativePath);
        Assert.Equal(archiveHash, result.Value.ArchiveSha256!["game/archive.rpa"]);
        Assert.Matches("^[0-9A-F]{64}$", result.Value.Fingerprint);
    }

    [Fact]
    public async Task ArchivePlan_截断输出不会保留半条目()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddBytes("game/archive.rpa", [1, 2, 3]);
        var root = fileSystem.ValidateProjectRoot("C:\\project").Value!;
        var runner = new PlanProcessRunner(string.Join('\n',
            "ARCHIVE\tAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "PLAN\tWRITE\tdir/a.txt\tC:\\project\\game\\dir\\a.txt\t3"));
        var service = new ArchiveExtractionService(fileSystem, runner);

        var result = await service.PlanAsync(new ArchiveExtractionRequest(root,
                new ValidatedToolPath("C:\\tools\\renpy.exe"), new ValidatedToolPath("C:\\tools\\safe_rpa_extract.py"),
                ["game/archive.rpa"], "game", false),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Equal(OperationStatus.SucceededWithWarnings, result.Status);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(ToolPlanDisposition.Error, item.Disposition);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ARCHIVE_PLAN_TRUNCATED");
    }

    private sealed class UnexpectedProcessRunner : IProcessRunnerService
    {
        public Task<OperationResult<ProcessExecutionSummary>> ExecuteAsync(ProcessExecutionPlan request,
            IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("计划测试不应启动外部进程。");
    }

    private sealed class PlanProcessRunner(string output) : IProcessRunnerService
    {
        public Task<OperationResult<ProcessExecutionSummary>> ExecuteAsync(ProcessExecutionPlan request,
            IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<ProcessExecutionSummary>.Success(
                new ProcessExecutionSummary(0, output, string.Empty, TimeSpan.Zero, false)));
    }
}
