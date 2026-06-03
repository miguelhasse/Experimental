using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RequestProcessor.Benchmarks;

/// <summary>
/// Measures the overhead of per-partition fair scheduling vs the default single-channel behaviour.
/// Requests are spread evenly across <see cref="PartitionCount"/> distinct partition keys.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class PartitionFairnessBenchmark
{
    private const int TotalRequests = 600;

    private ServiceProvider _sp = null!;
    private RequestPoolService _pool = null!;

    [Params(false, true)]
    public bool PartitionFairnessEnabled { get; set; }

    [Params(1, 4, 16)]
    public int PartitionCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _sp = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.None))
            .AddMetrics()
            .Configure<RequestPoolOptions>(o =>
            {
                o.MaxConcurrency = 4;
                o.BoundedCapacity = TotalRequests;
                o.PartitionFairnessEnabled = PartitionFairnessEnabled;
                o.PartitionCapacity = (TotalRequests + PartitionCount - 1) / Math.Max(1, PartitionCount);
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
    public async Task PartitionedDrain()
    {
        var tasks = new Task<RequestResult>[TotalRequests];

        for (int i = 0; i < TotalRequests; i++)
        {
            string partitionKey = $"partition-{i % PartitionCount}";
            var context = new RequestContext<int>($"req-{i}", i)
            {
                PartitionKey = PartitionFairnessEnabled ? partitionKey : null,
            };
            tasks[i] = EnqueueAndAwaitAsync(context);
        }

        await Task.WhenAll(tasks);
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
