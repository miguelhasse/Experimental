namespace OrleansSample;

/// <summary>
/// Grain interface for submitting and tracking a single background job.
/// The grain key is the job ID (string).
/// </summary>
[Alias("Grains.JobGrain")]
public interface IJobGrain : IGrainWithStringKey
{
    /// <summary>Submits any <see cref="IJobRequest"/> implementation for background processing.</summary>
    [Alias("SubmitAsync")]
    Task SubmitAsync(IJobRequest request);

    /// <summary>Returns the current lifecycle status of this job.</summary>
    [Alias("GetStatusAsync")]
    Task<JobStatus> GetStatusAsync();

    /// <summary>
    /// Returns the latest progress snapshot for this job, or <c>null</c> if no progress
    /// has been reported yet (e.g. still queued).
    /// </summary>
    [Alias("GetProgressAsync")]
    Task<JobProgressSnapshot?> GetProgressAsync();

    /// <summary>
    /// Attempts to cancel a job that is still queued but not yet dispatched.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the job was found in the pending queue and cancelled;
    /// <c>false</c> if the job was already dispatched, completed, or never submitted.
    /// </returns>
    [Alias("TryCancelAsync")]
    Task<bool> TryCancelAsync();
}
