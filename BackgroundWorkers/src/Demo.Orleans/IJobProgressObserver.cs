using Orleans.Concurrency;

namespace OrleansSample;

/// <summary>
/// Observer for incremental job progress updates.
/// Implemented by grains that want to track real-time progress from a running handler.
/// </summary>
[Alias("Grains.JobProgressObserver")]
public interface IJobProgressObserver : IGrainObserver
{
    /// <summary>
    /// Invoked by the request handler to report incremental progress.
    /// Implementations should handle this being called from a non-grain thread.
    /// </summary>
    [Alias("OnProgress"), AlwaysInterleave, OneWay]
    Task OnProgress(string jobId, JobProgressUpdate progress);
}
