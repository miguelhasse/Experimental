using Polly;
using Polly.Retry;
using System.Threading.Channels;

namespace RequestProcessor.Tests;

public class RequestPoolServiceTests
{
    // ── Argument validation ───────────────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_NullContext_ThrowsArgumentNullException()
    {
        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
            Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => f.Service.EnqueueAsync(null!, _ => Task.CompletedTask, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task EnqueueAsync_NullCallback_ThrowsArgumentNullException()
    {
        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
            Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        var ctx = new RequestContext<string>("r1", "p");
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => f.Service.EnqueueAsync(ctx, null!, TestContext.Current.CancellationToken).AsTask());
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_SuccessfulDispatch_InvokesCallbackWithSuccessResult()
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            await Task.Yield();
            return new RequestResult(ctx.RequestId, Success: true, Output: "processed");
        });

        var context = new RequestContext<string>("req-42", "some-payload");
        await f.Service.EnqueueAsync(context, r => { tcs.TrySetResult(r); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("req-42", result.RequestId);
        Assert.Equal("processed", result.Output);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task EnqueueAsync_WithResiliencePipeline_RetriesFlakyDispatcher()
    {
        var attempts = 0;
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt < 3)
                    throw new InvalidOperationException("transient failure");

                return Task.FromResult(new RequestResult(ctx.RequestId, Success: true, Output: "recovered"));
            },
            configure: o =>
            {
                o.ResiliencePipelineFactory = _ => new ResiliencePipelineBuilder()
                    .AddRetry(new RetryStrategyOptions
                    {
                        ShouldHandle = new PredicateBuilder().Handle<InvalidOperationException>(),
                        MaxRetryAttempts = 2,
                        Delay = TimeSpan.Zero,
                    })
                    .Build();
            });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("polly-retry", "payload"),
            r => { tcs.TrySetResult(r); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("recovered", result.Output);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task EnqueueAsync_WithoutResiliencePipeline_DoesNotRetryDispatcherException()
    {
        var attempts = 0;
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("no pipeline");
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("no-polly", "payload"),
            r => { tcs.TrySetResult(r); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task EnqueueAsync_RequestIdPassedThrough_ToDispatcherAndCallback()
    {
        var dispatchedId = "";
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
        {
            Volatile.Write(ref dispatchedId, ctx.RequestId);
            return Task.FromResult(new RequestResult(ctx.RequestId, true, null));
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("my-request", "payload"),
            r => { tcs.TrySetResult(r.RequestId); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var callbackId = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("my-request", dispatchedId);
        Assert.Equal("my-request", callbackId);
    }

    [Fact]
    public async Task EnqueueAsync_StateOverload_InvokesCallbackWithState()
    {
        var state = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            await Task.Yield();
            return new RequestResult(ctx.RequestId, Success: true, Output: "state-output");
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("state-callback", "payload"),
            state,
            static (s, r) =>
            {
                s.TrySetResult(r.Output);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var output = await state.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("state-output", output);
    }

    [Fact]
    public async Task EnqueueAsync_StateOverload_PassesStateByReference()
    {
        var allowDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new MutableCallbackState();

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            await allowDispatch.Task.WaitAsync(ct);
            return new RequestResult(ctx.RequestId, Success: true, Output: null);
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("mutable-state", "payload"),
            state,
            static (s, r) =>
            {
                s.SeenValue.TrySetResult(s.Value);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        state.Value = 42;
        allowDispatch.SetResult();

        var seenValue = await state.SeenValue.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(42, seenValue);
    }

    [Fact]
    public async Task EnqueueAsync_OldOverloadStillInvokesCallback()
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            await Task.Yield();
            return new RequestResult(ctx.RequestId, Success: true, Output: "old-overload");
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("old-overload", "payload"),
            r =>
            {
                tcs.TrySetResult(r);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal("old-overload", result.Output);
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_DispatcherThrows_CallbackReceivesFailureResult()
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
            Task.FromException<RequestResult>(new InvalidOperationException("boom")));

        await f.Service.EnqueueAsync(
            new RequestContext<string>("r1", "p"),
            r => { tcs.TrySetResult(r); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("boom", result.Error.Message);
    }

    [Fact]
    public async Task EnqueueAsync_RetrySucceedsOnSecondAttempt_CallbackReceivesSuccess()
    {
        var completed = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadLettered = 0;
        var attempts = 0;
        var callbackCount = 0;

        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
                throw new InvalidOperationException("transient");

            return Task.FromResult(new RequestResult(ctx.RequestId, Success: true, Output: "ok"));
        }, configure: options =>
        {
            options.MaxDispatchAttempts = 2;
            options.OnDeadLetter = (_, _) => Interlocked.Increment(ref deadLettered);
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("retry-then-success", "p"),
            r =>
            {
                Interlocked.Increment(ref callbackCount);
                completed.TrySetResult(r);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Output);
        Assert.Equal(2, attempts);
        Assert.Equal(0, deadLettered);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task EnqueueAsync_RetrySucceedsOnSecondAttempt_CallbackInvokedExactlyOnceWithSuccess()
    {
        var completed = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var callbackCount = 0;

        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            return Task.FromResult(attempt == 1
                ? new RequestResult(ctx.RequestId, Success: false, Output: null, Error: new InvalidOperationException("transient"))
                : new RequestResult(ctx.RequestId, Success: true, Output: "recovered"));
        }, configure: options => options.MaxDispatchAttempts = 2);

        await f.Service.EnqueueAsync(
            new RequestContext<string>("retry-callback-once", "p"),
            r =>
            {
                Interlocked.Increment(ref callbackCount);
                completed.TrySetResult(r);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("recovered", result.Output);
        Assert.Equal(2, attempts);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task EnqueueAsync_DroppedPromotedItem_RemovesPendingEntry()
    {
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            dispatchStarted.TrySetResult();
            await releaseWorker.Task.WaitAsync(ct);
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 1, configure: options =>
        {
            options.DrainTimeout = TimeSpan.FromMilliseconds(100);
            options.FullMode = BoundedChannelFullMode.DropOldest;
            options.PriorityAgingThreshold = TimeSpan.FromMilliseconds(1);
            options.PriorityAgingScanInterval = TimeSpan.FromMilliseconds(100);
            options.OnItemDropped = ctx =>
            {
                if (ctx.RequestId == "aged-drop")
                    dropped.TrySetResult(ctx.RequestId);
            };
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("blocker", "p", Priority: RequestPriority.High),
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(
            new RequestContext<string>("aged-drop", "p", Priority: RequestPriority.Low),
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        var monitor = (IRequestPoolMonitor)f.Service;
        var promoted = false;
        for (var i = 0; i < 200; i++)
        {
            if (monitor.GetSnapshot().QueueDepthNormal > 0)
            {
                promoted = true;
                break;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(promoted, "The low-priority request was not promoted before the timeout.");

        await f.Service.EnqueueAsync(
            new RequestContext<string>("normal-flood", "p", Priority: RequestPriority.Normal),
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(monitor.TryCancelRequest("aged-drop"));

        releaseWorker.TrySetResult();
    }

    [Fact]
    public async Task EnqueueAsync_RetryExhausted_DeadLettersAndCallbackReceivesFailure()
    {
        var completed = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadLettered = new TaskCompletionSource<(RequestContext Context, Exception Exception)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;

        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<RequestResult>(new InvalidOperationException("still-broken"));
        }, configure: options =>
        {
            options.MaxDispatchAttempts = 2;
            options.OnDeadLetter = (ctx, ex) => deadLettered.TrySetResult((ctx, ex));
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("retry-exhausted", "p"),
            r => { completed.TrySetResult(r); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var deadLetter = await deadLettered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("still-broken", result.Error?.Message);
        Assert.Equal("retry-exhausted", deadLetter.Context.RequestId);
        Assert.Equal("still-broken", deadLetter.Exception.Message);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldRetryFalse_DeadLettersWithoutRetry()
    {
        var completed = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadLettered = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var shouldRetryCalls = 0;

        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new RequestResult(
                ctx.RequestId,
                Success: false,
                Output: null,
                Error: new InvalidOperationException("do-not-retry")));
        }, configure: options =>
        {
            options.MaxDispatchAttempts = 3;
            options.ShouldRetry = (ex, attempt) =>
            {
                Interlocked.Increment(ref shouldRetryCalls);
                Assert.Equal("do-not-retry", ex.Message);
                Assert.Equal(1, attempt);
                return false;
            };
            options.OnDeadLetter = (_, ex) => deadLettered.TrySetResult(ex);
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("retry-denied", "p"),
            r => { completed.TrySetResult(r); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var deadLetter = await deadLettered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("do-not-retry", result.Error?.Message);
        Assert.Equal("do-not-retry", deadLetter.Message);
        Assert.Equal(1, attempts);
        Assert.Equal(1, shouldRetryCalls);
    }

    [Fact]
    public async Task EnqueueAsync_DefaultMaxDispatchAttempts_DoesNotRetry()
    {
        var completed = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;

        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<RequestResult>(new InvalidOperationException("boom"));
        });

        await f.Service.EnqueueAsync(
            new RequestContext<string>("no-retry-default", "p"),
            r => { completed.TrySetResult(r); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("boom", result.Error?.Message);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task EnqueueAsync_CallbackThrows_WorkerContinuesProcessingNextItem()
    {
        // First item's callback throws; second item should still complete.
        var secondDone = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)),
            concurrency: 1);   // single worker to ensure ordering

        await f.Service.EnqueueAsync(new RequestContext<string>("r1", "p"), _ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromException(new Exception("bad callback"));
        }, TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(new RequestContext<string>("r2", "p"), r =>
        {
            Interlocked.Increment(ref callCount);
            secondDone.TrySetResult(r);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        var result = await secondDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(2, callCount);   // both callbacks were invoked
    }

    [Fact]
    public async Task EnqueueAsync_DispatcherThrowsAndCallbackThrows_WorkerContinues()
    {
        var thirdDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;

        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
        {
            if (ctx.RequestId == "bad")
                throw new Exception("dispatcher fail");
            return Task.FromResult(new RequestResult(ctx.RequestId, true, null));
        }, concurrency: 1);

        await f.Service.EnqueueAsync(new RequestContext<string>("bad", "p"), _ => Task.FromException(new Exception("cb fail")), TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("ok1", "p"), _ => { Interlocked.Increment(ref completed); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("ok2", "p"), _ =>
        {
            Interlocked.Increment(ref completed);
            thirdDone.TrySetResult();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        await thirdDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, completed);
    }

    // ── Concurrency and throughput ────────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_ManyJobs_AllCallbacksFire()
    {
        const int jobCount = 50;
        int received = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            async (ctx, ct) => { await Task.Delay(5, ct); return new RequestResult(ctx.RequestId, true, "ok"); },
            concurrency: 4,
            capacity: 100);

        for (int i = 0; i < jobCount; i++)
        {
            await f.Service.EnqueueAsync(new RequestContext<string>($"job-{i}", "p"), _ =>
            {
                if (Interlocked.Increment(ref received) == jobCount)
                    allDone.TrySetResult();
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(jobCount, received);
    }

    [Fact]
    public async Task EnqueueAsync_MaxConcurrency_IsRespected()
    {
        const int maxConcurrency = 2;
        const int totalJobs = 8;

        int activeConcurrent = 0;
        int peakConcurrent = 0;
        int received = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            // Track concurrent executions
            int current = Interlocked.Increment(ref activeConcurrent);
            int prev;
            do { prev = Volatile.Read(ref peakConcurrent); }
            while (current > prev &&
                   Interlocked.CompareExchange(ref peakConcurrent, current, prev) != prev);

            await Task.Delay(30, ct);
            Interlocked.Decrement(ref activeConcurrent);
            return new RequestResult(ctx.RequestId, true, "ok");
        }, concurrency: maxConcurrency, capacity: 50);

        for (int i = 0; i < totalJobs; i++)
        {
            await f.Service.EnqueueAsync(new RequestContext<string>($"job-{i}", "p"), _ =>
            {
                if (Interlocked.Increment(ref received) == totalJobs)
                    allDone.TrySetResult();
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(totalJobs, received);
        Assert.True(peakConcurrent <= maxConcurrency,
            $"Expected ≤ {maxConcurrency} concurrent dispatches but observed {peakConcurrent}");
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_WithPendingItems_DrainsThenCompletes()
    {
        // Enqueue items, stop, verify all were processed before StopAsync returns.
        const int jobCount = 10;
        var results = new ConcurrentBag<RequestResult>();

        await using var f = await ServiceFactory.CreateAsync(
            async (ctx, ct) => { await Task.Delay(5, ct); return new RequestResult(ctx.RequestId, true, null); },
            concurrency: 2);

        for (int i = 0; i < jobCount; i++)
            await f.Service.EnqueueAsync(new RequestContext<string>($"j{i}", "p"), r => { results.Add(r); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        // StopAsync completes the channel writer and waits for workers to drain.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await f.Service.StopAsync(cts.Token);

        Assert.Equal(jobCount, results.Count);
    }

    [Fact]
    public async Task EnqueueAsync_AfterStopAsync_ThrowsChannelClosedException()
    {
        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        await f.Service.StopAsync(CancellationToken.None);

        // Channel writer is completed — writing should throw.
        await Assert.ThrowsAnyAsync<Exception>(
            () => f.Service.EnqueueAsync(new RequestContext<string>("r1", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task StopAsync_WithDrainTimeout_ReturnsWithinDeadline()
    {
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            async (ctx, ct) =>
            {
                dispatchStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new RequestResult(ctx.RequestId, true, null);
            },
            concurrency: 1,
            configure: o => o.DrainTimeout = TimeSpan.FromMilliseconds(100));

        await f.Service.EnqueueAsync(
            new RequestContext<string>("hanging", "p"),
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await f.Service.StopAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"StopAsync took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task StopAsync_WithInfiniteDrainTimeout_StopsWhenStoppingTokenIsCancelled()
    {
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatcher = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var f = await ServiceFactory.CreateAsync(
            async (ctx, ct) =>
            {
                dispatchStarted.TrySetResult();
                var completed = await Task.WhenAny(
                    Task.Delay(Timeout.InfiniteTimeSpan, ct),
                    releaseDispatcher.Task);
                if (completed != releaseDispatcher.Task)
                    ct.ThrowIfCancellationRequested();
                return new RequestResult(ctx.RequestId, true, null);
            },
            concurrency: 1,
            configure: o => o.DrainTimeout = Timeout.InfiniteTimeSpan);

        try
        {
            await f.Service.EnqueueAsync(
                new RequestContext<string>("hanging-infinite-drain", "p"),
                _ => Task.CompletedTask,
                TestContext.Current.CancellationToken);
            await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            using var stopCts = new CancellationTokenSource();
            var stopTask = f.Service.StopAsync(stopCts.Token);
            stopCts.Cancel();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
            stopwatch.Stop();

            Assert.Same(stopTask, completed);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"StopAsync took {stopwatch.Elapsed}.");
        }
        finally
        {
            releaseDispatcher.TrySetResult();
            await f.DisposeAsync();
        }
    }

    // ── Cancellation (TryCancelRequest) ──────────────────────────────────────

    [Fact]
    public async Task TryCancelRequest_PendingRequest_ReturnsTrueAndCallbackReceivesCancelled()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            arrived.TrySetResult();
            await release.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 10);

        // Slot the worker with a blocking request so the next item stays queued.
        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Enqueue the target request — it sits in the channel while the worker is busy.
        await f.Service.EnqueueAsync(new RequestContext<string>("cancel-me", "p"), r => { tcs.TrySetResult(r); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        // Cancel before the worker gets to it.
        bool cancelled = (f.Service as IRequestPoolMonitor)!.TryCancelRequest("cancel-me");
        Assert.True(cancelled);

        release.TrySetResult(); // unblock the worker

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error);
    }

    [Fact]
    public async Task TryCancelRequest_UnknownRequest_ReturnsFalse()
    {
        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        bool cancelled = (f.Service as IRequestPoolMonitor)!.TryCancelRequest("no-such-id");
        Assert.False(cancelled);
    }

    [Fact]
    public async Task TryCancelRequest_AlreadyDispatched_ReturnsFalse()
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            await Task.Yield(); // ensure it's truly dispatched before cancel attempt
            return new RequestResult(ctx.RequestId, true, "done");
        });

        await f.Service.EnqueueAsync(new RequestContext<string>("r1", "p"), r => { tcs.TrySetResult(r); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Already completed — cancel should be a no-op.
        bool cancelled = (f.Service as IRequestPoolMonitor)!.TryCancelRequest("r1");
        Assert.False(cancelled);
    }

    // ── Priority ordering ─────────────────────────────────────────────────────

    [Fact]
    public async Task Priority_HighBefore_Low_WhenEnqueuedTogether()
    {
        // Use a single worker + capacity 1 to ensure all items queue before the
        // worker starts draining.  Fill the worker slot with a blocking request,
        // then enqueue Low and High while it is occupied, then unblock and
        // verify High is processed before Low.
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
        }, concurrency: 1, capacity: 50);

        // Occupy the single worker.
        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Enqueue Low first, then High — pool must prefer High regardless of arrival order.
        await f.Service.EnqueueAsync(new RequestContext<string>("low", "p", Priority: RequestPriority.Low),
            _ => { order.Enqueue("low"); if (Interlocked.Increment(ref received) == 2) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(new RequestContext<string>("high", "p", Priority: RequestPriority.High),
            _ => { order.Enqueue("high"); if (Interlocked.Increment(ref received) == 2) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        release.TrySetResult();

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["high", "low"], order.ToArray());
    }

    [Fact]
    public async Task Priority_NormalBefore_Low_WhenEnqueuedTogether()
    {
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
        }, concurrency: 1, capacity: 50);

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(
            new RequestContext<string>("low", "p", Priority: RequestPriority.Low),
            _ => { order.Enqueue("low"); if (Interlocked.Increment(ref received) == 2) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(
            new RequestContext<string>("normal", "p", Priority: RequestPriority.Normal),
            _ => { order.Enqueue("normal"); if (Interlocked.Increment(ref received) == 2) allDone.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
        release.TrySetResult();

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["normal", "low"], order.ToArray());
    }

    // ── Stats snapshot (IRequestPoolMonitor) ─────────────────────────────────

    [Fact]
    public async Task GetSnapshot_AfterSuccessfulDispatch_IncrementsTotalCompleted()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        await f.Service.EnqueueAsync(
            new RequestContext<string>("r1", "p"),
            _ => { done.TrySetResult(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stats = (f.Service as IRequestPoolMonitor)!.GetSnapshot();
        Assert.Equal(1, stats.TotalEnqueued);
        Assert.Equal(1, stats.TotalCompleted);
        Assert.Equal(0, stats.TotalFailed);
        Assert.Equal(0, stats.TotalCancelled);
    }

    [Fact]
    public async Task GetSnapshot_AfterDispatcherThrows_IncrementsTotalFailed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromException<RequestResult>(new Exception("boom")));

        await f.Service.EnqueueAsync(new RequestContext<string>("r1", "p"), _ => { done.TrySetResult(); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stats = (f.Service as IRequestPoolMonitor)!.GetSnapshot();
        Assert.Equal(1, stats.TotalEnqueued);
        Assert.Equal(0, stats.TotalCompleted);
        Assert.Equal(1, stats.TotalFailed);
    }

    [Fact]
    public async Task GetSnapshot_ReflectsOptions()
    {
        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)),
            concurrency: 3, capacity: 42);

        var stats = (f.Service as IRequestPoolMonitor)!.GetSnapshot();
        Assert.Equal(3, stats.MaxConcurrency);
        Assert.Equal(42, stats.BoundedCapacity);
    }

    [Fact]
    public async Task EnqueueAsync_CancellationToken_CancelsPendingEnqueue()
    {
        var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            workerBlocked.TrySetResult();
            await releaseWorker.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 1);

        // Fill the worker slot
        await f.Service.EnqueueAsync(new RequestContext<string>("r1", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        // Fill the channel (capacity=1)
        await f.Service.EnqueueAsync(new RequestContext<string>("r2", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Third enqueue should block (channel full) — cancel it
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => f.Service.EnqueueAsync(new RequestContext<string>("r3", "p"), _ => Task.CompletedTask, cts.Token).AsTask());

        releaseWorker.TrySetResult();
    }

    // ── Stats snapshot – additional coverage ─────────────────────────────────

    [Fact]
    public async Task GetSnapshot_TotalCancelled_IncrementedAfterTryCancelRequest()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            arrived.TrySetResult();
            await release.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1);

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(new RequestContext<string>("cancel-me", "p"), r => { tcs.TrySetResult(r); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        (f.Service as IRequestPoolMonitor)!.TryCancelRequest("cancel-me");

        release.TrySetResult();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stats = (f.Service as IRequestPoolMonitor)!.GetSnapshot();
        Assert.Equal(1, stats.TotalCancelled);
        Assert.Equal(0, stats.TotalFailed);          // cancelled ≠ failed
        Assert.Equal(2, stats.TotalEnqueued);         // blocker + cancel-me
    }

    [Fact]
    public async Task GetSnapshot_QueueDepthByPriority_ReflectsChannelContents()
    {
        var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            workerBlocked.TrySetResult();
            await release.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 50);

        // Block the single worker so enqueued items stay in the channels.
        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(new RequestContext<string>("h1", "p", Priority: RequestPriority.High), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("h2", "p", Priority: RequestPriority.High), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("n1", "p", Priority: RequestPriority.Normal), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("l1", "p", Priority: RequestPriority.Low), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("l2", "p", Priority: RequestPriority.Low), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("l3", "p", Priority: RequestPriority.Low), _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        var stats = (f.Service as IRequestPoolMonitor)!.GetSnapshot();
        Assert.Equal(2, stats.QueueDepthHigh);
        Assert.Equal(1, stats.QueueDepthNormal);
        Assert.Equal(3, stats.QueueDepthLow);
        Assert.Equal(6, stats.TotalQueueDepth);

        release.TrySetResult();
    }

    [Fact]
    public async Task GetSnapshot_ActiveWorkers_IsNonZero_DuringDispatch()
    {
        var dispatching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            dispatching.TrySetResult();
            await release.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1);

        await f.Service.EnqueueAsync(new RequestContext<string>("r1", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await dispatching.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stats = (f.Service as IRequestPoolMonitor)!.GetSnapshot();
        Assert.True(stats.ActiveWorkers > 0,
            $"Expected ActiveWorkers > 0 while dispatch is in progress, but was {stats.ActiveWorkers}");

        release.TrySetResult();
    }

    [Fact]
    public async Task GetSnapshot_TotalEnqueued_IncludesAllPriorities()
    {
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;
        const int total = 3;

        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        Task OnDone(RequestResult _)
        {
            if (Interlocked.Increment(ref received) == total) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        await f.Service.EnqueueAsync(new RequestContext<string>("h", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("n", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("l", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stats = (f.Service as IRequestPoolMonitor)!.GetSnapshot();
        Assert.Equal(total, stats.TotalEnqueued);
        Assert.Equal(total, stats.TotalCompleted);
    }

    // ── Cancellation (CancelAllRequests) ──────────────────────────────────────

    [Fact]
    public async Task CancelAllRequests_NoPriorityFilter_CancelsEveryQueuedRequest()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = new ConcurrentBag<RequestResult>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int queued = 3;
        int received = 0;

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            arrived.TrySetResult();
            await release.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 50);

        // Block the single worker.
        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task OnDone(RequestResult r)
        {
            results.Add(r);
            if (Interlocked.Increment(ref received) == queued) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        await f.Service.EnqueueAsync(new RequestContext<string>("h1", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("n1", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("l1", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);

        var monitor = (f.Service as IRequestPoolMonitor)!;
        int count = monitor.CancelAllRequests();

        Assert.Equal(queued, count);

        release.TrySetResult();

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.False(r.Success));
        Assert.All(results, r => Assert.IsAssignableFrom<OperationCanceledException>(r.Error));

        var stats = monitor.GetSnapshot();
        Assert.Equal(queued, stats.TotalCancelled);
    }

    [Fact]
    public async Task CancelAllRequests_WithPriorityFilter_CancelsOnlyMatchingPriority()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = new ConcurrentDictionary<string, RequestResult>();
        var nonLowDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int nonLowReceived = 0;
        const int nonLowCount = 2; // High + Normal

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            arrived.TrySetResult();
            await release.Task;
            return new RequestResult(ctx.RequestId, true, "ok");
        }, concurrency: 1, capacity: 50);

        // Block the single worker.
        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task OnDone(RequestResult r)
        {
            results[r.RequestId] = r;
            if (Interlocked.Increment(ref nonLowReceived) == nonLowCount)
                nonLowDone.TrySetResult();
            return Task.CompletedTask;
        }

        await f.Service.EnqueueAsync(new RequestContext<string>("h1", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("n1", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);
        await f.Service.EnqueueAsync(new RequestContext<string>("l1", "p", Priority: RequestPriority.Low), _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        var monitor = (f.Service as IRequestPoolMonitor)!;
        int count = monitor.CancelAllRequests(RequestPriority.Low);

        Assert.Equal(1, count);

        release.TrySetResult();

        // High and Normal should complete successfully.
        await nonLowDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(results["h1"].Success);
        Assert.True(results["n1"].Success);
    }

    [Fact]
    public async Task CancelAllRequests_EmptyQueue_ReturnsZero()
    {
        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        var monitor = (f.Service as IRequestPoolMonitor)!;
        int count = monitor.CancelAllRequests();

        Assert.Equal(0, count);
    }

    // ── Race conditions & concurrency ─────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_DuplicateRequestId_ThrowsArgumentException()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            arrived.TrySetResult();
            await release.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 10);

        // Block the worker so the second enqueue can't be processed before we try the duplicate.
        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Enqueue "target" — it sits in the channel while the worker is busy.
        await f.Service.EnqueueAsync(new RequestContext<string>("target", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        // A second enqueue with the same RequestId must be rejected immediately.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => f.Service.EnqueueAsync(new RequestContext<string>("target", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("target", ex.Message);

        release.TrySetResult();
    }

    [Fact]
    public async Task TryCancelRequest_ConcurrentCancels_ExactlyOneSucceeds()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            arrived.TrySetResult();
            await release.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 20);

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await f.Service.EnqueueAsync(new RequestContext<string>("cancel-me", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        var monitor = (f.Service as IRequestPoolMonitor)!;

        // 20 concurrent TryCancelRequest calls on the same ID — exactly one must win.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() => monitor.TryCancelRequest("cancel-me"))));

        Assert.Equal(1, results.Count(r => r));

        release.TrySetResult();
    }

    [Fact]
    public async Task CancelAllRequests_ConcurrentWithEnqueue_NoExceptionsThrown()
    {
        await using var f = await ServiceFactory.CreateAsync(
            (ctx, ct) => Task.FromResult(new RequestResult(ctx.RequestId, true, null)),
            concurrency: 4, capacity: 100);

        var monitor = (f.Service as IRequestPoolMonitor)!;
        using var stopCts = new CancellationTokenSource();
        var exceptions = new ConcurrentBag<Exception>();

#pragma warning disable xUnit1051 // These background stress tasks intentionally use their own CancellationToken
        var enqueueTask = Task.Run(async () =>
        {
            for (int i = 0; !stopCts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    await f.Service.EnqueueAsync(
                        new RequestContext<string>($"stress-{i}", "p"), _ => Task.CompletedTask, stopCts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { exceptions.Add(ex); break; }
            }
        });

        var cancelTask = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                monitor.CancelAllRequests();
                await Task.Yield();
            }
        });
#pragma warning restore xUnit1051

        await cancelTask;
        stopCts.Cancel();
        await enqueueTask;

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task CancelAllRequests_AllThreePriorityChannels_CancelsAll()
    {
        var workerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = new ConcurrentBag<RequestResult>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int received = 0;
        const int perPriority = 3;
        const int total = perPriority * 3; // 9 requests across High, Normal, Low

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            workerBlocked.TrySetResult();
            await releaseWorker.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 20);

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await workerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task OnDone(RequestResult r)
        {
            results.Add(r);
            if (Interlocked.Increment(ref received) == total) allDone.TrySetResult();
            return Task.CompletedTask;
        }

        for (int i = 0; i < perPriority; i++)
        {
            await f.Service.EnqueueAsync(new RequestContext<string>($"h{i}", "p", Priority: RequestPriority.High), OnDone, TestContext.Current.CancellationToken);
            await f.Service.EnqueueAsync(new RequestContext<string>($"n{i}", "p", Priority: RequestPriority.Normal), OnDone, TestContext.Current.CancellationToken);
            await f.Service.EnqueueAsync(new RequestContext<string>($"l{i}", "p", Priority: RequestPriority.Low), OnDone, TestContext.Current.CancellationToken);
        }

        var monitor = (f.Service as IRequestPoolMonitor)!;
        int cancelled = monitor.CancelAllRequests();

        Assert.Equal(total, cancelled);

        releaseWorker.TrySetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.False(r.Success));
        Assert.All(results, r => Assert.IsAssignableFrom<OperationCanceledException>(r.Error));

        var stats = monitor.GetSnapshot();
        Assert.Equal(total, stats.TotalCancelled);
    }

    [Fact]
    public async Task CancelAllRequests_NoPriorityFilter_TotalCancelledEqualsCount()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCancelledDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancelledReceived = 0;
        const int queued = 4;

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            arrived.TrySetResult();
            await release.Task;
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 50);

        await f.Service.EnqueueAsync(new RequestContext<string>("blocker", "p"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task OnDone(RequestResult _)
        {
            if (Interlocked.Increment(ref cancelledReceived) == queued)
                allCancelledDone.TrySetResult();
            return Task.CompletedTask;
        }

        for (int i = 0; i < queued; i++)
            await f.Service.EnqueueAsync(new RequestContext<string>($"r{i}", "p"), OnDone, TestContext.Current.CancellationToken);

        var monitor = (f.Service as IRequestPoolMonitor)!;
        int cancelled = monitor.CancelAllRequests();
        Assert.Equal(queued, cancelled);

        release.TrySetResult();
        await allCancelledDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stats = monitor.GetSnapshot();
        Assert.Equal(queued, stats.TotalCancelled);
        Assert.Equal(0, stats.TotalFailed);
    }

    [Fact]
    public async Task PartitionFairnessEnabled_RoundRobinsPartitionsWithinPriority()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = 9;

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            await release.Task;
            order.Enqueue(ctx.PartitionKey ?? "__default");
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 50, configure: o => o.PartitionFairnessEnabled = true);

        Task OnDone(RequestResult result)
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                completed.TrySetResult();
            return Task.CompletedTask;
        }

        for (var i = 0; i < 3; i++)
            await f.Service.EnqueueAsync(new RequestContext<string>($"a-{i}", "p") { PartitionKey = "A" }, OnDone, TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
            await f.Service.EnqueueAsync(new RequestContext<string>($"b-{i}", "p") { PartitionKey = "B" }, OnDone, TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
            await f.Service.EnqueueAsync(new RequestContext<string>($"c-{i}", "p") { PartitionKey = "C" }, OnDone, TestContext.Current.CancellationToken);

        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var firstNine = order.ToArray();
        Assert.Equal(9, firstNine.Length);
        Assert.Equal(3, firstNine.Count(p => p == "A"));
        Assert.Equal(3, firstNine.Count(p => p == "B"));
        Assert.Equal(3, firstNine.Count(p => p == "C"));
    }

    [Fact]
    public async Task PartitionFairnessEnabled_SinglePartitionPreservesFifoOrder()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = 10;

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            await release.Task;
            order.Enqueue(((RequestContext<int>)ctx).Data);
            return new RequestResult(ctx.RequestId, true, null);
        }, concurrency: 1, capacity: 20, configure: o => o.PartitionFairnessEnabled = true);

        Task OnDone(RequestResult result)
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                completed.TrySetResult();
            return Task.CompletedTask;
        }

        for (var i = 0; i < 10; i++)
            await f.Service.EnqueueAsync(new RequestContext<int>($"single-{i}", i) { PartitionKey = "only" }, OnDone, TestContext.Current.CancellationToken);

        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, 10), order.ToArray());
    }

    private sealed class MutableCallbackState
    {
        public int Value;
        public TaskCompletionSource<int> SeenValue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class RequestPoolDispatchTimeoutTests
{
    [Fact]
    public async Task DispatchAsync_CancelsDispatch_WhenPerRequestTimeoutExceeds()
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            async (ctx, cancellationToken) =>
            {
                await Task.Delay(2000, cancellationToken);
                return new RequestResult(ctx.RequestId, Success: true, Output: null);
            },
            configure: o => o.DispatchTimeoutMs = Timeout.Infinite);

        await f.Service.EnqueueAsync(
            new RequestContext<string>("per-request-timeout", "data") { Timeout = TimeSpan.FromMilliseconds(200) },
            r => { tcs.TrySetResult(r); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error);
    }

    [Fact]
    public async Task DispatchAsync_CancelsDispatch_WhenDispatchTimeoutExceeds()
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(
            async (ctx, cancellationToken) =>
            {
                await Task.Delay(2000, cancellationToken);
                return new RequestResult(ctx.RequestId, Success: true, Output: null);
            },
            configure: o => o.DispatchTimeoutMs = 200);

        await f.Service.EnqueueAsync(
            new RequestContext<string>("timeout-req", "data"),
            r => { tcs.TrySetResult(r); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error);
    }
}
