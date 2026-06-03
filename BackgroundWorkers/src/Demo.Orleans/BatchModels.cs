namespace OrleansSample;

/// <summary>
/// Request submitted to the pool by a <see cref="BatchWorkerGrain"/> to process a single
/// batch item. The pool request key is <c>"{BatchId}:item-{ItemIndex}"</c>.
/// </summary>
[GenerateSerializer]
public record BatchWorkerItemRequest(
    [property: Id(0)] string BatchId,
    [property: Id(1)] int ItemIndex,
    [property: Id(2)] string Data) : IJobRequest
{
    public RequestPriority Priority => RequestPriority.Normal;
}

/// <summary>
/// Metadata stored in <see cref="IJobTracker"/> under the batch coordinator key
/// so that <see cref="IBatchCoordinatorGrain.GetSummaryAsync"/> and
/// <see cref="IBatchCoordinatorGrain.TryCancelAsync"/> can reconstruct the full
/// set of item keys after the coordinator grain deactivates and re-activates.
/// </summary>
[GenerateSerializer]
public sealed record BatchMetaOutput(
    [property: Id(0)] int ItemCount,
    [property: Id(1)] int WorkerCount) : JobOutput;

/// <summary>
/// Status snapshot for a single worker within a batch.
/// </summary>
[GenerateSerializer]
public record BatchWorkerStatus(
    [property: Id(0)] string WorkerId,
    [property: Id(1)] int ItemsProcessed,
    [property: Id(2)] JobStatus Status);

/// <summary>
/// Aggregate coordination status for an entire batch.
/// Combines worker statuses and overall batch progress.
/// </summary>
[GenerateSerializer]
public record BatchCoordinationSummary(
    [property: Id(0)] string BatchId,
    [property: Id(1)] int ItemCount,
    [property: Id(2)] int WorkerCount,
    [property: Id(3)] int TotalCompleted,
    [property: Id(4)] int TotalFailed,
    [property: Id(5)] int TotalCancelled,
    [property: Id(6)] IReadOnlyList<BatchWorkerStatus> WorkerStatuses,
    [property: Id(7)] JobStatus OverallStatus);

/// <summary>
/// Progress delta emitted during batch processing.
/// Carries per-worker progress details.
/// </summary>
[GenerateSerializer]
public sealed record BatchProgressDelta(
    [property: Id(0)] string WorkerId,
    [property: Id(1)] int ItemsProcessed,
    [property: Id(2)] int TotalItems) : JobProgressDelta;

/// <summary>
/// Data payload emitted once per completed batch item via <see cref="IJobDataObserver.OnDataReceived"/>.
/// Carries the worker's result for a single item so the coordinator can store it in the tracker.
/// </summary>
[GenerateSerializer]
public sealed record BatchItemResultPayload(
    [property: Id(0)] int ItemIndex,
    [property: Id(1)] string WorkerId,
    [property: Id(2)] string ProcessedData) : JobDataPayload;

/// <summary>
/// Final output stored in the tracker for a single batch item on completion.
/// </summary>
[GenerateSerializer]
public sealed record BatchItemOutput(
    [property: Id(0)] string ProcessedData) : JobOutput;
