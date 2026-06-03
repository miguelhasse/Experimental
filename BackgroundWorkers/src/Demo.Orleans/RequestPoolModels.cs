namespace OrleansSample;

/// <summary>
/// Orleans-serializable snapshot of request pool activity returned by
/// <see cref="IRequestPoolGrainService.GetStatisticsAsync"/>.
/// Mirrors <see cref="RequestProcessor.RequestPoolStats"/> across the Orleans grain boundary.
/// </summary>
[GenerateSerializer]
public record RequestPoolStatsSnapshot(
    [property: Id(0)] int QueueDepthHigh,
    [property: Id(1)] int QueueDepthNormal,
    [property: Id(2)] int QueueDepthLow,
    [property: Id(3)] int TotalQueueDepth,
    [property: Id(4)] int ActiveWorkers,
    [property: Id(5)] long TotalEnqueued,
    [property: Id(6)] long TotalCompleted,
    [property: Id(7)] long TotalFailed,
    [property: Id(8)] long TotalCancelled,
    [property: Id(9)] int MaxConcurrency,
    [property: Id(10)] int BoundedCapacity,
    [property: Id(11)] int SiloCount = 1);
