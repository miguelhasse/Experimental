using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RequestProcessor.Benchmarks;

/// <summary>
/// Measures end-to-end throughput: enqueues <see cref="RequestCount"/> no-op requests and
/// waits for all completions, varying both concurrency and request volume.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class EnqueueThroughputBenchmark
{
    private ServiceProvider _sp = null!;
    private RequestPoolService _pool = null!;

    [Params(1, 4, 8)]
    public int Concurrency { get; set; }

    [Params(500, 2000)]
    public int RequestCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _sp = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.None))
            .AddMetrics()
            .Configure<RequestPoolOptions>(o =>
            {
                o.MaxConcurrency = Concurrency;
                o.BoundedCapacity = RequestCount;
            })
            .AddSingleton<IRequestDispatcher>(new NoOpDispatcher())
            .AddSingleton<RequestPoolService>()
            .BuildServiceProvider();

        _pool = _sp.GetRequiredService<RequestPoolService>();
        await _pool.StartAsync(CancellationToken.None);
        await _pool.WhenStarted;
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _pool.StopAsync(CancellationToken.None);
        await _sp.DisposeAsync();
    }

    [Benchmark]
    public async Task DrainAllRequests()
    {
        var pending = new Task<RequestResult>[RequestCount];

        for (int i = 0; i < RequestCount; i++)
        {
            var context = new RequestContext<int>($"req-{i}", i, Priority: RequestPriority.Normal);
            pending[i] = EnqueueAndAwaitAsync(context);
        }

        await Task.WhenAll(pending);
    }

    private async Task<RequestResult> EnqueueAndAwaitAsync(RequestContext context)
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pool.EnqueueAsync(context, result =>
        {
            tcs.TrySetResult(result);
            return Task.CompletedTask;
        });
        return await tcs.Task;
    }
}
