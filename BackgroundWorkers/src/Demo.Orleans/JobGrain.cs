namespace OrleansSample;

/// <summary>
/// Grain that submits a job to the <see cref="IRequestPool"/> and then
/// <em>deactivates</em> — it does not hold an activation while the background
/// work runs.
///
/// Flow:
/// <list type="number">
///   <item>Client calls <see cref="SubmitAsync"/> → grain enqueues work in the pool,
///         captures <c>this.AsReference&lt;IJobCompletionObserver&gt;()</c> and
///         <c>this.AsReference&lt;IJobProgressObserver&gt;()</c> in the callback closure,
///         then calls <see cref="Grain.DeactivateOnIdle"/>.</item>
///   <item>The request-pool worker completes the job on a thread-pool thread and
///         calls <see cref="IJobCompletionObserver.OnCompleted"/>,
///         <see cref="IJobCompletionObserver.OnCanceled"/>, or
///         <see cref="IJobCompletionObserver.OnFaulted"/> on the captured reference.</item>
///   <item>Orleans routes the observer call through the grain scheduler, re-activating
///         this grain if necessary, before running the completion turn.</item>
/// </list>
///
/// <see cref="IJobTracker"/> is a process-scoped singleton that survives
/// deactivation/re-activation cycles.
/// </summary>
public sealed partial class JobGrain(
    IRequestPool pool,
    IRequestPoolMonitor monitor,
    IJobTracker tracker,
    ILogger<JobGrain> logger) : Grain, IJobGrain, IJobCompletionObserver, IJobProgressObserver
{
    // -------------------------------------------------------------------------
    // IJobGrain
    // -------------------------------------------------------------------------

    public async Task SubmitAsync(IJobRequest request)
    {
        var jobId = this.GetPrimaryKeyString();

        // Guard against re-submission while already running.
        var current = tracker.GetStatus(jobId);
        if (current is JobStatus.Processing)
        {
            LogAlreadySubmitted(jobId, current);
            return;
        }

        // Set Processing *before* EnqueueAsync so that if the handler completes
        // extremely fast the [AlwaysInterleave] observer turn cannot fire and
        // then be overwritten back to Processing by this turn.
        tracker.SetStatus(jobId, JobStatus.Processing);
        tracker.ClearProgress(jobId);

        var completionRef = this.AsReference<IJobCompletionObserver>();
        var progressRef = this.AsReference<IJobProgressObserver>();

        RequestProgressReporter progressReporter = (pct, msg, delta) =>
            progressRef.OnProgress(jobId, new JobProgressUpdate(pct, msg, delta as JobProgressDelta));

        // The mediator resolves handlers by the *static* generic type argument of RequestContext<TData>.
        // Because handlers are registered for the concrete types (JobRequest, BatchJobRequest,
        // ScheduledJobRequest) — not IJobRequest — we must create the strongly-typed context here.
        RequestContext context = request switch
        {
            JobRequest jr => new RequestContext<JobRequest>(jobId, jr, Priority: jr.Priority, OnProgress: progressReporter)
            {
                PartitionKey = jobId
            },
            BatchJobRequest br => new RequestContext<BatchJobRequest>(jobId, br, Priority: br.Priority, OnProgress: progressReporter)
            {
                PartitionKey = jobId
            },
            ScheduledJobRequest sr => new RequestContext<ScheduledJobRequest>(jobId, sr, Priority: sr.Priority, OnProgress: progressReporter)
            {
                PartitionKey = jobId
            },
            _ => throw new NotSupportedException($"No handler registered for job request type '{request.GetType().Name}'."),
        };

        await pool.EnqueueAsync(context, async result =>
        {
            // Fire-and-forget via Orleans messaging. Does not block the pool worker.
            if (result.Error is OperationCanceledException)       // also matches TaskCanceledException
                await completionRef.OnCanceled(jobId);
            else if (result.Error is not null)
                await completionRef.OnFaulted(jobId, result.Error);
            else
                await completionRef.OnCompleted(jobId, result.TypedOutput as JobOutput ?? new TextJobOutput(result.Output ?? string.Empty));
        });

        LogJobEnqueued(jobId);

        // Release this activation. The pool runs the job; the completion observer
        // will re-activate the grain when the work is done.
        DeactivateOnIdle();
    }

    public Task<JobStatus> GetStatusAsync() =>
        Task.FromResult(tracker.GetStatus(this.GetPrimaryKeyString()));

    public Task<JobProgressSnapshot?> GetProgressAsync() =>
        Task.FromResult(tracker.GetProgress(this.GetPrimaryKeyString()));

    /// <inheritdoc/>
    public Task<bool> TryCancelAsync()
    {
        var jobId = this.GetPrimaryKeyString();
        var current = tracker.GetStatus(jobId);

        if (current is not (JobStatus.Pending or JobStatus.Processing))
        {
            LogCancellationSkipped(jobId, current);
            return Task.FromResult(false);
        }

        bool cancelled = monitor.TryCancelRequest(jobId);

        if (cancelled)
        {
            tracker.SetStatus(jobId, JobStatus.Cancelled);
            LogJobCancelledWhileQueued(jobId);
        }
        else
        {
            LogJobCancelFailed(jobId);
        }

        return Task.FromResult(cancelled);
    }

    // -------------------------------------------------------------------------
    // IJobCompletionObserver + IJobProgressObserver — called via Orleans messaging
    // -------------------------------------------------------------------------

    /// <remarks>
    /// Runs as an Orleans grain turn, so it is safe to access any grain state here.
    /// The grain may have been freshly activated for this call.
    /// </remarks>
    public Task OnProgress(string jobId, JobProgressUpdate progress)
    {
        tracker.SetProgress(jobId, progress.PercentComplete, progress.Message);

        var deltaDetail = progress.Delta switch
        {
            JobStepProgressDelta s => $" [step {s.Step}/{s.TotalSteps}]",
            BatchItemProgressDelta b => $" [item {b.ProcessedCount}/{b.TotalItems}: {b.CurrentItem}]",
            ScheduledJobProgressDelta d => $" [phase: {d.Phase}]",
            null => string.Empty,
            var other => $" [{other.GetType().Name}]",
        };

        LogJobProgress(jobId, progress.PercentComplete, deltaDetail, progress.Message);
        return Task.CompletedTask;
    }

    public Task OnCompleted(string jobId, JobOutput output)
    {
        tracker.SetStatus(jobId, JobStatus.Completed);
        LogJobCompleted(jobId, output.ToString());
        return Task.CompletedTask;
    }

    public Task OnCanceled(string jobId)
    {
        tracker.SetStatus(jobId, JobStatus.Cancelled);
        LogJobCancelled(jobId);
        return Task.CompletedTask;
    }

    public Task OnFaulted(string jobId, Exception exception)
    {
        tracker.SetStatus(jobId, JobStatus.Failed);
        LogJobFailed(jobId, exception.Message);
        return Task.CompletedTask;
    }

    [LoggerMessage(1, LogLevel.Warning, "Job {JobId} is already {Status}; ignoring re-submit")]
    private partial void LogAlreadySubmitted(string jobId, JobStatus status);

    [LoggerMessage(2, LogLevel.Information, "Job {JobId} enqueued; grain will deactivate and re-activate on completion")]
    private partial void LogJobEnqueued(string jobId);

    [LoggerMessage(3, LogLevel.Debug, "Job {JobId} is {Status}; cancellation skipped")]
    private partial void LogCancellationSkipped(string jobId, JobStatus status);

    [LoggerMessage(4, LogLevel.Information, "Job {JobId} cancelled while queued")]
    private partial void LogJobCancelledWhileQueued(string jobId);

    [LoggerMessage(5, LogLevel.Warning, "Job {JobId} could not be cancelled — already dispatched or completed")]
    private partial void LogJobCancelFailed(string jobId);

    [LoggerMessage(6, LogLevel.Debug, "Job {JobId} progress: {Percent}%{Delta} — {Message}")]
    private partial void LogJobProgress(string jobId, int percent, string delta, string? message);

    [LoggerMessage(7, LogLevel.Information, "Job {JobId} completed (observer re-activated grain): {Output}")]
    private partial void LogJobCompleted(string jobId, string? output);

    [LoggerMessage(8, LogLevel.Information, "Job {JobId} cancelled")]
    private partial void LogJobCancelled(string jobId);

    [LoggerMessage(9, LogLevel.Error, "Job {JobId} failed (observer re-activated grain): {Error}")]
    private partial void LogJobFailed(string jobId, string? error);
}
