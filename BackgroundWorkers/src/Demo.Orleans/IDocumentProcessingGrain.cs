namespace OrleansSample;

/// <summary>
/// Grain that executes a three-step sequential document-processing pipeline.
///
/// <para>
/// Each step's typed output (<see cref="JobOutput"/> subtype) is stored in
/// <see cref="IJobTracker"/> and passed as input to the next step's request.
/// The full sequence is triggered by a single <see cref="RunAsync"/> call;
/// subsequent steps are chained automatically via <see cref="IDocumentProcessingObserver.OnStepCompleted"/>.
/// </para>
///
/// <para>
/// Pipeline steps: <b>Extract → Transform → Index</b><br/>
/// Step 1 produces <see cref="ExtractedContentOutput"/> (raw text, word count).<br/>
/// Step 2 consumes step 1's output and produces <see cref="TransformedContentOutput"/> (keywords, sentiment).<br/>
/// Step 3 consumes step 2's output and produces <see cref="IndexedContentOutput"/> (index ID, tag count).
/// </para>
///
/// <para><b>Idempotency:</b> <see cref="RunAsync"/> is a no-op if the pipeline is already
/// <c>Processing</c> (step 1 is running). If the pipeline previously completed, failed, or
/// was cancelled, calling <see cref="RunAsync"/> restarts it from step 1.</para>
/// </summary>
[Alias("Grains.DocumentProcessingGrain")]
public interface IDocumentProcessingGrain : IGrainWithStringKey
{
    /// <summary>
    /// Starts (or restarts) the three-step pipeline from step 1.
    /// Returns the initial status of step 1 after the call.
    /// </summary>
    [Alias("RunAsync")]
    Task<JobStatus> RunAsync();

    /// <summary>Returns the current status and latest progress/output for all three steps.</summary>
    [Alias("GetSummaryAsync")]
    Task<DocumentProcessingSnapshot> GetSummaryAsync();

    /// <summary>
    /// Attempts to cancel all queued (not yet dispatched) step jobs in this pipeline.
    /// </summary>
    /// <returns>
    /// <c>true</c> if at least one queued step was cancelled;
    /// <c>false</c> if no cancellable steps were found.
    /// </returns>
    [Alias("CancelAsync")]
    Task<bool> CancelAsync();
}
