using Orleans.Concurrency;

namespace OrleansSample;

/// <summary>
/// Observer for mid-handler streaming data payloads.
/// Unlike <see cref="IJobCompletionObserver"/> and <see cref="IJobProgressObserver"/>,
/// this method is <b>not</b> <c>[OneWay]</c> — it is awaitable and supports
/// back-pressure from the handler side.
/// </summary>
[Alias("Grains.JobDataObserver")]
public interface IJobDataObserver : IGrainObserver
{
    /// <summary>Invoked when the handler produces a streaming data payload.</summary>
    [Alias("OnDataReceived"), AlwaysInterleave]
    Task OnDataReceived(string jobId, JobDataPayload data, CancellationToken cancellationToken = default);
}
