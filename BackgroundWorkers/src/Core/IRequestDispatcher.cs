namespace RequestProcessor;

/// <summary>
/// Performs the actual work for a single request.
/// Implement and register this interface to plug in any dispatch logic
/// (HTTP calls, database writes, message-bus publishing, etc.).
/// </summary>
public interface IRequestDispatcher
{
    /// <summary>
    /// Processes <paramref name="context"/> and returns a result.
    /// Implementations must be thread-safe; the pool may invoke this
    /// concurrently from multiple worker threads.
    /// </summary>
    ValueTask<RequestResult> DispatchAsync(RequestContext context, CancellationToken cancellationToken);
}
