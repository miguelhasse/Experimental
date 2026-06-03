namespace RequestProcessor;

/// <summary>
/// Entry point for submitting requests to the background processing pool.
/// Resolved from DI as a singleton; enqueue from any thread or hosted service.
/// </summary>
public interface IRequestPool
{
    /// <summary>
    /// Enqueues <paramref name="context"/> for processing.
    /// <paramref name="onCompleted"/> is awaited once the dispatcher finishes,
    /// on whichever worker thread handled the request.
    /// </summary>
    /// <remarks>
    /// Blocks (asynchronously) only when the internal bounded channel is full.
    /// </remarks>
    ValueTask EnqueueAsync(
        RequestContext context,
        RequestCompletedCallback onCompleted,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues <paramref name="context"/> for processing, passing <paramref name="state"/>
    /// to <paramref name="callback"/> when the dispatcher finishes.
    /// </summary>
    /// <remarks>
    /// Use this overload to avoid closure allocations in completion callbacks.
    /// </remarks>
    ValueTask EnqueueAsync<TState>(
        RequestContext context,
        TState state,
        Func<TState, RequestResult, ValueTask> callback,
        CancellationToken cancellationToken = default);
}
