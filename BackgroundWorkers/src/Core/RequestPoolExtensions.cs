namespace RequestProcessor;

/// <summary>
/// Extension methods for <see cref="IRequestPool"/> to support async/await patterns.
/// </summary>
public static class RequestPoolExtensions
{
    /// <summary>
    /// Enqueues a request and returns a task that completes with the dispatch result.
    /// </summary>
    /// <remarks>
    /// If <paramref name="cancellationToken"/> is cancelled, the awaiter completes promptly with
    /// <see cref="OperationCanceledException"/>. The pool also makes a best-effort attempt
    /// to cancel the request via <see cref="IRequestPoolMonitor.TryCancelRequest(string)"/>
    /// — this succeeds only if the request has not yet been dispatched to a worker.
    /// Once dispatched, the request continues to completion regardless of <paramref name="cancellationToken"/>.
    /// </remarks>
    public static async Task<RequestResult> EnqueueAsync(this IRequestPool pool, RequestContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await pool.EnqueueAsync(context, result =>
        {
            tcs.TrySetResult(result);
            return Task.CompletedTask;
        },
        cancellationToken).ConfigureAwait(false);

        // Best-effort cancellation: if the caller's token fires after enqueue but before
        // dispatch, ask the pool to cancel the request. This avoids the "fire-and-forget"
        // failure mode where the caller exits via WaitAsync(cancellationToken) but the request keeps
        // processing and produces side effects the caller has already given up on.
        CancellationTokenRegistration registration = default;
        if (cancellationToken.CanBeCanceled && pool is IRequestPoolMonitor monitor)
        {
            registration = cancellationToken.Register(static state =>
            {
                var (m, id) = ((IRequestPoolMonitor, string))state!;
                try { m.TryCancelRequest(id); } catch { /* best-effort */ }
            },
            (monitor, context.RequestId));
        }

        try
        {
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }
}
