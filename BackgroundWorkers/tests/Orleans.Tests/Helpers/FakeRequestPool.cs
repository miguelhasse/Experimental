namespace Orleans.Tests;

/// <summary>
/// Configurable fake <see cref="IRequestPool"/> for grain integration tests.
/// Call one of the <c>Use*</c> methods before each test to control how the pool
/// responds; the callback is invoked synchronously inside <see cref="EnqueueAsync"/>.
/// When no handler is configured (after <see cref="Hold"/>), the callback is never
/// fired — useful for testing idempotency guards without triggering completion.
/// </summary>
internal sealed class FakeRequestPool : IRequestPool
{
    private volatile Func<RequestContext, RequestResult>? _handler;

    /// <summary>Completes every request immediately with a successful result.</summary>
    public FakeRequestPool UseSuccess(string? output = "ok", JobOutput? typedOutput = null) =>
        SetHandler(ctx => new RequestResult(ctx.RequestId, Success: true, Output: output, TypedOutput: typedOutput));

    /// <summary>Completes every request immediately with the supplied exception.</summary>
    public FakeRequestPool UseError(Exception error) =>
        SetHandler(ctx => new RequestResult(ctx.RequestId, Success: false, Output: null, Error: error));

    /// <summary>Completes every request immediately with <see cref="OperationCanceledException"/>.</summary>
    public FakeRequestPool UseCancel() => UseError(new OperationCanceledException());

    /// <summary>Invokes <see cref="RequestContext.OnProgress"/> with the given percentage before completing.</summary>
    public FakeRequestPool UseResultWithProgress(int percent, string? msg, Func<RequestContext, RequestResult> factory) =>
        SetHandler(ctx =>
        {
            ctx.OnProgress?.Invoke(percent, msg);
            return factory(ctx);
        });

    /// <summary>Uses the provided factory to produce a result for each request.</summary>
    public FakeRequestPool UseResult(Func<RequestContext, RequestResult> factory) => SetHandler(factory);

    /// <summary>
    /// Stops firing callbacks. Requests are accepted but never completed
    /// until a different <c>Use*</c> method is called.
    /// </summary>
    public FakeRequestPool Hold()
    {
        _handler = null;
        return this;
    }

    private FakeRequestPool SetHandler(Func<RequestContext, RequestResult> h)
    {
        _handler = h;
        return this;
    }

    public async ValueTask EnqueueAsync(
        RequestContext context,
        RequestCompletedCallback onCompleted,
        CancellationToken cancellationToken = default)
    {
        var handler = _handler;
        if (handler is not null)
        {
            var result = handler(context);
            await onCompleted(result);
        }
    }

    public async ValueTask EnqueueAsync<TState>(
        RequestContext context,
        TState state,
        Func<TState, RequestResult, ValueTask> callback,
        CancellationToken cancellationToken = default)
    {
        var handler = _handler;
        if (handler is not null)
        {
            var result = handler(context);
            await callback(state, result);
        }
    }
}
