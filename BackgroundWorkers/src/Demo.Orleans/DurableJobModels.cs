namespace OrleansSample;

/// <summary>
/// Durable record of one job, persisted in the grain's journal.
/// Kept to JSON-friendly types because the journal uses the System.Text.Json format.
/// </summary>
/// <remarks>
/// <see cref="Request"/>, <see cref="OwnerSilo"/> and <see cref="Attempts"/> exist so an unfinished job
/// can be resubmitted if the silo that was running it dies (see <see cref="DurableJobGrain"/>).
/// <para>
/// <see cref="Epoch"/> and <see cref="LastSequence"/> fence the data pushed back by pool workers.
/// Every run of a job (the first submit, a resubmit of the same job id, and each recovery) gets a new
/// epoch, and every progress/result call carries the epoch it was started under plus a sequence number
/// that increases within that run. Calls from an older epoch, duplicates and out-of-order calls are
/// ignored, so a late result from a lost owner cannot overwrite the run that replaced it.
/// <see cref="ResultsReceived"/> counts the results (partial and final) applied to the current run.
/// Progress and results have separate sequence spaces (<see cref="LastProgressSequence"/> and
/// <see cref="LastSequence"/>), so a stream of progress can never invalidate a partial result.
/// </para>
/// <para>
/// <see cref="SubmissionKey"/> is the client's idempotency key (<see cref="JobRequest.IdempotencyKey"/>): resubmitting a
/// job with the key it was last submitted with is a retry and does nothing, whatever state the job is in.
/// </para>
/// </remarks>
[GenerateSerializer]
public sealed record DurableJobRecord(
    [property: Id(0)] string JobId,
    [property: Id(1)] JobStatus Status,
    [property: Id(2)] int PercentComplete,
    [property: Id(3)] string? Message,
    [property: Id(4)] string? Output,
    [property: Id(5)] string? Error,
    [property: Id(6)] DateTimeOffset UpdatedAt,
    [property: Id(7)] JobRequest? Request = null,
    [property: Id(8)] string? OwnerSilo = null,
    [property: Id(9)] int Attempts = 0,
    [property: Id(10)] long Epoch = 0,
    [property: Id(11)] long LastSequence = 0,
    [property: Id(12)] int ResultsReceived = 0,
    [property: Id(13)] long LastProgressSequence = 0,
    [property: Id(14)] string? SubmissionKey = null)
{
    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
}
