namespace OrleansSample;

/// <summary>
/// Marker interface for all job request types submitted to <see cref="IJobGrain"/>.
/// All concrete implementations must be decorated with <c>[GenerateSerializer]</c>.
/// </summary>
public interface IJobRequest
{
    RequestPriority Priority { get; }
}

/// <summary>
/// Orleans-serializable payload submitted to a job grain.
/// </summary>
[GenerateSerializer]
public record JobRequest(
    [property: Id(0)] string Payload,
    [property: Id(1)] string? Category = null,
    [property: Id(2)] RequestPriority Priority = RequestPriority.Normal) : IJobRequest;

/// <summary>
/// A job that processes a collection of items as a single unit of work.
/// </summary>
[GenerateSerializer]
public record BatchJobRequest(
    [property: Id(0)] IReadOnlyList<string> Items,
    [property: Id(1)] string? Category = null,
    [property: Id(2)] RequestPriority Priority = RequestPriority.High) : IJobRequest;

/// <summary>
/// A job that carries a scheduled execution hint (for logging/routing; not a real timer).
/// </summary>
[GenerateSerializer]
public record ScheduledJobRequest(
    [property: Id(0)] string Payload,
    [property: Id(1)] DateTimeOffset ScheduledAt,
    [property: Id(2)] RequestPriority Priority = RequestPriority.Low) : IJobRequest;

/// <summary>
/// Abstract base for handler-specific typed output data carried by <see cref="JobResult"/>.
/// Derive from this record to attach structured results to a completed job.
/// All concrete subtypes must be decorated with <c>[GenerateSerializer]</c>.
/// </summary>
[GenerateSerializer]
public abstract record JobOutput;

/// <summary>
/// Typed output wrapper for handlers that produce a plain string result.
/// Use this when migrating from <c>string? Output</c> to the typed <see cref="JobOutput"/> system.
/// </summary>
[GenerateSerializer]
public sealed record TextJobOutput(
    [property: Id(0)] string Text) : JobOutput;

/// <summary>
/// Serializable outcome returned by a job grain.
/// Carries only primitive fields so it crosses the Orleans serialiser boundary.
/// <para>Id 1 is intentionally reserved (previously <c>string? Output</c>).</para>
/// </summary>
[GenerateSerializer]
public record JobResult(
    [property: Id(0)] bool Success,
    [property: Id(2)] string? Error,
    [property: Id(3)] bool Cancelled = false,
    [property: Id(4)] JobOutput? Output = null);

/// <summary>
/// Incremental progress update sent from a job handler back to the job grain observer.
/// </summary>
[GenerateSerializer]
public record JobProgressUpdate(
    [property: Id(0)] int PercentComplete,
    [property: Id(1)] string? Message = null,
    [property: Id(2)] JobProgressDelta? Delta = null);

/// <summary>
/// Abstract base for handler-specific progress delta data carried by <see cref="JobProgressUpdate"/>.
/// Derive from this record to attach strongly-typed progress details to a <see cref="JobProgressUpdate"/>.
/// All concrete subtypes must be decorated with <c>[GenerateSerializer]</c>.
/// </summary>
[GenerateSerializer]
public abstract record JobProgressDelta;

/// <summary>Progress delta emitted by <see cref="JobRequestHandler"/> at each processing step.</summary>
[GenerateSerializer]
public sealed record JobStepProgressDelta(
    [property: Id(0)] int Step,
    [property: Id(1)] int TotalSteps) : JobProgressDelta;

/// <summary>Progress delta emitted by <see cref="BatchJobRequestHandler"/> for each processed item.</summary>
[GenerateSerializer]
public sealed record BatchItemProgressDelta(
    [property: Id(0)] int ProcessedCount,
    [property: Id(1)] int TotalItems,
    [property: Id(2)] string CurrentItem) : JobProgressDelta;

/// <summary>Progress delta emitted by <see cref="ScheduledJobRequestHandler"/> for named phases.</summary>
[GenerateSerializer]
public sealed record ScheduledJobProgressDelta(
    [property: Id(0)] string Phase) : JobProgressDelta;

/// <summary>
/// Point-in-time progress snapshot returned by <see cref="IJobGrain.GetProgressAsync"/>.
/// </summary>
[GenerateSerializer]
public record JobProgressSnapshot(
    [property: Id(0)] int PercentComplete,
    [property: Id(1)] string? Message = null);

/// <summary>
/// Lifecycle state of a job, tracked in <see cref="IJobTracker"/>.
/// </summary>
[GenerateSerializer]
public enum JobStatus
{
    Unknown = 0,
    Pending = 1,
    Processing = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
}

// ── JobDataPayload (new abstract base for IJobDataObserver) ───────────────────

/// <summary>
/// Abstract base for streaming data payloads sent via <see cref="IJobDataObserver"/>.
/// All concrete subtypes must be decorated with <c>[GenerateSerializer]</c>.
/// </summary>
[GenerateSerializer]
public abstract record JobDataPayload;
