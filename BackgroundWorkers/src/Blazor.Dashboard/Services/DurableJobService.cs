using OrleansSample;

namespace BlazorDashboard.Services;

/// <summary>
/// Thin client over <see cref="IDurableJobGrain"/>. Stateless: each owner grain holds its jobs durably,
/// so the page reads the full records on every refresh instead of tracking them locally.
/// </summary>
public sealed class DurableJobService(IClusterClient client)
{
    private IDurableJobGrain Grain(string ownerId) => client.GetGrain<IDurableJobGrain>(ownerId);

    public Task SubmitAsync(string ownerId, string jobId, JobRequest request) =>
        Grain(ownerId).SubmitAsync(jobId, request);

    public Task<IReadOnlyList<DurableJobRecord>> GetJobsAsync(string ownerId) =>
        Grain(ownerId).GetJobsAsync();

    public Task<int> GetFinishedCountAsync(string ownerId) =>
        Grain(ownerId).GetFinishedCountAsync();

    /// <summary>Cancels a job that is still processing. Returns <c>false</c> if it is unknown or already finished.</summary>
    public Task<bool> CancelAsync(string ownerId, string jobId) =>
        Grain(ownerId).CancelAsync(jobId);

    /// <summary>
    /// Resubmits a failed job with its original request. The grain starts it as a new run (new epoch);
    /// a job that is already running again is left alone. Returns <c>false</c> if the job is not retryable.
    /// </summary>
    public async Task<bool> RetryAsync(string ownerId, string jobId)
    {
        var grain = Grain(ownerId);
        var job = await grain.GetJobAsync(jobId);
        if (job is not { Status: JobStatus.Failed, Request: { } request })
            return false;

        await grain.SubmitAsync(jobId, request);
        return true;
    }

    /// <summary>Retries every failed job of the owner; returns how many were resubmitted.</summary>
    public async Task<int> RetryAllFailedAsync(string ownerId)
    {
        var grain = Grain(ownerId);
        var failed = (await grain.GetJobsAsync()).Where(IsRetryable).ToList();
        await Task.WhenAll(failed.Select(j => grain.SubmitAsync(j.JobId, j.Request!)));
        return failed.Count;
    }

    public static bool IsRetryable(DurableJobRecord job) =>
        job is { Status: JobStatus.Failed, Request: not null };

    public Task<int> RecoverAsync(string ownerId) =>
        Grain(ownerId).RecoverUnfinishedJobsAsync();
}
