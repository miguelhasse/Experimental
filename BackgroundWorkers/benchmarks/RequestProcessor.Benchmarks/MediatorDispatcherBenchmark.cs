using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RequestProcessor.Benchmarks;

/// <summary>
/// Compares the routing overhead of <see cref="MediatorRequestDispatcher"/> (type-keyed handler
/// lookup) against a direct <see cref="NoOpDispatcher"/> that skips any resolution step.
/// Both dispatchers perform the same no-op work; the delta is pure framework overhead.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class MediatorDispatcherBenchmark
{
    private const int RequestCount = 500;

    private ServiceProvider _directSp = null!;
    private RequestPoolService _directPool = null!;

    private ServiceProvider _mediatorSp = null!;
    private RequestPoolService _mediatorPool = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directSp = BuildDirectProvider();
        _directPool = _directSp.GetRequiredService<RequestPoolService>();
        await _directPool.StartAsync(CancellationToken.None);
        await _directPool.WhenStarted;

        _mediatorSp = BuildMediatorProvider();
        _mediatorPool = _mediatorSp.GetRequiredService<RequestPoolService>();
        await _mediatorPool.StartAsync(CancellationToken.None);
        await _mediatorPool.WhenStarted;
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _directPool.StopAsync(CancellationToken.None);
        await _directSp.DisposeAsync();

        await _mediatorPool.StopAsync(CancellationToken.None);
        await _mediatorSp.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public Task DirectDispatch() => DrainPool(_directPool);

    [Benchmark]
    public Task MediatorDispatch() => DrainPool(_mediatorPool);

    private static async Task DrainPool(RequestPoolService pool)
    {
        var tasks = new Task<RequestResult>[RequestCount];
        for (int i = 0; i < RequestCount; i++)
        {
            var context = new RequestContext<BenchmarkRequest>($"req-{i}", new BenchmarkRequest(i));
            tasks[i] = EnqueueAndAwaitAsync(pool, context);
        }
        await Task.WhenAll(tasks);
    }

    private static async Task<RequestResult> EnqueueAndAwaitAsync(RequestPoolService pool, RequestContext context)
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await pool.EnqueueAsync(context, result =>
        {
            tcs.TrySetResult(result);
            return Task.CompletedTask;
        });
        return await tcs.Task;
    }

    private static ServiceProvider BuildDirectProvider()
        => new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.None))
            .AddMetrics()
            .Configure<RequestPoolOptions>(o =>
            {
                o.MaxConcurrency = 4;
                o.BoundedCapacity = RequestCount;
            })
            .AddSingleton<IRequestDispatcher>(new NoOpDispatcher())
            .AddSingleton<RequestPoolService>()
            .BuildServiceProvider();

    private static ServiceProvider BuildMediatorProvider()
        => new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.None))
            .AddMediatorRequestPool(o =>
            {
                o.MaxConcurrency = 4;
                o.BoundedCapacity = RequestCount;
            })
            .AddRequestHandler<BenchmarkRequest, NoOpBenchmarkHandler>()
            .BuildServiceProvider();
}

