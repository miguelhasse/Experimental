namespace RequestProcessor.Tests;

public sealed class MediatorRequestDispatcherTests
{
    private static IServiceProvider BuildSp(Action<IServiceCollection> configure)
    {
        var sc = new ServiceCollection().AddLogging().AddMetrics();
        configure(sc);
        return sc.BuildServiceProvider();
    }

    [Fact]
    public async Task DispatchAsync_RoutesToCorrectHandler_WhenHandlerRegistered()
    {
        var sp = BuildSp(sc => sc.AddRequestHandler<MediatorTestRequest, MediatorCapturingHandler>());

        var dispatcher = new MediatorRequestDispatcher(sp);
        var context = new RequestContext<MediatorTestRequest>("id-1", new MediatorTestRequest("hello"));

        var result = await dispatcher.DispatchAsync(context, CancellationToken.None);

        var handler = (MediatorCapturingHandler)sp.GetRequiredService<IRequestHandler<MediatorTestRequest>>();
        Assert.True(handler.WasCalled);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task DispatchAsync_ThrowsInvalidOperationException_WhenContextIsNonGenericBase()
    {
        var sp = BuildSp(_ => { });
        var dispatcher = new MediatorRequestDispatcher(sp);
        var context = new NonGenericTestContext("id-2");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync(context, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DispatchAsync_ThrowsInvalidOperationException_WhenHandlerNotRegistered()
    {
        var sp = BuildSp(_ => { });
        var dispatcher = new MediatorRequestDispatcher(sp);
        var context = new RequestContext<MediatorOtherRequest>("id-3", new MediatorOtherRequest("x"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync(context, CancellationToken.None).AsTask());

        Assert.Contains("No handler registered", ex.Message);
    }

    [Fact]
    public async Task DispatchAsync_InvokesProgressReporter_WhenHandlerCallsOnProgress()
    {
        int? reportedPercent = null;
        string? reportedMessage = null;

        var sp = BuildSp(sc => sc.AddRequestHandler<MediatorTestRequest, MediatorProgressReportingHandler>());

        var dispatcher = new MediatorRequestDispatcher(sp);
        var context = new RequestContext<MediatorTestRequest>(
            "id-4", new MediatorTestRequest("progress-test"),
            OnProgress: (pct, msg, delta) => { reportedPercent = pct; reportedMessage = msg; });

        await dispatcher.DispatchAsync(context, CancellationToken.None);

        Assert.Equal(50, reportedPercent);
        Assert.Equal("half", reportedMessage);
    }
    [Fact]
    public async Task DispatchAsync_OnProgressDelta_IsForwardedToCallback()
    {
        object? capturedDelta = null;

        var sp = BuildSp(sc => sc.AddRequestHandler<MediatorTestRequest, MediatorDeltaProgressHandler>());
        var dispatcher = new MediatorRequestDispatcher(sp);
        var context = new RequestContext<MediatorTestRequest>(
            "id-5", new MediatorTestRequest("delta-test"),
            OnProgress: (pct, msg, delta) => { capturedDelta = delta; });

        await dispatcher.DispatchAsync(context, CancellationToken.None);

        Assert.NotNull(capturedDelta);
        Assert.IsType<MediatorTestDelta>(capturedDelta);
        Assert.Equal(42, ((MediatorTestDelta)capturedDelta).Value);
    }

    [Fact]
    public async Task DispatchAsync_UsesCachedSingletonHandlerAcrossDispatches()
    {
        var sp = BuildSp(sc => sc
            .AddMediatorRequestPool()
            .AddRequestHandler<MediatorCacheRequest, MediatorCachingHandler>());

        var dispatcher = sp.GetRequiredService<IRequestDispatcher>();
        var handler = (MediatorCachingHandler)sp.GetRequiredService<IRequestHandler<MediatorCacheRequest>>();

        await dispatcher.DispatchAsync(
            new RequestContext<MediatorCacheRequest>("id-6", new MediatorCacheRequest("first")),
            CancellationToken.None);
        await dispatcher.DispatchAsync(
            new RequestContext<MediatorCacheRequest>("id-7", new MediatorCacheRequest("second")),
            CancellationToken.None);

        Assert.Equal(2, handler.DispatchCount);
        Assert.Equal(["id-6", "id-7"], handler.RequestIds);
    }

    [Fact]
    public async Task DispatchAsync_OnProgressCallbackThrows_ExceptionPropagatesFromDispatch()
    {
        var sp = BuildSp(sc => sc.AddRequestHandler<MediatorTestRequest, MediatorProgressReportingHandler>());
        var dispatcher = new MediatorRequestDispatcher(sp);
        var context = new RequestContext<MediatorTestRequest>(
            "id-8", new MediatorTestRequest("throw-test"),
            OnProgress: (_, _, _) => throw new InvalidOperationException("observer exploded"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync(context, CancellationToken.None).AsTask());
    }
}
internal record MediatorTestRequest(string Value);
internal record MediatorOtherRequest(string Value);
internal record MediatorCacheRequest(string Value);
internal sealed record NonGenericTestContext(string RequestId) : RequestContext(RequestId);

internal sealed class MediatorCapturingHandler : IRequestHandler<MediatorTestRequest>
{
    public bool WasCalled { get; private set; }

    public ValueTask<RequestResult> HandleAsync(RequestContext<MediatorTestRequest> context, CancellationToken cancellationToken)
    {
        WasCalled = true;
        return ValueTask.FromResult(new RequestResult(context.RequestId, true, null));
    }
}

internal sealed class MediatorProgressReportingHandler : IRequestHandler<MediatorTestRequest>
{
    public ValueTask<RequestResult> HandleAsync(RequestContext<MediatorTestRequest> context, CancellationToken cancellationToken)
    {
        context.OnProgress?.Invoke(50, "half");
        return ValueTask.FromResult(new RequestResult(context.RequestId, true, null));
    }
}

internal sealed class MediatorCachingHandler : IRequestHandler<MediatorCacheRequest>
{
    private readonly List<string> requestIds = [];

    public int DispatchCount => requestIds.Count;

    public string[] RequestIds => [.. requestIds];

    public ValueTask<RequestResult> HandleAsync(RequestContext<MediatorCacheRequest> context, CancellationToken cancellationToken)
    {
        requestIds.Add(context.RequestId);
        return ValueTask.FromResult(new RequestResult(context.RequestId, true, context.Data.Value));
    }
}

internal record MediatorTestDelta(int Value);

internal sealed class MediatorDeltaProgressHandler : IRequestHandler<MediatorTestRequest>
{
    public ValueTask<RequestResult> HandleAsync(RequestContext<MediatorTestRequest> context, CancellationToken cancellationToken)
    {
        context.OnProgress?.Invoke(100, "done", new MediatorTestDelta(42));
        return ValueTask.FromResult(new RequestResult(context.RequestId, true, null));
    }
}
