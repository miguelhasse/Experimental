namespace OrleansSample;

/// <summary>
/// Worker grain that submits batch items to the <see cref="IRequestPool"/>
/// following the same pool-enqueue pattern as <see cref="JobGrain"/>.
///
/// <para>
/// A single activation handles all items round-robined to it for one batch
/// (up to <c>itemCount / workerCount</c> items). It does not call
/// <see cref="Grain.DeactivateOnIdle"/> after each item — doing so with a parallel
/// fan-out would race with subsequent items queued for the same activation.
/// Orleans will naturally deactivate the grain once it becomes idle after the
/// last item's pool callback fires.
/// </para>
/// </summary>
public sealed partial class BatchWorkerGrain(
    IRequestPool pool,
    IRequestPoolMonitor monitor,
    IJobTracker tracker,
    ILogger<BatchWorkerGrain> logger) : Grain, IBatchWorkerGrain
{
    public async Task SubmitItemAsync(
        string itemKey,
        string batchId,
        int itemIndex,
        string data,
        IJobCompletionObserver completionObserver,
        IJobProgressObserver progressObserver,
        IJobDataObserver? dataObserver)
    {
        var workerKey = this.GetPrimaryKeyString();
        using var activity = BatchGrainTelemetry.StartSubmitItemActivity(workerKey, batchId, itemIndex);

        var current = tracker.GetStatus(itemKey);
        if (current is JobStatus.Processing)
        {
            LogAlreadySubmitted(itemKey, current);
            return;
        }

        // Set Processing *before* EnqueueAsync so that if the handler completes
        // extremely fast the observer turn cannot fire and overwrite back to Processing.
        tracker.SetStatus(itemKey, JobStatus.Processing);
        tracker.ClearProgress(itemKey);

        RequestProgressReporter progressReporter = (pct, msg, delta) =>
            progressObserver.OnProgress(itemKey, new JobProgressUpdate(pct, msg, delta as JobProgressDelta));

        await pool.EnqueueAsync(
            new RequestContext<BatchWorkerItemRequest>(
                itemKey,
                new BatchWorkerItemRequest(batchId, itemIndex, data),
                Priority: RequestPriority.Normal,
                OnProgress: progressReporter)
            { PartitionKey = batchId },
            async result =>
            {
                if (result.Error is OperationCanceledException)
                {
                    tracker.SetStatus(itemKey, JobStatus.Cancelled);
                    await completionObserver.OnCanceled(itemKey);
                }
                else if (result.Error is not null)
                {
                    tracker.SetStatus(itemKey, JobStatus.Failed);
                    await completionObserver.OnFaulted(itemKey, result.Error);
                }
                else
                {
                    tracker.SetStatus(itemKey, JobStatus.Completed);

                    if (dataObserver is not null)
                    {
                        var payload = new BatchItemResultPayload(itemIndex, workerKey, result.Output ?? "");
                        await dataObserver.OnDataReceived(itemKey, payload);
                    }

                    await completionObserver.OnCompleted(itemKey,
                        result.TypedOutput as BatchItemOutput ?? new BatchItemOutput(result.Output ?? ""));
                }
            });

        LogItemEnqueued(itemKey, workerKey);
    }

    public Task<bool> CancelAsync()
    {
        var workerKey = this.GetPrimaryKeyString();
        // The worker's own item key equals its grain key when a single item is round-robined to it,
        // but the coordinator may assign multiple items per worker. Enumerate all tracker entries
        // whose key starts with "{batchId}:item-" where batchId is the prefix of the worker key.
        // Since the tracker is process-scoped we use a naming convention:
        // itemKey == "{batchId}:item-{i}" and workerKey == "{batchId}:worker-{wi}".
        // Extract batchId and cancel every item still queued under it that belongs to this worker.
        var colonIdx = workerKey.LastIndexOf(":worker-", StringComparison.Ordinal);
        if (colonIdx < 0)
        {
            LogWorkerCancelSkipped(workerKey);
            return Task.FromResult(false);
        }

        var batchId = workerKey[..colonIdx];
        var workerIndex = int.TryParse(workerKey[(colonIdx + ":worker-".Length)..], out var wi) ? wi : -1;
        var meta = tracker.GetOutput(batchId) as BatchMetaOutput;

        if (meta is null || workerIndex < 0)
        {
            LogWorkerCancelSkipped(workerKey);
            return Task.FromResult(false);
        }

        bool anyCancelled = false;

        for (int i = workerIndex; i < meta.ItemCount; i += meta.WorkerCount)
        {
            var itemKey = $"{batchId}:item-{i}";
            var status = tracker.GetStatus(itemKey);

            if (status is not (JobStatus.Pending or JobStatus.Processing))
                continue;

            if (monitor.TryCancelRequest(itemKey))
            {
                tracker.SetStatus(itemKey, JobStatus.Cancelled);
                anyCancelled = true;
            }
        }

        LogWorkerCancelResult(workerKey, anyCancelled);
        return Task.FromResult(anyCancelled);
    }

    [LoggerMessage(1, LogLevel.Warning, "Item {ItemKey} is already {Status}; ignoring re-submit")]
    private partial void LogAlreadySubmitted(string itemKey, JobStatus status);

    [LoggerMessage(2, LogLevel.Debug, "Item {ItemKey} enqueued by worker {WorkerKey}; grain will deactivate")]
    private partial void LogItemEnqueued(string itemKey, string workerKey);

    [LoggerMessage(3, LogLevel.Debug, "Worker {WorkerKey}: CancelAsync skipped — no batch metadata or invalid key")]
    private partial void LogWorkerCancelSkipped(string workerKey);

    [LoggerMessage(4, LogLevel.Information, "Worker {WorkerKey}: CancelAsync result — cancelled={Cancelled}")]
    private partial void LogWorkerCancelResult(string workerKey, bool cancelled);
}
