using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RequestProcessor.Benchmarks;

/// <summary>
/// Measures the allocation difference between the closure-based and the state-based
/// <see cref="IRequestPool.EnqueueAsync"/> overloads.
/// <para>
/// The closure overload (<c>Func&lt;RequestResult, ValueTask&gt;</c>) typically captures
/// variables into a heap-allocated closure object. The generic state overload
/// (<c>EnqueueAsync&lt;TState&gt;</c>) passes state through a struct-typed delegate,
/// allowing the JIT to elide the allocation when <typeparamref name="TState"/> is a
/// value type or a single captured reference that fits in a display class.
/// </para>
/// </summary>
[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class CallbackAllocationBenchmark
{
    private const int RequestCount = 200;

    private ServiceProvider _sp = null!;
    private RequestPoolService _pool = null!;
    private string[] _requestIds = [];

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _requestIds = Enumerable.Range(0, RequestCount).Select(i => $"req-{i}").ToArray();

        _sp = new ServiceCollection()
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

    /// <summary>
    /// Uses the non-generic <see cref="IRequestPool.EnqueueAsync"/> overload with a
    /// lambda that closes over a <see cref="TaskCompletionSource{T}"/> — allocates a
    /// closure object per request.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task ClosureCallback()
    {
        var tasks = new Task<RequestResult>[RequestCount];
        for (int i = 0; i < RequestCount; i++)
        {
            var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = new RequestContext<int>(_requestIds[i], i);
            await _pool.EnqueueAsync(context, result =>
            {
                tcs.TrySetResult(result);
                return Task.CompletedTask;
            });
            tasks[i] = tcs.Task;
        }
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Uses the generic <see cref="IRequestPool.EnqueueAsync{TState}"/> overload, passing
    /// the <see cref="TaskCompletionSource{T}"/> as <c>TState</c> — avoids the closure alloc.
    /// </summary>
    [Benchmark]
    public async Task StatefulCallback()
    {
        var tasks = new Task<RequestResult>[RequestCount];
        for (int i = 0; i < RequestCount; i++)
        {
            var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = new RequestContext<int>(_requestIds[i], i);
            await _pool.EnqueueAsync(
                context,
                tcs,
                static (state, result) =>
                {
                    state.TrySetResult(result);
                    return ValueTask.CompletedTask;
                });
            tasks[i] = tcs.Task;
        }
        await Task.WhenAll(tasks);
    }
}
