namespace Orleans.Tests;

/// <summary>
/// Fake <see cref="IRequestPoolMonitor"/> for grain integration tests.
/// Preconfigure per-request cancel outcomes with <see cref="ConfigureCancel"/>.
/// </summary>
internal sealed class FakeRequestPoolMonitor : IRequestPoolMonitor
{
    private readonly Dictionary<string, bool> _cancelResults = new();

    /// <summary>
    /// Configures whether <see cref="TryCancelRequest"/> returns <paramref name="result"/>
    /// for the given <paramref name="requestId"/>.
    /// </summary>
    public void ConfigureCancel(string requestId, bool result) =>
        _cancelResults[requestId] = result;

    public bool TryCancelRequest(string requestId) =>
        _cancelResults.TryGetValue(requestId, out var result) ? result : false;

    public RequestPoolStats GetSnapshot() =>
        new(QueueDepthHigh: 0, QueueDepthNormal: 0, QueueDepthLow: 0,
            TotalQueueDepth: 0, ActiveWorkers: 0,
            TotalEnqueued: 0, TotalCompleted: 0, TotalFailed: 0, TotalCancelled: 0,
            MaxConcurrency: 1, BoundedCapacity: 1000);

    public int CancelAllRequests(RequestPriority? priority = null) => 0;
}
