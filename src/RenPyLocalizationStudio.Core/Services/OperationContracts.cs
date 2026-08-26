namespace RenPyLocalizationStudio.Core.Services;

public enum OperationStatus
{
    Succeeded,
    SucceededWithWarnings,
    Failed,
    Cancelled
}

public enum ToolOperationStage
{
    Scanning,
    Planning,
    Reading,
    Writing,
    BackingUp,
    Validating,
    StartingProcess,
    RunningProcess,
    Completed
}

public enum ToolLogLevel { Trace, Info, Warning, Error }

public sealed record ToolOperationProgress(
    ToolOperationStage Stage,
    int CompletedItems,
    int? TotalItems,
    double? Percentage,
    string? RelativePath,
    string Message,
    ToolLogLevel Level,
    DateTimeOffset Timestamp)
{
    public static ToolOperationProgress Create(
        ToolOperationStage stage,
        string message,
        int completed = 0,
        int? total = null,
        string? relativePath = null,
        ToolLogLevel level = ToolLogLevel.Info) =>
        new(stage, completed, total, total > 0 ? completed * 100d / total : null,
            relativePath, message, level, DateTimeOffset.Now);
}

public sealed record OperationResult<T>(
    OperationStatus Status,
    T? Value,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsSuccess => Status is OperationStatus.Succeeded or OperationStatus.SucceededWithWarnings;

    public static OperationResult<T> Success(T value, IReadOnlyList<Diagnostic>? diagnostics = null)
    {
        diagnostics ??= [];
        var status = diagnostics.Any(x => x.Severity == DiagnosticSeverity.Warning)
            ? OperationStatus.SucceededWithWarnings : OperationStatus.Succeeded;
        return new(status, value, diagnostics);
    }

    public static OperationResult<T> Failure(params Diagnostic[] diagnostics) =>
        new(OperationStatus.Failed, default, diagnostics);

    public static OperationResult<T> Cancelled(params Diagnostic[] diagnostics) =>
        new(OperationStatus.Cancelled, default, diagnostics);
}

public interface IAsyncOperationService<in TRequest, TResult>
{
    Task<OperationResult<TResult>> ExecuteAsync(
        TRequest request,
        IProgress<ToolOperationProgress> progress,
        CancellationToken cancellationToken);
}
