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
    bool KillEntireProcessTree = true,
    int MaxCapturedOutputCharacters = 1_000_000);

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
        var stdout = new BoundedLineBuffer(request.MaxCapturedOutputCharacters);
        var stderr = new BoundedLineBuffer(request.MaxCapturedOutputCharacters);
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
            // 外部工具只能看到执行计划明确允许的变量，避免继承令牌、代理和用户 PATH。
            startInfo.Environment.Clear();
            foreach (var (key, value) in request.Environment)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0'))
                    return OperationResult<ProcessExecutionSummary>.Failure(ProcessDiagnostic("PROCESS_ENVIRONMENT_INVALID", "外部进程环境变量名称无效。"));
                if (value is not null) startInfo.Environment[key] = value;
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            progress.Report(ToolOperationProgress.Create(ToolOperationStage.StartingProcess, $"启动 {Path.GetFileName(request.Executable.FullPath)}"));
            if (!process.Start())
            {
                return OperationResult<ProcessExecutionSummary>.Failure(ProcessDiagnostic("PROCESS_START_FAILED", "外部进程未能启动。"));
            }

            async Task DrainAsync(StreamReader reader, BoundedLineBuffer target, ToolLogLevel level)
            {
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    target.Add(line);
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
                        new ProcessExecutionSummary(-1, stdout.Text, stderr.Text, stopwatch.Elapsed, true),
                        [.. diagnostics, ProcessDiagnostic("PROCESS_TIMEOUT", $"外部工具运行超过 {request.Timeout.TotalMinutes:0.#} 分钟，已终止。")]);
                }
                return OperationResult<ProcessExecutionSummary>.Cancelled(ProcessDiagnostic("PROCESS_CANCELLED", "外部工具已取消。", DiagnosticSeverity.Info));
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return OperationResult<ProcessExecutionSummary>.Cancelled(
                    ProcessDiagnostic("PROCESS_CANCELLED", "外部工具已取消。", DiagnosticSeverity.Info));
            }
            stopwatch.Stop();
            if (stdout.WasTruncated || stderr.WasTruncated)
                diagnostics.Enqueue(ProcessDiagnostic("PROCESS_OUTPUT_TRUNCATED", "外部工具输出过多，仅保留最近部分；实时日志未受影响。", DiagnosticSeverity.Warning));
            var summary = new ProcessExecutionSummary(process.ExitCode, stdout.Text, stderr.Text, stopwatch.Elapsed, false);
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
            if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow() &&
                process.WaitForExit((int)Math.Max(0, plan.CancellationGracePeriod.TotalMilliseconds))) return;
            process.Kill(plan.KillEntireProcessTree);
            process.WaitForExit();
        }
        catch { }
    }
    private static Diagnostic ProcessDiagnostic(string code, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        new(severity, code, message, Category: DiagnosticCategory.Process);

    private sealed class BoundedLineBuffer
    {
        private readonly int _limit;
        private readonly Queue<string> _lines = new();
        private readonly object _gate = new();
        private int _length;

        public BoundedLineBuffer(int limit) => _limit = Math.Max(4096, limit);
        public bool WasTruncated { get; private set; }
        public string Text
        {
            get
            {
                lock (_gate) return string.Join(Environment.NewLine, _lines);
            }
        }

        public void Add(string line)
        {
            lock (_gate)
            {
                _lines.Enqueue(line);
                _length += line.Length + Environment.NewLine.Length;
                while (_length > _limit && _lines.Count > 1)
                {
                    var removed = _lines.Dequeue();
                    _length -= removed.Length + Environment.NewLine.Length;
                    WasTruncated = true;
                }
            }
        }
    }
}
