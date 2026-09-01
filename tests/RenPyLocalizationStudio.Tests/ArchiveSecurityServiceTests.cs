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

    private sealed class UnexpectedProcessRunner : IProcessRunnerService
    {
        public Task<OperationResult<ProcessExecutionSummary>> ExecuteAsync(ProcessExecutionPlan request,
            IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("计划测试不应启动外部进程。");
    }
}
