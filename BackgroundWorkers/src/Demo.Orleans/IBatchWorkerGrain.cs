namespace OrleansSample;

/// <summary>
/// Grain interface for submitting a single batch item to the request pool.
/// The grain key is the worker ID (e.g., <c>"{batchId}:worker-0"</c>).
///
/// <para>
/// Follows the <c>JobGrain</c> pattern: <see cref="SubmitItemAsync"/> enqueues a
/// <see cref="BatchWorkerItemRequest"/> into the pool and calls
/// <see cref="Grain.DeactivateOnIdle"/> immediately. The pool worker processes the
/// item and fires the supplied observer references when done.
/// </para>
/// </summary>
[Alias("Grains.BatchWorkerGrain")]
public interface IBatchWorkerGrain : IGrainWithStringKey
{
    /// <summary>
    /// Enqueues the item into the request pool and returns immediately.
    /// Results are delivered asynchronously via the supplied observer references.
    /// </summary>
    [Alias("SubmitItemAsync")]
    Task SubmitItemAsync(
        string itemKey,
        string batchId,
        int itemIndex,
        string data,
        IJobCompletionObserver completionObserver,
        IJobProgressObserver progressObserver,
        IJobDataObserver? dataObserver);

    /// <summary>
    /// Attempts to cancel all queued (not yet dispatched) requests owned by this worker grain.
    /// </summary>
    /// <returns>
    /// <c>true</c> if at least one queued request was cancelled;
    /// <c>false</c> if no cancellable requests were found.
    /// </returns>
    [Alias("CancelAsync")]
    Task<bool> CancelAsync();
}
