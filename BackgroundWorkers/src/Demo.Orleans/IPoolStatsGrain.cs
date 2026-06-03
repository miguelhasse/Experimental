namespace OrleansSample;

/// <summary>
/// Singleton grain that exposes pool statistics to Orleans clients (e.g. the Blazor dashboard).
/// Because <see cref="IRequestPoolGrainServiceClient"/> can only be called from inside a grain,
/// this thin wrapper bridges the grain-service boundary for external callers.
/// </summary>
/// <remarks>Use the well-known key <c>"default"</c> when resolving this grain.</remarks>
[Alias("Grains.IPoolStatsGrain")]
public interface IPoolStatsGrain : IGrainWithStringKey
{
    /// <summary>Returns a point-in-time statistics snapshot of the local silo's request pool.</summary>
    [Alias("GetSnapshotAsync")]
    Task<RequestPoolStatsSnapshot> GetSnapshotAsync();
}
