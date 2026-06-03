using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RequestProcessor.Benchmarks;

/// <summary>
/// Measures weighted round-robin scheduling overhead when mixing High, Normal, and Low priority
/// requests. Tests two weight presets: balanced (1:1:1) and skewed (High=5, Normal=3, Low=1).
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class PrioritySchedulingBenchmark
{
    private const int RequestsPerPriority = 200;

    private ServiceProvider _sp = null!;
    private RequestPoolService _pool = null!;

    /// <summary>Comma-separated High:Normal:Low weights.</summary>
    [Params("5,3,1", "1,1,1")]
    public string Weights { get; set; } = "5,3,1";

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var parts = Weights.Split(',');
        int high = int.Parse(parts[0]);
        int normal = int.Parse(parts[1]);
        int low = int.Parse(parts[2]);

        _sp = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.None))
            .AddMetrics()
            .Configure<RequestPoolOptions>(o =>
            {
                o.MaxConcurrency = 4;
                o.BoundedCapacity = RequestsPerPriority * 3;
                o.PriorityWeights = [low, normal, high];
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
    public async Task MixedPriorityDrain()
    {
        var tasks = new Task<RequestResult>[RequestsPerPriority * 3];
        int idx = 0;

        for (int i = 0; i < RequestsPerPriority; i++)
            tasks[idx++] = EnqueueAndAwaitAsync($"high-{i}", RequestPriority.High);

        for (int i = 0; i < RequestsPerPriority; i++)
            tasks[idx++] = EnqueueAndAwaitAsync($"normal-{i}", RequestPriority.Normal);

        for (int i = 0; i < RequestsPerPriority; i++)
            tasks[idx++] = EnqueueAndAwaitAsync($"low-{i}", RequestPriority.Low);

        await Task.WhenAll(tasks);
    }

    private async Task<RequestResult> EnqueueAndAwaitAsync(string id, RequestPriority priority)
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pool.EnqueueAsync(
            new RequestContext<string>(id, id, Priority: priority),
            result =>
            {
                tcs.TrySetResult(result);
                return Task.CompletedTask;
            });
        return await tcs.Task;
    }
}
