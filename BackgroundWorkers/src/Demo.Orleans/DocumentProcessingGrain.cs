namespace OrleansSample;

/// <summary>
/// Grain that executes a three-step sequential document-processing pipeline:
/// <b>Extract → Transform → Index</b>.
///
/// <para>
/// <see cref="RunAsync"/> dispatches step 1 only. Each step's
/// <see cref="IJobCompletionObserver.OnCompleted"/> callback stores the typed
/// <see cref="JobOutput"/> in <see cref="IJobTracker"/> and automatically enqueues
/// the next step. The concrete output type (<see cref="ExtractedContentOutput"/>,
/// <see cref="TransformedContentOutput"/>, <see cref="IndexedContentOutput"/>)
/// determines which step completed and drives the sequential chain.
/// </para>
///
/// <para><b>Idempotency:</b> if step 1 is already <c>Processing</c>, <see cref="RunAsync"/>
/// returns immediately. Any other prior state (Completed, Failed, Cancelled, Unknown)
/// triggers a full restart from step 1, clearing all three steps.</para>
///
/// <para><b>Tracker keys:</b> <c>"{pipelineId}:step1"</c>, <c>"{pipelineId}:step2"</c>,
/// <c>"{pipelineId}:step3"</c>. Typed outputs are stored under the same keys via
/// <see cref="IJobTracker.SetOutput"/>.</para>
/// </summary>
public sealed partial class DocumentProcessingGrain(
    IRequestPool pool,
    IRequestPoolMonitor monitor,
    IJobTracker tracker,
    ILogger<DocumentProcessingGrain> logger) : Grain, IDocumentProcessingGrain, IJobCompletionObserver, IJobProgressObserver
{
    private const string Step1 = "step1";
    private const string Step2 = "step2";
    private const string Step3 = "step3";

    // -------------------------------------------------------------------------
    // IDocumentProcessingGrain
    // -------------------------------------------------------------------------

    public async Task<JobStatus> RunAsync()
    {
        var pipelineId = this.GetPrimaryKeyString();
        var step1Key = StepKey(pipelineId, Step1);

        // Block restart if ANY step is currently running. Checking only step 1
        // would allow restart while step 2 or 3 is in the pool — the stale
        // OnCompleted callback would then find null outputs and silently
        // abort the chain of the newly-started run.
        foreach (var step in new[] { Step1, Step2, Step3 })
        {
            if (tracker.GetStatus(StepKey(pipelineId, step)) is JobStatus.Processing)
            {
                var current = tracker.GetStatus(step1Key);
                LogAlreadyRunning(pipelineId, current);
                return current;
            }
        }

        // Reset all three steps to start a fresh run.
        foreach (var step in new[] { Step1, Step2, Step3 })
        {
            var key = StepKey(pipelineId, step);
            tracker.SetStatus(key, JobStatus.Unknown);
            tracker.ClearProgress(key);
            tracker.SetOutput(key, null);
        }

        return await DispatchStep1Async(pipelineId);
    }

    public Task<DocumentProcessingSnapshot> GetSummaryAsync()
    {
        var pipelineId = this.GetPrimaryKeyString();
        var k1 = StepKey(pipelineId, Step1);
        var k2 = StepKey(pipelineId, Step2);
        var k3 = StepKey(pipelineId, Step3);

        return Task.FromResult(new DocumentProcessingSnapshot(
            PipelineId: pipelineId,
            Step1Status: tracker.GetStatus(k1),
            Step2Status: tracker.GetStatus(k2),
            Step3Status: tracker.GetStatus(k3),
            Step1Progress: tracker.GetProgress(k1),
            Step2Progress: tracker.GetProgress(k2),
            Step3Progress: tracker.GetProgress(k3),
            Step1Output: tracker.GetOutput(k1) as ExtractedContentOutput,
            Step2Output: tracker.GetOutput(k2) as TransformedContentOutput,
            Step3Output: tracker.GetOutput(k3) as IndexedContentOutput));
    }

    public Task<bool> CancelAsync()
    {
        var pipelineId = this.GetPrimaryKeyString();
        var cancelled = 0;

        foreach (var step in new[] { Step1, Step2, Step3 })
        {
            var stepKey = StepKey(pipelineId, step);
            if (monitor.TryCancelRequest(stepKey))
            {
                tracker.SetStatus(stepKey, JobStatus.Cancelled);
                LogStepCancelledWhileQueued(pipelineId, step);
                cancelled++;
            }
        }

        LogCancelAllSteps(pipelineId, cancelled);
        return Task.FromResult(cancelled > 0);
    }

    // -------------------------------------------------------------------------
    // IJobCompletionObserver + IJobProgressObserver — called via Orleans messaging
    // -------------------------------------------------------------------------

    /// <remarks>
    /// Runs as an Orleans grain turn. The concrete <see cref="JobOutput"/> subtype
    /// determines which pipeline step completed, enabling the sequential step chain.
    /// </remarks>
    public async Task OnCompleted(string jobId, JobOutput output)
    {
        var pipelineId = this.GetPrimaryKeyString();

        // jobId is the stepKey: "{pipelineId}:step1|step2|step3"
        var step = jobId == StepKey(pipelineId, Step1) ? Step1
                 : jobId == StepKey(pipelineId, Step2) ? Step2
                 : jobId == StepKey(pipelineId, Step3) ? Step3
                 : throw new InvalidOperationException($"Unexpected jobId: {jobId}");

        // Defense-in-depth: if the step has been reset to Unknown by a concurrent
        // RunAsync() restart that raced past our idempotency guard (e.g., due to
        // an [AlwaysInterleave] turn), discard this stale callback entirely.
        var stepKey = jobId;
        if (tracker.GetStatus(stepKey) is JobStatus.Unknown)
        {
            LogStaleCallback(pipelineId, step);
            return;
        }

        tracker.SetStatus(stepKey, JobStatus.Completed);
        tracker.SetOutput(stepKey, output);
        LogStepCompleted(pipelineId, step, output.ToString());

        // Chain the next step.
        if (step == Step1) await DispatchStep2Async(pipelineId);
        else if (step == Step2) await DispatchStep3Async(pipelineId);
        // Step3 complete: pipeline is done.
    }

    // Cancel and fault are handled in the dispatch closures (which capture the exact
    // stepKey). These observer methods are no-ops: acting here would require
    // FindProcessingStep() to guess which step failed, which races with RunAsync()
    // restarting and dispatching a new step under [AlwaysInterleave] concurrency.
    public Task OnCanceled(string jobId) => Task.CompletedTask;
    public Task OnFaulted(string jobId, Exception exception) => Task.CompletedTask;

    public Task OnProgress(string jobId, JobProgressUpdate progress)
    {
        tracker.SetProgress(jobId, progress.PercentComplete, progress.Message);
        LogStepProgress(jobId, progress.PercentComplete, progress.Message);
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Dispatch helpers
    // -------------------------------------------------------------------------

    private async Task<JobStatus> DispatchStep1Async(string pipelineId)
    {
        var stepKey = StepKey(pipelineId, Step1);
        var completionRef = this.AsReference<IJobCompletionObserver>();
        var progressRef = this.AsReference<IJobProgressObserver>();

        tracker.ClearProgress(stepKey);
        tracker.SetStatus(stepKey, JobStatus.Processing);

        RequestProgressReporter reporter = (pct, msg, delta) =>
            progressRef.OnProgress(stepKey, new JobProgressUpdate(pct, msg, delta as JobProgressDelta));

        await pool.EnqueueAsync(
            new RequestContext<ExtractContentRequest>(
                stepKey,
                new ExtractContentRequest(pipelineId),
                Priority: RequestPriority.High,
                OnProgress: reporter),
            async result =>
            {
                if (result.Error is OperationCanceledException)
                {
                    tracker.SetStatus(stepKey, JobStatus.Cancelled);
                    LogStepCancelled(pipelineId, Step1);
                    await completionRef.OnCanceled(stepKey);
                }
                else if (result.Error is not null)
                {
                    tracker.SetStatus(stepKey, JobStatus.Failed);
                    LogStepFailed(pipelineId, Step1, result.Error.Message);
                    await completionRef.OnFaulted(stepKey, result.Error);
                }
                else
                {
                    await completionRef.OnCompleted(stepKey,
                        result.TypedOutput as JobOutput
                        ?? new TextJobOutput(result.Output ?? string.Empty));
                }
            });
        DeactivateOnIdle();
        return JobStatus.Processing;
    }

    private async Task DispatchStep2Async(string pipelineId)
    {
        var step1Output = tracker.GetOutput(StepKey(pipelineId, Step1)) as ExtractedContentOutput;
        if (step1Output is null)
        {
            LogMissingOutput(pipelineId, Step1);
            return;
        }

        var stepKey = StepKey(pipelineId, Step2);
        var completionRef = this.AsReference<IJobCompletionObserver>();
        var progressRef = this.AsReference<IJobProgressObserver>();

        tracker.ClearProgress(stepKey);
        tracker.SetStatus(stepKey, JobStatus.Processing);

        RequestProgressReporter reporter = (pct, msg, delta) =>
            progressRef.OnProgress(stepKey, new JobProgressUpdate(pct, msg, delta as JobProgressDelta));

        await pool.EnqueueAsync(
            new RequestContext<TransformContentRequest>(
                stepKey,
                new TransformContentRequest(pipelineId, step1Output.RawText, step1Output.WordCount),
                Priority: RequestPriority.Normal,
                OnProgress: reporter),
            async result =>
            {
                if (result.Error is OperationCanceledException)
                {
                    tracker.SetStatus(stepKey, JobStatus.Cancelled);
                    LogStepCancelled(pipelineId, Step2);
                    await completionRef.OnCanceled(stepKey);
                }
                else if (result.Error is not null)
                {
                    tracker.SetStatus(stepKey, JobStatus.Failed);
                    LogStepFailed(pipelineId, Step2, result.Error.Message);
                    await completionRef.OnFaulted(stepKey, result.Error);
                }
                else
                {
                    await completionRef.OnCompleted(stepKey,
                        result.TypedOutput as JobOutput
                        ?? new TextJobOutput(result.Output ?? string.Empty));
                }
            });

        LogStepEnqueued(pipelineId, Step2);
        DeactivateOnIdle();
    }

    private async Task DispatchStep3Async(string pipelineId)
    {
        var step2Output = tracker.GetOutput(StepKey(pipelineId, Step2)) as TransformedContentOutput;
        if (step2Output is null)
        {
            LogMissingOutput(pipelineId, Step2);
            return;
        }

        var stepKey = StepKey(pipelineId, Step3);
        var completionRef = this.AsReference<IJobCompletionObserver>();
        var progressRef = this.AsReference<IJobProgressObserver>();

        tracker.ClearProgress(stepKey);
        tracker.SetStatus(stepKey, JobStatus.Processing);

        RequestProgressReporter reporter = (pct, msg, delta) =>
            progressRef.OnProgress(stepKey, new JobProgressUpdate(pct, msg, delta as JobProgressDelta));

        await pool.EnqueueAsync(
            new RequestContext<IndexContentRequest>(
                stepKey,
                new IndexContentRequest(pipelineId, step2Output.Keywords, step2Output.SentimentScore),
                Priority: RequestPriority.Normal,
                OnProgress: reporter),
            async result =>
            {
                if (result.Error is OperationCanceledException)
                {
                    tracker.SetStatus(stepKey, JobStatus.Cancelled);
                    LogStepCancelled(pipelineId, Step3);
                    await completionRef.OnCanceled(stepKey);
                }
                else if (result.Error is not null)
                {
                    tracker.SetStatus(stepKey, JobStatus.Failed);
                    LogStepFailed(pipelineId, Step3, result.Error.Message);
                    await completionRef.OnFaulted(stepKey, result.Error);
                }
                else
                {
                    await completionRef.OnCompleted(stepKey,
                        result.TypedOutput as JobOutput
                        ?? new TextJobOutput(result.Output ?? string.Empty));
                }
            });

        LogStepEnqueued(pipelineId, Step3);
        DeactivateOnIdle();
    }

    private static string StepKey(string pipelineId, string step) => $"{pipelineId}:{step}";

    // ── Logging ────────────────────────────────────────────────────────────────

    [LoggerMessage(1, LogLevel.Warning, "Pipeline {PipelineId} is already {Status}; ignoring RunAsync")]
    private partial void LogAlreadyRunning(string pipelineId, JobStatus status);

    [LoggerMessage(2, LogLevel.Information, "Pipeline {PipelineId}: {Step} enqueued; grain will deactivate")]
    private partial void LogStepEnqueued(string pipelineId, string step);

    [LoggerMessage(3, LogLevel.Information, "Pipeline {PipelineId}: {Step} completed — {Output}")]
    private partial void LogStepCompleted(string pipelineId, string step, string? output);

    [LoggerMessage(4, LogLevel.Warning, "Pipeline {PipelineId}: {Step} cancelled")]
    private partial void LogStepCancelled(string pipelineId, string step);

    [LoggerMessage(5, LogLevel.Error, "Pipeline {PipelineId}: {Step} failed — {Error}")]
    private partial void LogStepFailed(string pipelineId, string step, string? error);

    [LoggerMessage(6, LogLevel.Debug, "Pipeline step {StepKey} progress: {Percent}% — {Message}")]
    private partial void LogStepProgress(string stepKey, int percent, string? message);

    [LoggerMessage(7, LogLevel.Error, "Pipeline {PipelineId}: missing output for {Step}; aborting chain")]
    private partial void LogMissingOutput(string pipelineId, string step);

    [LoggerMessage(8, LogLevel.Warning, "Pipeline {PipelineId}: discarding stale OnCompleted callback for {Step} (pipeline was restarted)")]
    private partial void LogStaleCallback(string pipelineId, string step);

    [LoggerMessage(9, LogLevel.Information, "Pipeline {PipelineId}: {Step} cancelled while queued")]
    private partial void LogStepCancelledWhileQueued(string pipelineId, string step);

    [LoggerMessage(10, LogLevel.Information, "Pipeline {PipelineId}: TryCancelAllSteps cancelled {Count} step(s)")]
    private partial void LogCancelAllSteps(string pipelineId, int count);
}
