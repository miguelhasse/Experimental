using Orleans.Runtime.Services;

namespace OrleansSample;

/// <summary>
/// Routes <see cref="IRequestPoolGrainService"/> calls to the service instance
/// on the same silo as the calling grain.
/// </summary>
public sealed class RequestPoolGrainServiceClient(IServiceProvider serviceProvider)
        : GrainServiceClient<IRequestPoolGrainService>(serviceProvider), IRequestPoolGrainServiceClient
{
    private IRequestPoolGrainService GrainService => GetGrainService(CurrentGrainReference.GrainId);

    /// <inheritdoc/>
    public Task<RequestPoolStatsSnapshot> GetStatisticsAsync() =>
        GrainService.GetStatisticsAsync();

    /// <inheritdoc/>
    public Task<RequestPoolStatsSnapshot> GetStatisticsAsync(SiloAddress siloAddress) =>
        GetGrainService(siloAddress).GetStatisticsAsync();

    /// <inheritdoc/>
    public Task<bool> TryCancelRequestAsync(string requestId) =>
        GrainService.TryCancelRequestAsync(requestId);

    /// <inheritdoc/>
    public Task<int> CancelAllRequestsAsync(RequestPriority? priority = null) =>
        GrainService.CancelAllRequestsAsync(priority);
}
