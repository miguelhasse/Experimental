namespace OrleansSample;

/// <summary>
/// Singleton grain that exposes cluster-wide pool statistics to Orleans clients (e.g. the Blazor dashboard).
/// Fans out <see cref="IRequestPoolGrainServiceClient.GetStatisticsAsync(SiloAddress)"/> calls to every
/// active silo and returns the aggregated totals.
/// </summary>
public sealed class PoolStatsGrain(IRequestPoolGrainServiceClient poolClient)
    : Grain, IPoolStatsGrain
{
    public async Task<RequestPoolStatsSnapshot> GetSnapshotAsync()
    {
        var mgmt = GrainFactory.GetGrain<IManagementGrain>(0);
        var hosts = await mgmt.GetHosts(onlyActive: true);

        if (hosts.Count == 0)
            return new RequestPoolStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, SiloCount: 0);

        var tasks = hosts.Keys.Select(poolClient.GetStatisticsAsync).ToList();
        var results = await Task.WhenAll(tasks);

        return results.Aggregate(
            new RequestPoolStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, SiloCount: 0),
            (acc, s) => acc with
            {
                QueueDepthHigh = acc.QueueDepthHigh + s.QueueDepthHigh,
                QueueDepthNormal = acc.QueueDepthNormal + s.QueueDepthNormal,
                QueueDepthLow = acc.QueueDepthLow + s.QueueDepthLow,
                TotalQueueDepth = acc.TotalQueueDepth + s.TotalQueueDepth,
                ActiveWorkers = acc.ActiveWorkers + s.ActiveWorkers,
                TotalEnqueued = acc.TotalEnqueued + s.TotalEnqueued,
                TotalCompleted = acc.TotalCompleted + s.TotalCompleted,
                TotalFailed = acc.TotalFailed + s.TotalFailed,
                TotalCancelled = acc.TotalCancelled + s.TotalCancelled,
                MaxConcurrency = acc.MaxConcurrency + s.MaxConcurrency,
                BoundedCapacity = acc.BoundedCapacity + s.BoundedCapacity,
                SiloCount = acc.SiloCount + s.SiloCount,
            });
    }
}
