namespace OrleansSample;

/// <summary>Settings for <see cref="DurableJobGrain"/>: recovery of orphaned jobs, input limits and retention.</summary>
public sealed class JobRecoveryOptions
{
    /// <summary>
    /// How far ahead the next recovery check is scheduled (as an Orleans durable job) while a grain has
    /// unfinished jobs. The check reactivates the grain even if nothing calls it. Durable jobs fire at shard
    /// granularity (<c>DurableJobsOptions.ShardDuration</c>, one minute by default), so the real delay can be longer.
    /// </summary>
    public TimeSpan CheckPeriod { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A job whose owner is gone is only treated as orphaned once it has had no activity (progress or result)
    /// for this long. This absorbs membership lag, where a live silo is briefly missing from this silo's view,
    /// and a silo that is shutting down and may still deliver results, so the job is not resubmitted while
    /// another silo is still running it. Zero disables the grace period.
    /// </summary>
    public TimeSpan OrphanGracePeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many finished jobs a grain keeps. When a new run is submitted and more are retained, the oldest
    /// finished ones are removed from durable state. Running jobs are never removed.
    /// </summary>
    public int MaxRetainedFinishedJobs { get; set; } = 1000;

    /// <summary>
    /// Results can only be delivered to the activation that started the job (they travel through an in-process mailbox), so while
    /// a job is running the grain must stay in memory even if it is otherwise idle. It does so by repeatedly delaying its own
    /// deactivation by this much (renewed every third of it). A job can run for much longer than this and than Orleans'
    /// idle collection age. If the activation is lost anyway, the job is released and recovered by rerunning it.
    /// </summary>
    public TimeSpan KeepAliveSlice { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a single run may take before it is failed ("Timed out"). Because a running job pins its grain in memory, an upper
    /// bound is what stops a hung handler from pinning it forever. It is applied twice: as the request's <c>Timeout</c> in the pool,
    /// which cancels a handler that honours its cancellation token, and as a deadline the grain enforces itself (this value plus a
    /// small allowance), which also catches a handler that ignores cancellation and so never lets the pool call back.
    /// <c>null</c>, zero or <see cref="Timeout.InfiniteTimeSpan"/> disable the limit.
    /// </summary>
    public TimeSpan? MaxRunDuration { get; set; } = TimeSpan.FromHours(1);
}
