namespace RequestProcessor.Tests;

public class RequestPoolExtensionsTests
{
    private sealed class StubDispatcher : IRequestDispatcher
    {
        public ValueTask<RequestResult> DispatchAsync(RequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new RequestResult(context.RequestId, true, null));
    }

    [Fact]
    public async Task EnqueueAsync_TaskOverload_ReturnsDispatchResult()
    {
        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
            Task.FromResult(new RequestResult(ctx.RequestId, Success: true, Output: "ok")));

        IRequestPool pool = f.Service;
        var result = await pool.EnqueueAsync(
            new RequestContext<string>("task-overload", "payload"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("task-overload", result.RequestId);
        Assert.Equal("ok", result.Output);
    }

    [Fact]
    public void AddRequestPool_RegistersIRequestDispatcher()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddRequestPool<StubDispatcher>()
            .BuildServiceProvider();

        var dispatcher = sp.GetService<IRequestDispatcher>();
        Assert.NotNull(dispatcher);
        Assert.IsType<StubDispatcher>(dispatcher);
    }

    [Fact]
    public void AddRequestPool_RegistersIRequestPool()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddRequestPool<StubDispatcher>()
            .BuildServiceProvider();

        var pool = sp.GetService<IRequestPool>();
        Assert.NotNull(pool);
        Assert.IsType<RequestPoolService>(pool);
    }

    [Fact]
    public void AddRequestPool_RegistersIHostedService()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddRequestPool<StubDispatcher>()
            .BuildServiceProvider();

        var hostedServices = sp.GetServices<IHostedService>().ToList();
        Assert.Contains(hostedServices, s => s is RequestPoolService);
    }

    [Fact]
    public void AddRequestPool_IRequestPool_And_IHostedService_AreSameInstance()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddRequestPool<StubDispatcher>()
            .BuildServiceProvider();

        var pool = sp.GetRequiredService<IRequestPool>();
        var service = sp.GetServices<IHostedService>().OfType<RequestPoolService>().Single();

        Assert.Same(pool, service);
    }

    [Fact]
    public void AddRequestPool_ConfiguresOptions()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddRequestPool<StubDispatcher>(o =>
            {
                o.MaxConcurrency = 8;
                o.BoundedCapacity = 250;
            })
            .BuildServiceProvider();

        var opts = sp.GetRequiredService<IOptions<RequestPoolOptions>>().Value;

        Assert.Equal(8, opts.MaxConcurrency);
        Assert.Equal(250, opts.BoundedCapacity);
    }

    [Fact]
    public void AddRequestPool_WithoutConfigureOptions_UsesDefaults()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddRequestPool<StubDispatcher>()
            .BuildServiceProvider();

        var opts = sp.GetRequiredService<IOptions<RequestPoolOptions>>().Value;

        Assert.Equal(Environment.ProcessorCount, opts.MaxConcurrency);
        Assert.Equal(1_000, opts.BoundedCapacity);
    }
}
