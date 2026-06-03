using Orleans.Services;

namespace OrleansSample;

/// <summary>
/// Client interface used by grains (and other silo-side components) to call the
/// local silo's <see cref="IRequestPoolGrainService"/>.
/// </summary>
/// <remarks>
/// Register with:
/// <code>services.AddSingleton&lt;IRequestPoolGrainServiceClient, RequestPoolGrainServiceClient&gt;();</code>
/// </remarks>
public interface IRequestPoolGrainServiceClient : IGrainServiceClient<IRequestPoolGrainService>
{
    /// <summary>Returns a statistics snapshot of the local silo's request pool.</summary>
    Task<RequestPoolStatsSnapshot> GetStatisticsAsync();

    /// <summary>Returns a statistics snapshot of the request pool on the specified silo.</summary>
    Task<RequestPoolStatsSnapshot> GetStatisticsAsync(SiloAddress siloAddress);

    /// <summary>
    /// Attempts to cancel a queued request before it is dispatched.
    /// </summary>
    /// <param name="requestId">The <see cref="Demo.RequestContext.RequestId"/> to cancel.</param>
    /// <returns>
    /// <c>true</c> if the request was found and cancelled;
    /// <c>false</c> if already dispatched, completed, or never submitted.
    /// </returns>
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
    Task<int> CancelAllRequestsAsync(RequestPriority? priority = null);
}
