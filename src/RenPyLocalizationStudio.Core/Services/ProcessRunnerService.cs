using System.Collections.Concurrent;
using System.Diagnostics;

namespace RenPyLocalizationStudio.Core.Services;

public sealed record ProcessExecutionPlan(
    ValidatedExecutablePath Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment,
    TimeSpan Timeout,
    TimeSpan IdleWarningThreshold,
    TimeSpan CancellationGracePeriod,
    bool KillEntireProcessTree = true);

public sealed record ProcessExecutionSummary(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration, bool TimedOut);

public interface IProcessRunnerService : IAsyncOperationService<ProcessExecutionPlan, ProcessExecutionSummary>;

public sealed class ProcessRunnerService : IProcessRunnerService
{
    public async Task<OperationResult<ProcessExecutionSummary>> ExecuteAsync(
        ProcessExecutionPlan request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var diagnostics = new ConcurrentQueue<Diagnostic>();
        var stdout = new List<string>();
        var stderr = new List<string>();
        var gate = new object();
        var lastOutput = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.Executable.FullPath,
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
            foreach (var (key, value) in request.Environment) startInfo.Environment[key] = value;

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.StartingProcess, $"启动 {Path.GetFileName(request.Executable.FullPath)}"));
            if (!process.Start())
            {
                return OperationResult<ProcessExecutionSummary>.Failure(ProcessDiagnostic("PROCESS_START_FAILED", "外部进程未能启动。"));
            }

            async Task DrainAsync(StreamReader reader, List<string> target, ToolLogLevel level)
            {
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    lock (gate) target.Add(line);
                    lastOutput = DateTimeOffset.UtcNow;
                    progress.Report(ToolOperationProgress.Create(ToolOperationStage.RunningProcess, line, level: level));
                }
            }

            var stdoutTask = DrainAsync(process.StandardOutput, stdout, ToolLogLevel.Info);
            var stderrTask = DrainAsync(process.StandardError, stderr, ToolLogLevel.Warning);
            using var timeoutCts = new CancellationTokenSource(request.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var idleWarningIssued = false;

            try
            {
                while (!process.HasExited)
                {
                    await Task.Delay(250, linked.Token).ConfigureAwait(false);
                    if (!idleWarningIssued && DateTimeOffset.UtcNow - lastOutput >= request.IdleWarningThreshold)
                    {
                        idleWarningIssued = true;
                        var warning = ProcessDiagnostic("PROCESS_IDLE", "外部工具长时间没有输出，可能已挂起。", DiagnosticSeverity.Warning);
                        diagnostics.Enqueue(warning);
                        progress.Report(ToolOperationProgress.Create(ToolOperationStage.RunningProcess, warning.Message, level: ToolLogLevel.Warning));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                TryStop(process, request);
                await Task.WhenAll(IgnoreCancellation(stdoutTask), IgnoreCancellation(stderrTask)).ConfigureAwait(false);
                stopwatch.Stop();
                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return new OperationResult<ProcessExecutionSummary>(OperationStatus.Failed,
                        new ProcessExecutionSummary(-1, string.Join(Environment.NewLine, stdout), string.Join(Environment.NewLine, stderr), stopwatch.Elapsed, true),
                        [.. diagnostics, ProcessDiagnostic("PROCESS_TIMEOUT", $"外部工具运行超过 {request.Timeout.TotalMinutes:0.#} 分钟，已终止。")]);
                }
                return OperationResult<ProcessExecutionSummary>.Cancelled(ProcessDiagnostic("PROCESS_CANCELLED", "外部工具已取消。", DiagnosticSeverity.Info));
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            stopwatch.Stop();
            var summary = new ProcessExecutionSummary(process.ExitCode, string.Join(Environment.NewLine, stdout), string.Join(Environment.NewLine, stderr), stopwatch.Elapsed, false);
            if (process.ExitCode != 0)
            {
                return new OperationResult<ProcessExecutionSummary>(OperationStatus.Failed, summary,
                    [.. diagnostics, ProcessDiagnostic("PROCESS_NON_ZERO_EXIT", $"外部工具返回退出码 {process.ExitCode}。")]);
            }
            return OperationResult<ProcessExecutionSummary>.Success(summary, diagnostics.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return OperationResult<ProcessExecutionSummary>.Failure(ProcessDiagnostic("PROCESS_FAILED", ex.Message));
        }
    }

    private static async Task IgnoreCancellation(Task task) { try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    private static void TryStop(Process process, ProcessExecutionPlan plan)
    {
        try
        {
            if (process.HasExited) return;
            process.CloseMainWindow();
            if (process.WaitForExit((int)Math.Max(0, plan.CancellationGracePeriod.TotalMilliseconds))) return;
            process.Kill(plan.KillEntireProcessTree);
            process.WaitForExit();
        }
        catch { }
    }
    private static Diagnostic ProcessDiagnostic(string code, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        new(severity, code, message, Category: DiagnosticCategory.Process);
}
