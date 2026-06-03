using Orleans.Concurrency;

namespace OrleansSample;

/// <summary>
/// Grain that demonstrates the fan-out/fan-in pattern:
/// <see cref="ProcessBatchAsync"/> fans out by calling <see cref="IBatchWorkerGrain.SubmitItemAsync"/>
/// on one worker grain per item — each worker grain enqueues its own
/// <see cref="BatchWorkerItemRequest"/> into the pool and deactivates.
/// <see cref="GetSummaryAsync"/> fans in by reading all per-item tracker keys and
/// aggregating counts.
///
/// <para><strong>Tracker keys</strong>:
/// <list type="bullet">
///   <item><c>"{batchId}"</c> — overall coordinator status; holds a <see cref="BatchMetaOutput"/>
///         output that records <c>itemCount</c> and <c>workerCount</c>.</item>
///   <item><c>"{batchId}:item-{i}"</c> — per-item status updated by each pool completion callback.</item>
/// </list>
/// </para>
///
/// <para><strong>Worker grain keys</strong>: <c>"{batchId}:worker-{workerIndex}"</c> — each worker
/// grain processes only the items round-robined to it, isolated per batch.</para>
/// </summary>
[Reentrant]
public sealed partial class BatchCoordinatorGrain(
    IRequestPoolMonitor monitor,
    IJobTracker tracker,
    ILogger<BatchCoordinatorGrain> logger) : Grain, IBatchCoordinatorGrain,
    IJobCompletionObserver, IJobProgressObserver, IJobDataObserver
{
    private static string ItemKey(string batchId, int itemIndex) => $"{batchId}:item-{itemIndex}";

    // -------------------------------------------------------------------------
    // IBatchCoordinatorGrain
    // -------------------------------------------------------------------------

    public async Task<BatchCoordinationSummary> ProcessBatchAsync(int itemCount, int workerCount)
    {
        var batchId = this.GetPrimaryKeyString();
        using var activity = BatchGrainTelemetry.StartProcessBatchActivity(batchId, itemCount, workerCount);

        // Guard: serialised by Orleans turn-based concurrency, so this is atomic.
        if (tracker.GetStatus(batchId) is JobStatus.Processing)
        {
            LogAlreadySubmitted(batchId);
            return await GetSummaryAsync();
        }

        // Persist metadata so GetSummaryAsync / TryCancelAsync can reconstruct item keys
        // even after this activation deactivates and a new one is created.
        tracker.SetOutput(batchId, new BatchMetaOutput(itemCount, workerCount));
        tracker.SetStatus(batchId, JobStatus.Processing);

        var completionRef = this.AsReference<IJobCompletionObserver>();
        var progressRef = this.AsReference<IJobProgressObserver>();
        var dataObserverRef = this.AsReference<IJobDataObserver>();

        // Initialise all item tracker entries synchronously before any async fan-out,
        // so GetSummaryAsync polling sees the full item list immediately.
        for (int i = 0; i < itemCount; i++)
        {
            tracker.SetStatus(ItemKey(batchId, i), JobStatus.Pending);
            tracker.ClearProgress(ItemKey(batchId, i));
        }

        // Fan-out in parallel: all worker grains are contacted concurrently.
        var fanOutTasks = Enumerable.Range(0, itemCount).Select(i =>
        {
            var itemKey = ItemKey(batchId, i);
            var workerKey = $"{batchId}:worker-{i % workerCount}";
            var worker = GrainFactory.GetGrain<IBatchWorkerGrain>(workerKey);
            return worker.SubmitItemAsync(
                itemKey, batchId, i,
                data: $"batch-{batchId}:item-{i}",
                completionObserver: completionRef,
                progressObserver: progressRef,
                dataObserver: dataObserverRef);
        });

        // Await the fan-out before returning. Fire-and-forget tasks don't guarantee
        // execution order in Orleans, leading to serialization. This await ensures all
        // items are submitted before ProcessBatchAsync returns and the next batch begins.
        await Task.WhenAll(fanOutTasks);

        LogBatchSubmitted(batchId, itemCount, workerCount);

        // The grain stays alive for polling via GetSummaryAsync() until all items reach
        // terminal state. Deactivation is handled by TryDeactivateIfComplete() in observer
        // callbacks (lines 251-279), which ensures all finalizations are complete.
        return await GetSummaryAsync();
    }

    public Task<BatchCoordinationSummary> GetSummaryAsync()
    {
        var batchId = this.GetPrimaryKeyString();
        var meta = tracker.GetOutput(batchId) as BatchMetaOutput;

        if (meta is null)
        {
            return Task.FromResult(new BatchCoordinationSummary(
                batchId, ItemCount: 0, WorkerCount: 0,
                TotalCompleted: 0, TotalFailed: 0, TotalCancelled: 0,
                WorkerStatuses: [], OverallStatus: tracker.GetStatus(batchId)));
        }

        var itemCount = meta.ItemCount;
        var workerCount = meta.WorkerCount;

        // Per-worker item-processed counters (fan-in).
        var workerCompleted = new int[workerCount];
        int totalCompleted = 0, totalFailed = 0, totalCancelled = 0;

        for (int i = 0; i < itemCount; i++)
        {
            var status = tracker.GetStatus(ItemKey(batchId, i));
            var wi = i % workerCount;

            switch (status)
            {
                case JobStatus.Completed: totalCompleted++; workerCompleted[wi]++; break;
                case JobStatus.Failed: totalFailed++; break;
                case JobStatus.Cancelled: totalCancelled++; break;
            }
        }

        // Derive worker status snapshots.
        var workerStatuses = Enumerable.Range(0, workerCount)
            .Select(wi => new BatchWorkerStatus(
                $"{batchId}:worker-{wi}",
                workerCompleted[wi],
                JobStatus.Unknown))
            .ToList();

        // Derive overall status.
        var batchStatus = tracker.GetStatus(batchId);
        JobStatus overallStatus;

        if (batchStatus is JobStatus.Cancelled)
        {
            overallStatus = JobStatus.Cancelled;
        }
        else
        {
            var done = totalCompleted + totalFailed + totalCancelled;
            if (done == itemCount && itemCount > 0)
            {
                overallStatus = totalFailed > 0 ? JobStatus.Failed
                              : totalCancelled > 0 ? JobStatus.Cancelled
                              : JobStatus.Completed;
            }
            else
            {
                overallStatus = batchStatus; // Processing while work is in flight
            }
        }

        return Task.FromResult(new BatchCoordinationSummary(
            batchId, itemCount, workerCount,
            totalCompleted, totalFailed, totalCancelled,
            workerStatuses, overallStatus));
    }

    public Task<bool> CancelAsync()
    {
        var batchId = this.GetPrimaryKeyString();
        var current = tracker.GetStatus(batchId);

        if (current is not (JobStatus.Pending or JobStatus.Processing))
        {
            LogCancellationSkipped(batchId, current);
            return Task.FromResult(false);
        }

        var meta = tracker.GetOutput(batchId) as BatchMetaOutput;
        int cancelledCount = 0;

        if (meta is not null)
        {
            for (int i = 0; i < meta.ItemCount; i++)
            {
                var itemKey = ItemKey(batchId, i);
                var itemStatus = tracker.GetStatus(itemKey);

                if (itemStatus is not (JobStatus.Pending or JobStatus.Processing))
                    continue;

                if (monitor.TryCancelRequest(itemKey))
                {
                    tracker.SetStatus(itemKey, JobStatus.Cancelled);
                    cancelledCount++;
                }
            }
        }

        if (cancelledCount > 0)
        {
            tracker.SetStatus(batchId, JobStatus.Cancelled);
            LogBatchCancelled(batchId, cancelledCount);
        }
        else
        {
            LogBatchCancelFailed(batchId);
        }

        return Task.FromResult(cancelledCount > 0);
    }

    // -------------------------------------------------------------------------
    // Observer interfaces
    // -------------------------------------------------------------------------

    public Task OnProgress(string jobId, JobProgressUpdate progress)
    {
        tracker.SetProgress(jobId, progress.PercentComplete, progress.Message);
        LogItemProgress(jobId, progress.PercentComplete, progress.Message);
        return Task.CompletedTask;
    }

    public Task OnCompleted(string jobId, JobOutput output)
    {
        LogItemCompleted(this.GetPrimaryKeyString(), jobId);
        TryDeactivateIfComplete();
        return Task.CompletedTask;
    }

    public Task OnCanceled(string jobId)
    {
        LogItemCanceled(this.GetPrimaryKeyString(), jobId);
        TryDeactivateIfComplete();
        return Task.CompletedTask;
    }

    public Task OnFaulted(string jobId, Exception exception)
    {
        LogItemFaulted(exception, this.GetPrimaryKeyString(), jobId);
        TryDeactivateIfComplete();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called after each terminal item callback. When every item has reached a
    /// terminal state the batch-level tracker status is finalised and
    /// <see cref="Grain.DeactivateOnIdle"/> is requested so the activation is
    /// released instead of lingering until Orleans's collection timeout.
    /// </summary>
    private void TryDeactivateIfComplete()
    {
        var batchId = this.GetPrimaryKeyString();
        var meta = tracker.GetOutput(batchId) as BatchMetaOutput;

        if (meta is null)
            return;

        int completed = 0, failed = 0, cancelled = 0;

        for (int i = 0; i < meta.ItemCount; i++)
        {
            switch (tracker.GetStatus(ItemKey(batchId, i)))
            {
                case JobStatus.Completed: completed++; break;
                case JobStatus.Failed: failed++; break;
                case JobStatus.Cancelled: cancelled++; break;
            }
        }

        if (completed + failed + cancelled < meta.ItemCount)
            return;

        var finalStatus = failed > 0 ? JobStatus.Failed
                        : cancelled > 0 ? JobStatus.Cancelled
                        : JobStatus.Completed;

        tracker.SetStatus(batchId, finalStatus);
        LogBatchComplete(batchId, meta.ItemCount, finalStatus);
        DeactivateOnIdle();
    }

    public Task OnDataReceived(string jobId, JobDataPayload data, CancellationToken cancellationToken = default)
    {
        if (data is BatchItemResultPayload payload)
        {
            tracker.SetOutput(jobId, new BatchItemOutput(payload.ProcessedData));
            LogItemDataReceived(this.GetPrimaryKeyString(), jobId, payload.WorkerId, payload.ItemIndex);
        }
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Logging
    // -------------------------------------------------------------------------

    [LoggerMessage(1, LogLevel.Warning, "Batch {BatchId} is already Processing; ignoring re-submit")]
    private partial void LogAlreadySubmitted(string batchId);

    [LoggerMessage(2, LogLevel.Information, "Batch {BatchId} submitted {ItemCount} items to {WorkerCount} worker grain(s); coordinator will deactivate")]
    private partial void LogBatchSubmitted(string batchId, int itemCount, int workerCount);

    [LoggerMessage(3, LogLevel.Debug, "Batch {BatchId} is {Status}; cancellation skipped")]
    private partial void LogCancellationSkipped(string batchId, JobStatus status);

    [LoggerMessage(4, LogLevel.Information, "Batch {BatchId}: cancelled {CancelledCount} item(s) while queued")]
    private partial void LogBatchCancelled(string batchId, int cancelledCount);

    [LoggerMessage(5, LogLevel.Warning, "Batch {BatchId}: no items in a cancellable state")]
    private partial void LogBatchCancelFailed(string batchId);

    [LoggerMessage(6, LogLevel.Debug, "Batch item {JobId}: progress {Percent}% — {Message}")]
    private partial void LogItemProgress(string jobId, int percent, string? message);

    [LoggerMessage(7, LogLevel.Debug, "Batch {BatchId}: item {JobId} completed")]
    private partial void LogItemCompleted(string batchId, string jobId);

    [LoggerMessage(8, LogLevel.Debug, "Batch {BatchId}: item {JobId} cancelled")]
    private partial void LogItemCanceled(string batchId, string jobId);

    [LoggerMessage(9, LogLevel.Warning, "Batch {BatchId}: item {JobId} faulted")]
    private partial void LogItemFaulted(Exception exception, string batchId, string jobId);

    [LoggerMessage(10, LogLevel.Debug, "Batch {BatchId}: received data for item {JobId} from worker {WorkerId} (index {ItemIndex})")]
    private partial void LogItemDataReceived(string batchId, string jobId, string workerId, int itemIndex);

    [LoggerMessage(11, LogLevel.Information, "Batch {BatchId} complete ({ItemCount} items, status {FinalStatus}); grain deactivating")]
    private partial void LogBatchComplete(string batchId, int itemCount, JobStatus finalStatus);
}
