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
    private volatile PoolCapture? _capture;

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
        _capture = null;
        return this;
    }

    /// <summary>
    /// Accepts requests and keeps their progress reporter and completion callback, so a test can drive them later
    /// exactly like a slow real worker would: report progress, deliver partial results, and complete, possibly
    /// long after the grain that submitted the job has been deactivated. Replaced by the next <c>Use*</c>/<c>Hold</c> call.
    /// </summary>
    public PoolCapture Capture()
    {
        var capture = new PoolCapture();
        _handler = null;
        _capture = capture;
        return capture;
    }

    private FakeRequestPool SetHandler(Func<RequestContext, RequestResult> h)
    {
        _handler = h;
        _capture = null;
        return this;
    }

    public async ValueTask EnqueueAsync(
        RequestContext context,
        RequestCompletedCallback onCompleted,
        CancellationToken cancellationToken = default)
    {
        if (_capture is { } capture)
        {
            capture.Add(new HeldRequest(context, onCompleted));
            return;
        }

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

/// <summary>Requests accepted by <see cref="FakeRequestPool.Capture"/>, in the order they were enqueued.</summary>
internal sealed class PoolCapture
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<HeldRequest> _requests = new();

    public int Count => _requests.Count;

    public IReadOnlyList<HeldRequest> Requests => _requests.ToArray();

    /// <summary>
    /// Like the real pool, rejects a request whose id equals one that is still pending (held and not yet completed).
    /// </summary>
    internal void Add(HeldRequest request)
    {
        lock (_requests)
        {
            if (_requests.Any(r => !r.IsCompleted && r.Context.RequestId == request.Context.RequestId))
                throw new ArgumentException($"A request with RequestId '{request.Context.RequestId}' is already enqueued.", nameof(request));

            _requests.Enqueue(request);
        }
    }
}

/// <summary>One request held by the fake pool; the test plays the worker that is processing it.</summary>
internal sealed class HeldRequest(RequestContext context, RequestCompletedCallback onCompleted)
{
    public RequestContext Context { get; } = context;

    /// <summary>What a worker does when it reports progress (<paramref name="delta"/> may be a <see cref="PartialResultDelta"/>).</summary>
    public void Report(int percent, string? message = null, object? delta = null) =>
        Context.OnProgress?.Invoke(percent, message, delta);

    /// <summary>What a worker does when it finishes.</summary>
    /// <summary>True once the worker has completed the request (it is no longer pending).</summary>
    public bool IsCompleted { get; private set; }

    public Task CompleteAsync(RequestResult result)
    {
        IsCompleted = true;
        return onCompleted(result);
    }

    public Task CompleteAsync(string output = "done") =>
        CompleteAsync(new RequestResult(Context.RequestId, Success: true, Output: output, TypedOutput: new TextJobOutput(output)));
}
