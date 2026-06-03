using Orleans.Services;

namespace OrleansSample;

/// <summary>
/// Per-silo system-target grain service that exposes the local silo's
/// <see cref="Demo.IRequestPool"/> to other grains and external clients.
/// </summary>
/// <remarks>
/// Because this is an <see cref="IGrainService"/>, exactly one instance runs
/// on each silo in the cluster.  Grains call it via
/// <see cref="IRequestPoolGrainServiceClient"/> which routes every call to the
/// service on the same silo as the calling grain.
/// </remarks>
[Alias("Grains.IRequestPoolGrainService")]
public interface IRequestPoolGrainService : IGrainService
{
    /// <summary>Returns a point-in-time statistics snapshot of the local silo's request pool.</summary>
    [Alias("GetStatisticsAsync")]
    Task<RequestPoolStatsSnapshot> GetStatisticsAsync();

    /// <summary>
    /// Attempts to cancel a queued request before it is dispatched.
    /// </summary>
    /// <param name="requestId">The <see cref="Demo.RequestContext.RequestId"/> to cancel.</param>
    /// <returns>
    /// <c>true</c> if the request was found and cancelled;
    /// <c>false</c> if the request had already started, completed, or was never submitted.
    /// </returns>
    [Alias("TryCancelRequestAsync")]
    Task<bool> TryCancelRequestAsync(string requestId);

    /// <summary>
    /// Cancels all requests that are still queued (not yet dispatched) on the local silo,
    /// optionally filtered to a specific priority level.
    /// </summary>
    /// <param name="priority">
    /// When provided, only requests with this <see cref="Demo.RequestPriority"/> are cancelled.
    /// When <c>null</c> (the default), all queued requests across every priority level are cancelled.
    /// </param>
    /// <returns>The number of requests that were found queued and cancelled.</returns>
    [Alias("CancelAllRequestsAsync")]
    Task<int> CancelAllRequestsAsync(RequestPriority? priority = null);
}
