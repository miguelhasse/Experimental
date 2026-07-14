using System.Diagnostics;

namespace RequestProcessor;

/// <summary>
/// A point-in-time snapshot of <see cref="IRequestPool"/> activity.
/// </summary>
/// <param name="QueueDepthHigh">Requests currently waiting in the High-priority channel.</param>
/// <param name="QueueDepthNormal">Requests currently waiting in the Normal-priority channel.</param>
/// <param name="QueueDepthLow">Requests currently waiting in the Low-priority channel.</param>
/// <param name="TotalQueueDepth">Sum of all three priority queue depths.</param>
/// <param name="ActiveWorkers">Workers currently inside <see cref="IRequestDispatcher.DispatchAsync"/>.</param>
/// <param name="TotalEnqueued">Cumulative requests accepted since pool start.</param>
/// <param name="TotalCompleted">Cumulative requests that completed with <c>Success=true</c>.</param>
/// <param name="TotalFailed">Cumulative requests that completed with <c>Success=false</c> or threw.</param>
/// <param name="TotalCancelled">Cumulative requests cancelled before dispatch began.</param>
/// <param name="MaxConcurrency">Configured worker count.</param>
/// <param name="BoundedCapacity">Configured per-priority channel capacity.</param>
[DebuggerDisplay("Queue = {TotalQueueDepth} (H:{QueueDepthHigh}/N:{QueueDepthNormal}/L:{QueueDepthLow}), Active = {ActiveWorkers}/{MaxConcurrency}")]
public record RequestPoolStats(
    int QueueDepthHigh,
    int QueueDepthNormal,
    int QueueDepthLow,
    int TotalQueueDepth,
    int ActiveWorkers,
    long TotalEnqueued,
    long TotalCompleted,
    long TotalFailed,
    long TotalCancelled,
    int MaxConcurrency,
    int BoundedCapacity);

/// <summary>
/// Live view into the request pool: statistics snapshot and request cancellation.
/// Implemented by <see cref="RequestPoolService"/> and exposed as a singleton in DI.
/// </summary>
public interface IRequestPoolMonitor
{
    /// <summary>Returns a consistent point-in-time snapshot of pool activity.</summary>
    RequestPoolStats GetSnapshot();

    /// <summary>
    /// Attempts to cancel a request that is still queued (not yet dispatched).
    /// </summary>
    /// <param name="requestId">The <see cref="RequestContext.RequestId"/> to cancel.</param>
    /// <returns>
    /// <c>true</c> if the request was found in the pending queue and cancelled;
    /// <c>false</c> if the request was already dispatched, completed, or never submitted.
    /// </returns>
    bool TryCancelRequest(string requestId);

    /// <summary>
    /// Cancels all requests that are still queued (not yet dispatched),
    /// optionally filtered to a specific priority level.
    /// </summary>
    /// <param name="priority">
    /// When provided, only requests with this <see cref="RequestPriority"/> are cancelled.
    /// When <c>null</c> (the default), all queued requests across every priority level are cancelled.
    /// </param>
    /// <returns>The number of requests that were found queued and cancelled.</returns>
    int CancelAllRequests(RequestPriority? priority = null);
}
