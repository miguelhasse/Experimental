using Orleans.Concurrency;

namespace OrleansSample;

/// <summary>
/// Observer for job completion lifecycle events (completed, cancelled, or faulted).
/// Implemented by grains that enqueue work in the request pool and need to be
/// notified when that work finishes.
/// </summary>
[Alias("Grains.JobCompletionObserver")]
public interface IJobCompletionObserver : IGrainObserver
{
    /// <summary>Invoked when the job finishes successfully.</summary>
    [Alias("OnCompleted"), AlwaysInterleave, OneWay]
    Task OnCompleted(string jobId, JobOutput output);

    /// <summary>Invoked when the job is cancelled (pre-dispatch or in-flight).</summary>
    [Alias("OnCanceled"), AlwaysInterleave, OneWay]
    Task OnCanceled(string jobId);

    /// <summary>
    /// Invoked when the job faults with an unhandled exception.
    /// <see cref="System.Exception"/> crosses the Orleans 10 grain boundary natively
    /// via <c>ExceptionCodec</c> — no wrapper type is needed.
    /// All BCL exception types (<c>System.*</c>) are covered by the default
    /// <c>ExceptionSerializationOptions.SupportedNamespacePrefixes</c>.
    /// </summary>
    [Alias("OnFaulted"), AlwaysInterleave, OneWay]
    Task OnFaulted(string jobId, Exception exception);
}
