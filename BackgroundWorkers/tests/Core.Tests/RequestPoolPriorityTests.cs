namespace RequestProcessor.Tests;

/// <summary>
/// Tests for priority-ordering guarantees and the <see cref="RequestPoolOptions.TaskSchedulerFactory"/> hook.
/// </summary>
public class RequestPoolPriorityTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a factory (concurrency=1) whose first request blocks execution
    /// until <paramref name="release"/> is signalled, giving callers a window to
    /// enqueue additional items before the worker drains the channels.
    /// </summary>
    private static async Task<ServiceFactory> CreateBlockedWorkerFactoryAsync(
        TaskCompletionSource workerBlocked,
        TaskCompletionSource release,
        int capacity = 50)
        => await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            if (ctx.RequestId == "blocker")
            {
                workerBlocked.TrySetResult();
                await release.Task;
            }
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: capacity);

    // ─── Ordering: High > Normal ──────────────────────────────────────────────

    [Fact]
    public async Task High_ProcessedBefore_Normal_WhenBothQueued()
    {
        var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;

        await using var f = await CreateBlockedWorkerFactoryAsync(workerBlocked, release);

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Enqueue Normal first, then High — the pool must prefer High regardless of arrival order.
        await f.Service.EnqueueAsync(new RequestContext<string>("normal", "p", Priority: RequestPriority.Normal),
            _ => { order.Enqueue("normal"); if (Interlocked.Increment(ref received) == 2) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(new RequestContext<string>("high", "p", Priority: RequestPriority.High),
            _ => { order.Enqueue("high"); if (Interlocked.Increment(ref received) == 2) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        release.TrySetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["high", "normal"], order.ToArray());
    }

    // ─── Ordering: all three levels together ──────────────────────────────────

    [Fact]
    public async Task AllThreePriorities_ProcessedInOrder_HighThenNormalThenLow()
    {
        var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;

        await using var f = await CreateBlockedWorkerFactoryAsync(workerBlocked, release);

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Enqueue in reverse priority order to prove arrival order is irrelevant.
        await f.Service.EnqueueAsync(new RequestContext<string>("low", "p", Priority: RequestPriority.Low),
            _ => { order.Enqueue("low"); if (Interlocked.Increment(ref received) == 3) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("normal", "p", Priority: RequestPriority.Normal),
            _ => { order.Enqueue("normal"); if (Interlocked.Increment(ref received) == 3) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("high", "p", Priority: RequestPriority.High),
            _ => { order.Enqueue("high"); if (Interlocked.Increment(ref received) == 3) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        release.TrySetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["high", "normal", "low"], order.ToArray());
    }

    // ─── Multiple items at the same priority ──────────────────────────────────

    [Fact]
    public async Task ThreeHighItems_AllProcessedBefore_NormalItem()
    {
        var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;
        const int total = 4; // 3 High + 1 Normal

        await using var f = await CreateBlockedWorkerFactoryAsync(workerBlocked, release);

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Enqueue Normal before all High items — High must still drain first.
        await f.Service.EnqueueAsync(
            new RequestContext<string>("normal-1", "p", Priority: RequestPriority.Normal),
            _ => { order.Enqueue("normal"); if (Interlocked.Increment(ref received) == total) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        for (int i = 1; i <= 3; i++)
        {
            await f.Service.EnqueueAsync(
                new RequestContext<string>($"high-{i}", "p", Priority: RequestPriority.High),
                _ => { order.Enqueue("high"); if (Interlocked.Increment(ref received) == total) allDone.TrySetResult(); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);
        }

        release.TrySetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var items = order.ToArray();
        int normalIndex = Array.IndexOf(items, "normal");

        Assert.Equal(total, items.Length);
        Assert.Equal(total - 1, normalIndex);  // "normal" must be last
        Assert.All(items[..normalIndex], label => Assert.Equal("high", label));
    }

    [Fact]
    public async Task FiveLowItems_AllProcessedAfter_TwoHighItems()
    {
        var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;
        const int total = 7; // 2 High + 5 Low

        await using var f = await CreateBlockedWorkerFactoryAsync(workerBlocked, release);

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        for (int i = 1; i <= 5; i++)
        {
            await f.Service.EnqueueAsync(
                new RequestContext<string>($"low-{i}", "p", Priority: RequestPriority.Low),
                _ => { order.Enqueue("low"); if (Interlocked.Increment(ref received) == total) allDone.TrySetResult(); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);
        }

        for (int i = 1; i <= 2; i++)
        {
            await f.Service.EnqueueAsync(
                new RequestContext<string>($"high-{i}", "p", Priority: RequestPriority.High),
                _ => { order.Enqueue("high"); if (Interlocked.Increment(ref received) == total) allDone.TrySetResult(); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);
        }

        release.TrySetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var items = order.ToArray();
        Assert.Equal(total, items.Length);
        Assert.Equal("high", items[0]);
        Assert.Equal("high", items[1]);
        Assert.All(items[2..], label => Assert.Equal("low", label));
    }

    // ─── TaskSchedulerFactory ─────────────────────────────────────────────────

    [Fact]
    public async Task TaskSchedulerFactory_IsInvokedForEachPriority_AtStartup()
    {
        // Factory is called exactly once per RequestPriority (3 values) at service
        // construction — not per dispatched request. This test verifies all three
        // priorities are seen and the returned scheduler is used for dispatches.
        var invocations = new ConcurrentQueue<RequestPriority>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;
        const int total = 3;

        // A non-default scheduler is required to exercise the Task.Factory.StartNew path.
        var customScheduler = new ConcurrentExclusiveSchedulerPair().ConcurrentScheduler;

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)),
            configure: opts =>
            {
                opts.TaskSchedulerFactory = priority =>
                {
                    invocations.Enqueue(priority);
                    return customScheduler;
                };
            });

        // Factory is already called by this point (during construction) — 3 items in the queue.
        var startupInvocations = invocations.ToArray();
        Assert.Equal(total, startupInvocations.Length);
        Assert.Contains(RequestPriority.High, startupInvocations);
        Assert.Contains(RequestPriority.Normal, startupInvocations);
        Assert.Contains(RequestPriority.Low, startupInvocations);

        // Dispatching requests must NOT trigger additional factory calls.
        Task OnDone(RequestResult _)
        {
            if (Interlocked.Increment(ref received) == total) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        await f.Service.EnqueueAsync(new RequestContext<string>("r-high", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("r-normal", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("r-low", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Still exactly 3 invocations total — the 3 requests added none.
        Assert.Equal(total, invocations.Count);
    }

    [Fact]
    public async Task TaskSchedulerFactory_IsInvokedExactlyOncePerPriority_NotPerRequest()
    {
        // Submit many requests per priority; verify the factory is still called exactly 3 times.
        var invocationCount = 0;
        const int requestsPerPriority = 10;
        const int totalRequests = requestsPerPriority * 3;
        int completed = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var customScheduler = new ConcurrentExclusiveSchedulerPair().ConcurrentScheduler;

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)),
            concurrency: 4,
            configure: opts => opts.TaskSchedulerFactory = _ =>
            {
                Interlocked.Increment(ref invocationCount);
                return customScheduler;
            });

        Task OnDone(RequestResult _)
        {
            if (Interlocked.Increment(ref completed) == totalRequests) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        for (int i = 0; i < requestsPerPriority; i++)
        {
            await f.Service.EnqueueAsync(new RequestContext<string>($"h-{i}", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);
            await f.Service.EnqueueAsync(new RequestContext<string>($"n-{i}", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);
            await f.Service.EnqueueAsync(new RequestContext<string>($"l-{i}", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);
        }

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        // Factory is called 3 times at construction, never again during dispatch.
        Assert.Equal(3, invocationCount);
        Assert.Equal(totalRequests, completed);
    }

    [Fact]
    public async Task TaskSchedulerFactory_CustomScheduler_RequestCompletesSuccessfully()
    {
        var done = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new ConcurrentExclusiveSchedulerPair().ConcurrentScheduler;

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, "from-scheduler")),
            configure: opts => opts.TaskSchedulerFactory = _ => scheduler);

        await f.Service.EnqueueAsync(
            new RequestContext<string>("r1", "p", Priority: RequestPriority.High),
            r => { done.TrySetResult(r); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal("from-scheduler", result.Output);
    }

    [Fact]
    public async Task TaskSchedulerFactory_ReturningNull_FallsBackToDefaultScheduler()
    {
        var done = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)),
            configure: opts => opts.TaskSchedulerFactory = _ => null!);

        await f.Service.EnqueueAsync(new RequestContext<string>("r1", "p"), r => { done.TrySetResult(r); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task TaskSchedulerFactory_DifferentSchedulerPerPriority_AllRequestsComplete()
    {
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;
        const int total = 3;

        var highScheduler = new ConcurrentExclusiveSchedulerPair().ConcurrentScheduler;
        var normalScheduler = new ConcurrentExclusiveSchedulerPair().ConcurrentScheduler;
        var lowScheduler = new ConcurrentExclusiveSchedulerPair().ConcurrentScheduler;

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)),
            configure: opts =>
            {
                opts.TaskSchedulerFactory = priority => priority switch
                {
                    RequestPriority.High => highScheduler,
                    RequestPriority.Normal => normalScheduler,
                    RequestPriority.Low => lowScheduler,
                    _ => TaskScheduler.Default,
                };
            });

        Task OnDone(RequestResult _)
        {
            if (Interlocked.Increment(ref received) == total) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        await f.Service.EnqueueAsync(new RequestContext<string>("r-high", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("r-normal", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("r-low", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(total, received);
    }

    // ─── Weighted scheduling: starvation prevention ───────────────────────────

    [Fact]
    public async Task LowPriority_IsNotStarved_WhenHighPriorityChannelSaturated()
    {
        // Arrange: pool with 4 workers; dispatcher completes instantly.
        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)),
            concurrency: 4, capacity: 500);

        var lowDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int lowCompleted = 0;
        const int lowTotal = 5;

        // Keep flooding High-priority work in the background for the duration of the test.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var highFlood = Task.Run(async () =>
        {
            int id = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                var reqId = $"high-{id++}";
                try
                {
                    await f.Service.EnqueueAsync(
                        new RequestContext<string>(reqId, "p", Priority: RequestPriority.High),
                        _ => Task.CompletedTask,
                        cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (System.Threading.Channels.ChannelClosedException) { break; }
                await Task.Yield();
            }
        }, cts.Token);

        // Give the flood a moment to saturate the High channel before enqueuing Low items.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        for (int i = 0; i < lowTotal; i++)
        {
            await f.Service.EnqueueAsync(
                new RequestContext<string>($"low-{i}", "p", Priority: RequestPriority.Low),
                _ => { if (Interlocked.Increment(ref lowCompleted) == lowTotal) lowDone.TrySetResult(); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);
        }

        // Act / Assert: all Low items must complete within 10 s despite continuous High load.
        await lowDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(lowTotal, lowCompleted);

        await cts.CancelAsync();
        await highFlood; // drain
    }

    [Fact]
    public async Task WeightRatio_IsApproximatelyRespected_UnderSteadyLoad()
    {
        // Arrange: pool with default weights { Low=1, Normal=3, High=5 } and 4 workers.
        // Enqueue 90 items simultaneously: 50 High + 30 Normal + 10 Low.
        const int highCount = 50;
        const int normalCount = 30;
        const int lowCount = 10;
        const int total = highCount + normalCount + lowCount;

        int highCompleted = 0;
        int normalCompleted = 0;
        int lowCompleted = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            async (ctx, ct) => { await Task.Yield(); return new RequestResult(ctx.RequestId, true, null); },
            concurrency: 4, capacity: 200);

        for (int i = 0; i < highCount; i++)
        {
            var id = $"h-{i}";
            await f.Service.EnqueueAsync(
                new RequestContext<string>(id, "p", Priority: RequestPriority.High),
                _ => { Interlocked.Increment(ref highCompleted); if (highCompleted + normalCompleted + lowCompleted == total) allDone.TrySetResult(); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);
        }
        for (int i = 0; i < normalCount; i++)
        {
            var id = $"n-{i}";
            await f.Service.EnqueueAsync(
                new RequestContext<string>(id, "p", Priority: RequestPriority.Normal),
                _ => { Interlocked.Increment(ref normalCompleted); if (highCompleted + normalCompleted + lowCompleted == total) allDone.TrySetResult(); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);
        }
        for (int i = 0; i < lowCount; i++)
        {
            var id = $"l-{i}";
            await f.Service.EnqueueAsync(
                new RequestContext<string>(id, "p", Priority: RequestPriority.Low),
                _ => { Interlocked.Increment(ref lowCompleted); if (highCompleted + normalCompleted + lowCompleted == total) allDone.TrySetResult(); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);
        }

        // Act: wait for all completions.
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert: all items completed (no loss).
        Assert.Equal(highCount, highCompleted);
        Assert.Equal(normalCount, normalCompleted);
        Assert.Equal(lowCount, lowCompleted);
    }

    // ─── Priority aging ───────────────────────────────────────────────────────

    [Fact]
    public async Task PriorityAging_PromotesLowRequest_AfterThreshold()
    {
        var timeProvider = new FakeTimeProvider();
        var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            if (ctx.RequestId == "blocker")
            {
                workerBlocked.TrySetResult();
                await release.Task;
            }

            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 20, configure: opts =>
        {
            opts.PriorityAgingThreshold = TimeSpan.FromSeconds(10);
            opts.PriorityAgingScanInterval = TimeSpan.FromSeconds(1);
            opts.TimeProvider = timeProvider;
        });

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p", Priority: RequestPriority.High), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(new RequestContext<string>("aged-low", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await WaitForQueueDepthAsync(f.Service, s => s.QueueDepthNormal > 0);

        await f.Service.EnqueueAsync(new RequestContext<string>("fresh-normal", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);

        release.TrySetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["aged-low", "fresh-normal"], order.ToArray());

        Task OnDone(RequestResult result)
        {
            order.Enqueue(result.RequestId);
            if (Interlocked.Increment(ref received) == 2) allDone.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PriorityAging_Disabled_DoesNotPromoteLowRequest()
    {
        var timeProvider = new FakeTimeProvider();
        var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            if (ctx.RequestId == "blocker")
            {
                workerBlocked.TrySetResult();
                await release.Task;
            }

            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 20, configure: opts =>
        {
            opts.PriorityAgingThreshold = null;
            opts.PriorityAgingScanInterval = TimeSpan.FromSeconds(1);
            opts.TimeProvider = timeProvider;
        });

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p", Priority: RequestPriority.High), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(new RequestContext<string>("aged-low", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("fresh-normal", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);

        release.TrySetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["fresh-normal", "aged-low"], order.ToArray());

        Task OnDone(RequestResult result)
        {
            order.Enqueue(result.RequestId);
            if (Interlocked.Increment(ref received) == 2) allDone.TrySetResult();
            return Task.CompletedTask;
        }
    }

    // ─── MaxConcurrentPerPriority ─────────────────────────────────────────────

    [Fact]
    public async Task MaxConcurrentPerPriority_CapHigh_LimitsParallelism()
    {
        // Cap High concurrency at 1. Three High requests that each hold a gate
        // should run strictly sequentially, not in parallel.
        const int cap = 1;
        int maxObservedConcurrency = 0;
        int currentConcurrency = 0;

        await using var f = await ServiceFactory.CreateAsync(
            async (ctx, ct) =>
            {
                int c = Interlocked.Increment(ref currentConcurrency);
                InterlockedMax(ref maxObservedConcurrency, c);
                await Task.Delay(30, ct);
                Interlocked.Decrement(ref currentConcurrency);
                return new RequestResult(ctx.RequestId, true, null);
            },
            concurrency: 4,
            configure: opts => opts.MaxConcurrentPerPriority = [0, 0, cap]);  // [Low, Normal, High]

        int completed = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int requests = 3;

        Task OnDone(RequestResult _)
        {
            if (Interlocked.Increment(ref completed) == requests) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        for (int i = 0; i < requests; i++)
            await f.Service.EnqueueAsync(new RequestContext<string>($"h-{i}", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(requests, completed);
        Assert.Equal(cap, maxObservedConcurrency);
    }

    [Fact]
    public async Task MaxConcurrentPerPriority_UncappedPriority_IsUnaffected()
    {
        // Cap only Low at 1. Normal requests should run freely (up to MaxConcurrency).
        int maxNormalConcurrency = 0;
        int currentNormal = 0;

        await using var f = await ServiceFactory.CreateAsync(
            async (ctx, ct) =>
            {
                if (ctx.Priority == RequestPriority.Normal)
                {
                    int c = Interlocked.Increment(ref currentNormal);
                    InterlockedMax(ref maxNormalConcurrency, c);
                    await Task.Delay(20, ct);
                    Interlocked.Decrement(ref currentNormal);
                }
                return new RequestResult(ctx.RequestId, true, null);
            },
            concurrency: 4,
            configure: opts => opts.MaxConcurrentPerPriority = [1, 0, 0]);  // [Low=1, Normal=uncapped, High=uncapped]

        const int normalRequests = 8;
        int completed = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task OnDone(RequestResult _)
        {
            if (Interlocked.Increment(ref completed) == normalRequests) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        for (int i = 0; i < normalRequests; i++)
            await f.Service.EnqueueAsync(new RequestContext<string>($"n-{i}", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(normalRequests, completed);
        // Normal is uncapped; with 4 workers and 8 requests that overlap, concurrency > 1.
        Assert.True(maxNormalConcurrency > 1, $"Expected Normal to run concurrently but max was {maxNormalConcurrency}");
    }

    [Fact]
    public async Task MaxConcurrentPerPriority_AllPrioritiesCapped_AllWorkCompletes()
    {
        // Smoke test: capping all tiers should not deadlock or lose work.
        const int perPriority = 5;
        const int total = perPriority * 3;
        int completed = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            async (ctx, ct) => { await Task.Yield(); return new RequestResult(ctx.RequestId, true, null); },
            concurrency: 4, capacity: 50,
            configure: opts => opts.MaxConcurrentPerPriority = [2, 2, 2]);

        Task OnDone(RequestResult _)
        {
            if (Interlocked.Increment(ref completed) == total) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        for (int i = 0; i < perPriority; i++)
        {
            await f.Service.EnqueueAsync(new RequestContext<string>($"h-{i}", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);
            await f.Service.EnqueueAsync(new RequestContext<string>($"n-{i}", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);
            await f.Service.EnqueueAsync(new RequestContext<string>($"l-{i}", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);
        }

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(total, completed);
    }

    [Fact]
    public async Task MaxConcurrentPerPriority_NullDefault_BehaviourUnchanged()
    {
        // When MaxConcurrentPerPriority is null (default), all work completes normally.
        const int total = 9;
        int completed = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)),
            concurrency: 3);
        // MaxConcurrentPerPriority intentionally not set.

        Task OnDone(RequestResult _)
        {
            if (Interlocked.Increment(ref completed) == total) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        for (int i = 0; i < 3; i++)
        {
            await f.Service.EnqueueAsync(new RequestContext<string>($"h-{i}", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);
            await f.Service.EnqueueAsync(new RequestContext<string>($"n-{i}", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);
            await f.Service.EnqueueAsync(new RequestContext<string>($"l-{i}", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);
        }

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(total, completed);
    }

    private static async Task WaitForQueueDepthAsync(RequestPoolService service, Func<RequestPoolStats, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(service.GetSnapshot()))
                return;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(predicate(service.GetSnapshot()), "Queue depth predicate was not satisfied before the timeout.");
    }

    // Helper: thread-safe max update (Interlocked.Exchange-based).
    private static void InterlockedMax(ref int location, int candidate)
    {
        int current;
        do { current = Volatile.Read(ref location); }
        while (candidate > current &&
               Interlocked.CompareExchange(ref location, candidate, current) != current);
    }
}
