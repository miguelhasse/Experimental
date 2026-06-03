namespace RequestProcessor.Tests.Helpers;

/// <summary>
/// Builds and starts a <see cref="RequestPoolService"/> wired through a minimal DI container.
/// Call <see cref="StopAndDisposeAsync"/> (or wrap in <c>await using</c>) to clean up.
/// </summary>
public sealed class ServiceFactory : IAsyncDisposable
{
    private readonly ServiceProvider _sp;

    public RequestPoolService Service { get; }

    private ServiceFactory(ServiceProvider sp, RequestPoolService service)
    {
        _sp = sp;
        Service = service;
    }

    /// <summary>
    /// Creates a <see cref="ServiceFactory"/> with a started <see cref="RequestPoolService"/>.
    /// </summary>
    /// <param name="dispatch">Dispatcher implementation for the test.</param>
    /// <param name="concurrency">Worker count (default 2).</param>
    /// <param name="capacity">Per-priority channel capacity (default 100).</param>
    /// <param name="configure">Optional delegate applied after defaults, e.g. to set <see cref="RequestPoolOptions.TaskSchedulerFactory"/>.</param>
    public static async Task<ServiceFactory> CreateAsync(
        Func<RequestContext, CancellationToken, Task<RequestResult>> dispatch,
        int concurrency = 2,
        int capacity = 100,
        Action<RequestPoolOptions>? configure = null)
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .Configure<RequestPoolOptions>(o =>
            {
                o.MaxConcurrency = concurrency;
                o.BoundedCapacity = capacity;
                configure?.Invoke(o);
            })
            .AddSingleton<IRequestDispatcher>(new FakeDispatcher(dispatch))
            .AddSingleton<RequestPoolService>()
            .BuildServiceProvider();

        var service = sp.GetRequiredService<RequestPoolService>();
        await service.StartAsync(CancellationToken.None);
        // BackgroundService.StartAsync (NET 8+) schedules ExecuteAsync via Task.Run, passing the
        // stoppingCts token to both the lambda and Task.Run itself.  If StopAsync fires that token
        // before the ThreadPool assigns a thread to the task, the task is cancelled before workers
        // ever start.  WhenStarted completes once ExecuteAsync is actually running, eliminating
        // this scheduling race in tests.
        await service.WhenStarted;
        return new ServiceFactory(sp, service);
    }

    public async ValueTask DisposeAsync()
    {
        // Guard against double-stop: tests that explicitly call StopAsync have already
        // completed the channel writer; catching ChannelClosedException here keeps
        // DisposeAsync safe to call regardless.
        try
        {
            await Service.StopAsync(CancellationToken.None);
        }
        catch (System.Threading.Channels.ChannelClosedException) { }

        await _sp.DisposeAsync();
    }
}

