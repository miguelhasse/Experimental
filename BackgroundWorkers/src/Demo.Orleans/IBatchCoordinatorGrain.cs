namespace OrleansSample;

/// <summary>
/// Grain interface for batch coordination using fan-out/fan-in pattern.
/// The grain key is the batch ID (string).
///
/// <para>
/// The coordinator orchestrates processing of multiple items across worker grains,
/// enqueues batch processing requests to the pool, and tracks progress via tracker keys
/// like "{batchId}:worker-0", "{batchId}:worker-1", etc.
/// </para>
/// </summary>
[Alias("Grains.BatchCoordinatorGrain")]
public interface IBatchCoordinatorGrain : IGrainWithStringKey
{
    /// <summary>
    /// Initiates batch processing: fans out items to worker grains and enqueues
    /// a batch request to the request pool.
    /// </summary>
    /// <param name="itemCount">Total number of items to process.</param>
    /// <param name="workerCount">Number of worker grains to distribute items across.</param>
    /// <returns>Current coordination summary after fan-out.</returns>
    [Alias("ProcessBatchAsync")]
    Task<BatchCoordinationSummary> ProcessBatchAsync(int itemCount, int workerCount);

    /// <summary>
    /// Polls all worker grains and returns the current coordination status.
    /// </summary>
    [Alias("GetSummaryAsync")]
    Task<BatchCoordinationSummary> GetSummaryAsync();

    /// <summary>
    /// Attempts to cancel all queued (not yet dispatched) items for this batch.
    /// </summary>
    /// <returns>
    /// <c>true</c> if at least one queued item was cancelled;
    /// <c>false</c> if no cancellable items were found.
    /// </returns>
    [Alias("CancelAsync")]
    Task<bool> CancelAsync();
}
