using System.Collections.Concurrent;

namespace Orleans.Tests;

/// <summary>
/// Fake <see cref="IRequestPoolMonitor"/> for grain integration tests.
/// Preconfigure per-request cancel outcomes with <see cref="ConfigureCancel"/>; every cancel attempt is recorded in
/// <see cref="CancelRequests"/> (grains call it from silo threads, so everything here is thread-safe).
/// </summary>
internal sealed class FakeRequestPoolMonitor : IRequestPoolMonitor
{
    private readonly ConcurrentDictionary<string, bool> _cancelResults = new();
    private readonly ConcurrentQueue<string> _cancelRequests = new();

    /// <summary>Request ids passed to <see cref="TryCancelRequest"/>, in call order.</summary>
    public IReadOnlyCollection<string> CancelRequests => _cancelRequests.ToArray();

    /// <summary>
    /// Configures whether <see cref="TryCancelRequest"/> returns <paramref name="result"/>
    /// for the given <paramref name="requestId"/>.
    /// </summary>
    public void ConfigureCancel(string requestId, bool result) =>
        _cancelResults[requestId] = result;

    public bool TryCancelRequest(string requestId)
    {
        _cancelRequests.Enqueue(requestId);
        return _cancelResults.TryGetValue(requestId, out var result) && result;
    }

    public RequestPoolStats GetSnapshot() =>
        new(QueueDepthHigh: 0, QueueDepthNormal: 0, QueueDepthLow: 0,
            TotalQueueDepth: 0, ActiveWorkers: 0,
            TotalEnqueued: 0, TotalCompleted: 0, TotalFailed: 0, TotalCancelled: 0,
            MaxConcurrency: 1, BoundedCapacity: 1000);

    public int CancelAllRequests(RequestPriority? priority = null) => 0;
}
