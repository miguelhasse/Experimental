namespace OrleansSample;

/// <summary>
/// Grain that manages a report through three independent, idempotent lifecycle operations:
/// generate, review, and publish.
///
/// <para>
/// Each of the three methods (<see cref="GenerateAsync"/>, <see cref="ReviewAsync"/>,
/// <see cref="PublishAsync"/>) dispatches exactly one queued request to <see cref="IRequestPool"/>,
/// then deactivates. The grain is re-activated by Orleans when a completion callback arrives.
/// </para>
///
/// <para><strong>Idempotency</strong>: if an operation is already
/// <see cref="JobStatus.Pending"/>, <see cref="JobStatus.Processing"/>, or
/// <see cref="JobStatus.Completed"/>, calling its method again is a no-op that returns
/// the current status. <see cref="JobStatus.Unknown"/> and <see cref="JobStatus.Failed"/>
/// trigger a new dispatch (first run or retry).
/// </para>
///
/// <para><strong>Tracker keys</strong>: each operation is tracked independently under
/// <c>"{reportId}:generate"</c>, <c>"{reportId}:review"</c>, and <c>"{reportId}:publish"</c>
/// in the process-scoped <see cref="IJobTracker"/>.</para>
/// </summary>
public sealed partial class ReportGrain(
    IRequestPool pool,
    IRequestPoolMonitor monitor,
    IJobTracker tracker,
    ILogger<ReportGrain> logger) : Grain, IReportGrain, IJobCompletionObserver
{
    private const string Generate = "generate";
    private const string Review = "review";
    private const string Publish = "publish";

    // -------------------------------------------------------------------------
    // IReportGrain
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<JobStatus> GenerateAsync() =>
        DispatchOperationAsync<GenerateReportRequest>(
            Generate,
            reportId => new GenerateReportRequest(reportId));

    /// <inheritdoc/>
    public Task<JobStatus> ReviewAsync() =>
        DispatchOperationAsync<ReviewReportRequest>(
            Review,
            reportId => new ReviewReportRequest(reportId));

    /// <inheritdoc/>
    public Task<JobStatus> PublishAsync() =>
        DispatchOperationAsync<PublishReportRequest>(
            Publish,
            reportId => new PublishReportRequest(reportId));

    /// <inheritdoc/>
    public Task<ReportSummary> GetSummaryAsync()
    {
        var reportId = this.GetPrimaryKeyString();
        return Task.FromResult(new ReportSummary(
            reportId,
            tracker.GetStatus(OperationKey(reportId, Generate)),
            tracker.GetStatus(OperationKey(reportId, Review)),
            tracker.GetStatus(OperationKey(reportId, Publish)),
            tracker.GetProgress(OperationKey(reportId, Generate)),
            tracker.GetProgress(OperationKey(reportId, Review)),
            tracker.GetProgress(OperationKey(reportId, Publish))));
    }

    /// <inheritdoc/>
    public Task<bool> CancelAsync()
    {
        var reportId = this.GetPrimaryKeyString();
        var cancelledCount = 0;

        foreach (var operation in new[] { Generate, Review, Publish })
        {
            var opKey = OperationKey(reportId, operation);
            var status = tracker.GetStatus(opKey);

            if (status is not (JobStatus.Pending or JobStatus.Processing))
                continue;

            if (monitor.TryCancelRequest(opKey))
            {
                tracker.SetStatus(opKey, JobStatus.Cancelled);
                cancelledCount++;
            }
        }

        if (cancelledCount > 0)
            LogCancelSucceeded(reportId, cancelledCount);
        else
            LogCancelNoOp(reportId);

        return Task.FromResult(cancelledCount > 0);
    }

    // -------------------------------------------------------------------------
    // IJobCompletionObserver — called by the request pool via Orleans messaging
    // -------------------------------------------------------------------------

    /// <remarks>
    /// Operation-specific tracker writes and logging are committed in the pool callback
    /// closure (thread-safe via <see cref="IJobTracker"/>'s <c>ConcurrentDictionary</c>).
    /// These observer methods handle cross-cutting concerns at the report level.
    /// </remarks>
    public Task OnCompleted(string jobId, JobOutput output)
    {
        LogOperationCompleted(this.GetPrimaryKeyString(), output.ToString());
        return Task.CompletedTask;
    }

    public Task OnCanceled(string jobId)
    {
        LogOperationCancelled(this.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public Task OnFaulted(string jobId, Exception exception)
    {
        LogOperationFailed(this.GetPrimaryKeyString(), exception.Message);
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Shared dispatch helper
    // -------------------------------------------------------------------------

    private async Task<JobStatus> DispatchOperationAsync<TRequest>(
        string operation,
        Func<string, TRequest> requestFactory)
        where TRequest : notnull, IJobRequest
    {
        var reportId = this.GetPrimaryKeyString();
        var opKey = OperationKey(reportId, operation);
        var current = tracker.GetStatus(opKey);

        if (current is JobStatus.Processing or JobStatus.Completed)
        {
            LogOperationSkipped(reportId, operation, current);
            return current;
        }

        var request = requestFactory(reportId);
        var completionRef = this.AsReference<IJobCompletionObserver>();

        RequestProgressReporter progressReporter = (pct, msg, _) =>
            tracker.SetProgress(opKey, pct, msg);

        // Set Processing *before* EnqueueAsync so that if the handler completes
        // extremely fast the [AlwaysInterleave] observer turn cannot fire and
        // then be overwritten back to Processing by this turn.
        tracker.SetStatus(opKey, JobStatus.Processing);
        tracker.ClearProgress(opKey);

        await pool.EnqueueAsync(
            new RequestContext<TRequest>(opKey, request, Priority: request.Priority, OnProgress: progressReporter),
            async result =>
            {
                // Operation-specific status committed in closure (ConcurrentDictionary is thread-safe).
                if (result.Error is OperationCanceledException)
                {
                    tracker.SetStatus(opKey, JobStatus.Cancelled);
                    LogOperationCancelledWithOp(reportId, operation);
                    await completionRef.OnCanceled(opKey);
                }
                else if (result.Error is not null)
                {
                    tracker.SetStatus(opKey, JobStatus.Failed);
                    LogOperationFailedWithOp(reportId, operation, result.Error.Message);
                    await completionRef.OnFaulted(opKey, result.Error);
                }
                else
                {
                    tracker.SetStatus(opKey, JobStatus.Completed);
                    var output = result.TypedOutput as JobOutput ?? new TextJobOutput(result.Output ?? string.Empty);
                    LogOperationCompletedWithOp(reportId, operation, output.ToString());
                    await completionRef.OnCompleted(opKey, output);
                }
            });

        LogOperationEnqueued(reportId, operation);

        DeactivateOnIdle();
        return JobStatus.Processing;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string OperationKey(string reportId, string operation) =>
        $"{reportId}:{operation}";

    // ── Logging ───────────────────────────────────────────────────────────────

    [LoggerMessage(1, LogLevel.Information, "Report {ReportId}: '{Operation}' enqueued; grain will deactivate")]
    private partial void LogOperationEnqueued(string reportId, string operation);

    [LoggerMessage(2, LogLevel.Debug, "Report {ReportId}: '{Operation}' already {Status}; skipping dispatch")]
    private partial void LogOperationSkipped(string reportId, string operation, JobStatus status);

    [LoggerMessage(3, LogLevel.Information, "Report {ReportId}: '{Operation}' completed — {Output}")]
    private partial void LogOperationCompletedWithOp(string reportId, string operation, string? output);

    [LoggerMessage(4, LogLevel.Warning, "Report {ReportId}: '{Operation}' cancelled")]
    private partial void LogOperationCancelledWithOp(string reportId, string operation);

    [LoggerMessage(5, LogLevel.Error, "Report {ReportId}: '{Operation}' failed — {Error}")]
    private partial void LogOperationFailedWithOp(string reportId, string operation, string? error);

    [LoggerMessage(6, LogLevel.Information, "Report {ReportId}: an operation completed — {Output}")]
    private partial void LogOperationCompleted(string reportId, string? output);

    [LoggerMessage(7, LogLevel.Warning, "Report {ReportId}: an operation was cancelled")]
    private partial void LogOperationCancelled(string reportId);

    [LoggerMessage(8, LogLevel.Error, "Report {ReportId}: an operation failed — {Error}")]
    private partial void LogOperationFailed(string reportId, string? error);

    [LoggerMessage(9, LogLevel.Information, "Report {ReportId}: {CancelledCount} operation(s) cancelled")]
    private partial void LogCancelSucceeded(string reportId, int cancelledCount);

    [LoggerMessage(10, LogLevel.Debug, "Report {ReportId}: no operations in a cancellable state")]
    private partial void LogCancelNoOp(string reportId);
}
