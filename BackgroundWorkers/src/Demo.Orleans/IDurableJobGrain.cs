namespace OrleansSample;

/// <summary>
/// Job grain whose state is persisted through Orleans Journaling (<c>DurableGrain</c>).
/// Submits work to the <see cref="IRequestPool"/>; results come back through an in-process mailbox that
/// the grain drains on its own scheduler (no grain method receives them) and are stored in durable state.
/// While a job runs the grain keeps itself in memory, however long the job takes.
/// </summary>
[Alias("Grains.IDurableJobGrain")]
public interface IDurableJobGrain : IGrainWithStringKey
{
    /// <summary>
    /// Enqueues <paramref name="request"/> under <paramref name="jobId"/>. A no-op if that job is already processing, or if
    /// <see cref="JobRequest.IdempotencyKey"/> equals the key the job was last submitted with (a client retry).
    /// Submitting a finished job id without a matching key starts a new run. Throws <see cref="ArgumentException"/>
    /// (without creating any state) for an empty or oversized id, a null request, or <c>PartialResults</c> outside 0..100.
    /// If the pool refuses the work the run is failed and the exception is rethrown.
    /// </summary>
    [Alias("SubmitAsync")]
    Task SubmitAsync(string jobId, JobRequest request);

    /// <summary>
    /// Resubmits jobs that are still <c>Processing</c> but whose owning silo is gone. Also runs
    /// automatically when the grain activates. Returns how many jobs were resubmitted.
    /// </summary>
    [Alias("RecoverUnfinishedJobsAsync")]
    Task<int> RecoverUnfinishedJobsAsync();

    /// <summary>
    /// Cancels <paramref name="jobId"/> if it is still processing: the job becomes <c>Cancelled</c> immediately and whatever its
    /// run reports later is ignored. A run still queued in the pool is removed from the queue; a handler that is already
    /// executing is not interrupted and finishes with its data dropped. Returns false if the job is unknown or already finished.
    /// </summary>
    [Alias("CancelAsync")]
    Task<bool> CancelAsync(string jobId);

    [Alias("GetJobAsync")]
    Task<DurableJobRecord?> GetJobAsync(string jobId);

    [Alias("GetJobsAsync")]
    Task<IReadOnlyList<DurableJobRecord>> GetJobsAsync();

    /// <summary>Number of jobs that reached a terminal state, across activations.</summary>
    [Alias("GetFinishedCountAsync")]
    Task<int> GetFinishedCountAsync();
}
