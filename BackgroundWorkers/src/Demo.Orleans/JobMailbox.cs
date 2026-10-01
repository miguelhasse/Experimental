namespace OrleansSample;

/// <summary>
/// What a request-pool worker hands back to the grain that submitted the job. Workers write these into the activation's
/// in-process mailbox; the grain applies them on its own scheduler (see <c>DurableJobGrain</c>'s pump). There is deliberately no
/// grain method behind this: nothing outside the silo process can post a message.
/// </summary>
/// <param name="JobId">The job, within the grain.</param>
/// <param name="Epoch">The run the message belongs to (see <see cref="DurableJobRecord.Epoch"/>).</param>
/// <param name="Sequence">Increases within the run; used to drop duplicates (progress and results have separate spaces).</param>
internal abstract record MailboxMessage(string JobId, long Epoch, long Sequence);

internal sealed record ProgressMessage(string JobId, long Epoch, long Sequence, int PercentComplete, string? Message)
    : MailboxMessage(JobId, Epoch, Sequence);

/// <summary><see cref="JobStatus.Processing"/> is a partial result; Completed, Failed and Cancelled are final.</summary>
internal sealed record ResultMessage(string JobId, long Epoch, long Sequence, JobStatus Status, string? Output, string? Error)
    : MailboxMessage(JobId, Epoch, Sequence)
{
    public bool IsFinal => Status is not JobStatus.Processing;
}

/// <summary>A run the activation has handed to the pool and expects results for.</summary>
/// <param name="RequestId">The id the pool knows the run by (unique per owner, job and epoch), used to cancel it.</param>
/// <param name="Deadline">When the grain gives up on the run (see <see cref="JobRecoveryOptions.MaxRunDuration"/>); null means never.</param>
internal sealed record InFlightRun(long Epoch, string RequestId, DateTimeOffset? Deadline = null);
