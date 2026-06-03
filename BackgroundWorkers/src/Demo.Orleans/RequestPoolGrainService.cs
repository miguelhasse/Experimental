namespace OrleansSample;

/// <summary>
/// Singleton-per-silo grain service that bridges Orleans grains to the local
/// <see cref="IRequestPool"/> / <see cref="IRequestPoolMonitor"/>.
/// </summary>
/// <remarks>
/// Register with:
/// <code>siloBuilder.AddGrainService&lt;RequestPoolGrainService&gt;();</code>
/// and inject <see cref="IRequestPoolGrainServiceClient"/> into grains that need it.
/// </remarks>
public sealed class RequestPoolGrainService : GrainService, IRequestPoolGrainService
{
    private readonly IRequestPoolMonitor _monitor;

    public RequestPoolGrainService(
        GrainId grainId,
        Silo silo,
        ILoggerFactory loggerFactory,
        IRequestPoolMonitor monitor)
        : base(grainId, silo, loggerFactory)
    {
        _monitor = monitor;
    }

    /// <inheritdoc/>
    public Task<RequestPoolStatsSnapshot> GetStatisticsAsync()
    {
        var s = _monitor.GetSnapshot();
        return Task.FromResult(new RequestPoolStatsSnapshot(
            s.QueueDepthHigh,
            s.QueueDepthNormal,
            s.QueueDepthLow,
            s.TotalQueueDepth,
            s.ActiveWorkers,
            s.TotalEnqueued,
            s.TotalCompleted,
            s.TotalFailed,
            s.TotalCancelled,
            s.MaxConcurrency,
            s.BoundedCapacity,
            SiloCount: 1));
    }

    /// <inheritdoc/>
    public Task<bool> TryCancelRequestAsync(string requestId) =>
        Task.FromResult(_monitor.TryCancelRequest(requestId));

    /// <inheritdoc/>
    public Task<int> CancelAllRequestsAsync(RequestPriority? priority = null) =>
        Task.FromResult(_monitor.CancelAllRequests(priority));
}
