namespace RequestProcessor.Benchmarks;

/// <summary>
/// Minimal no-op dispatcher that completes each request synchronously.
/// Used by all benchmarks to isolate pool overhead from dispatcher work.
/// </summary>
internal sealed class NoOpDispatcher : IRequestDispatcher
{
    public ValueTask<RequestResult> DispatchAsync(RequestContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(new RequestResult(context.RequestId, Success: true, Output: null));
}

/// <summary>
/// No-op mediator handler for <see cref="BenchmarkRequest"/>.
/// </summary>
internal sealed class NoOpBenchmarkHandler : IRequestHandler<BenchmarkRequest>
{
    public ValueTask<RequestResult> HandleAsync(RequestContext<BenchmarkRequest> context, CancellationToken cancellationToken)
        => ValueTask.FromResult(new RequestResult(context.RequestId, Success: true, Output: null));
}

/// <summary>
/// Payload type used by mediator benchmarks.
/// </summary>
internal sealed record BenchmarkRequest(int Value);
