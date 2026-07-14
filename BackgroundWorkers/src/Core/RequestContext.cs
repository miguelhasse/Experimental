using System.Diagnostics;

namespace RequestProcessor;

/// <summary>
/// Callback invoked by a request handler to report incremental progress.
/// </summary>
/// <param name="percentComplete">Value between 0–100.</param>
/// <param name="message">Optional human-readable status message.</param>
/// <param name="delta">
/// Optional handler-specific progress data.  The concrete type is defined by the handler
/// and must be handled by whoever wires up the <see cref="RequestContext.OnProgress"/> callback.
/// </param>
public delegate void RequestProgressReporter(int percentComplete, string? message = null, object? delta = null);

/// <summary>
/// Non-generic base for all request contexts.
/// Used by <see cref="IRequestPool"/> and <see cref="IRequestDispatcher"/> boundaries
/// where the concrete request type is not relevant.
/// </summary>
[DebuggerDisplay("RequestId = {RequestId}, Priority = {Priority}")]
public abstract record RequestContext(
    string RequestId,
    RequestPriority Priority = RequestPriority.Normal,
    RequestProgressReporter? OnProgress = null)
{
    /// <summary>
    /// Optional timeout for this request's dispatcher invocation. When set, it overrides
    /// <see cref="RequestPoolOptions.DispatchTimeoutMs"/> because the per-request value is more specific.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Optional correlation identifier added to processing telemetry and log scopes.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Optional key used to fairly round-robin requests within the same priority when
    /// <see cref="RequestPoolOptions.PartitionFairnessEnabled"/> is enabled.
    /// A <see langword="null"/> value uses the shared <c>__default</c> partition.
    /// </summary>
    public string? PartitionKey { get; init; }

    // Returns typeof(TData) for RequestContext<TData>; throws for non-generic subclasses.
    // Used by MediatorRequestDispatcher to resolve the handler without reflection.
    internal virtual Type RequestDataType => throw new InvalidOperationException(
        $"{nameof(MediatorRequestDispatcher)} requires a {nameof(RequestContext)}<TData> instance. " +
        $"Received: {GetType().Name}.");
}

/// <summary>
/// Strongly-typed request context that carries a <typeparamref name="TData"/> payload.
/// Create these when enqueuing requests; handlers receive this type directly.
/// </summary>
public sealed record RequestContext<TData>(
    string RequestId,
    TData Data,
    RequestPriority Priority = RequestPriority.Normal,
    RequestProgressReporter? OnProgress = null)
    : RequestContext(RequestId, Priority, OnProgress)
    where TData : notnull
{
    internal override Type RequestDataType => typeof(TData);
}
