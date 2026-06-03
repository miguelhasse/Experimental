using System.Diagnostics;

namespace OrleansSample;

/// <summary>
/// Telemetry and observability infrastructure for batch grain operations.
/// Provides ActivitySource for distributed tracing and metrics for monitoring
/// concurrent batch execution, queue depth, and item processing overlaps.
/// </summary>
public static class BatchGrainTelemetry
{
    /// <summary>ActivitySource name for batch grain operations traces.</summary>
    public const string ActivitySourceName = "OrleansSample.Batch";

    /// <summary>Lazy-initialized ActivitySource for batch grain traces.</summary>
    private static readonly Lazy<ActivitySource> _activitySourceLazy =
        new(() => new ActivitySource(ActivitySourceName));

    /// <summary>Gets the ActivitySource for batch grain operations.</summary>
    public static ActivitySource ActivitySource => _activitySourceLazy.Value;

    /// <summary>
    /// Activity kind tags for batch grain operations.
    /// </summary>
    public static class ActivityTags
    {
        public const string BatchId = "batch.id";
        public const string ItemIndex = "item.index";
        public const string WorkerId = "worker.id";
        public const string ItemCount = "batch.item_count";
        public const string WorkerCount = "batch.worker_count";
        public const string QueueDepth = "queue.depth";
        public const string ProcessingDurationMs = "processing.duration_ms";
    }

    /// <summary>
    /// Creates a started Activity for batch coordinator ProcessBatchAsync.
    /// </summary>
    /// <param name="batchId">The batch identifier (grain key).</param>
    /// <param name="itemCount">Total number of items in the batch.</param>
    /// <param name="workerCount">Number of worker grains.</param>
    /// <returns>Started Activity or null if ActivitySource is disabled.</returns>
    public static Activity? StartProcessBatchActivity(string batchId, int itemCount, int workerCount)
    {
        var activity = ActivitySource.StartActivity("batch_coordinator.process_batch");
        if (activity is not null)
        {
            activity.SetTag(ActivityTags.BatchId, batchId);
            activity.SetTag(ActivityTags.ItemCount, itemCount);
            activity.SetTag(ActivityTags.WorkerCount, workerCount);
        }
        return activity;
    }

    /// <summary>
    /// Creates a started Activity for batch worker SubmitItemAsync.
    /// </summary>
    /// <param name="workerId">The worker grain identifier (grain key).</param>
    /// <param name="batchId">The batch identifier.</param>
    /// <param name="itemIndex">The index of this item within the batch.</param>
    /// <returns>Started Activity or null if ActivitySource is disabled.</returns>
    public static Activity? StartSubmitItemActivity(string workerId, string batchId, int itemIndex)
    {
        var activity = ActivitySource.StartActivity("batch_worker.submit_item");
        if (activity is not null)
        {
            activity.SetTag(ActivityTags.WorkerId, workerId);
            activity.SetTag(ActivityTags.BatchId, batchId);
            activity.SetTag(ActivityTags.ItemIndex, itemIndex);
        }
        return activity;
    }

    /// <summary>
    /// Creates a started Activity for batch item processing in the request handler.
    /// </summary>
    /// <param name="itemKey">The item tracker key (unique identifier).</param>
    /// <param name="batchId">The batch identifier.</param>
    /// <returns>Started Activity or null if ActivitySource is disabled.</returns>
    public static Activity? StartProcessItemActivity(string itemKey, string batchId)
    {
        var activity = ActivitySource.StartActivity("batch_item.process");
        if (activity is not null)
        {
            activity.SetTag("item.key", itemKey);
            activity.SetTag(ActivityTags.BatchId, batchId);
        }
        return activity;
    }

    /// <summary>
    /// Adds a progress event to the current activity.
    /// Useful for tracking multi-step batch item processing (Preparing → Processing → Finalizing).
    /// </summary>
    /// <param name="stepName">Name of the processing step (e.g., "Preparing", "Processing", "Finalizing").</param>
    public static void RecordProcessingStep(string stepName)
    {
        Activity.Current?.AddEvent(new ActivityEvent("batch_step", tags: new ActivityTagsCollection
        {
            { "step.name", stepName }
        }));
    }
}
