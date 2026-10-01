namespace Orleans.Tests;

[Collection("ClusterCollection")]
public sealed class DurableJobGrainTests(ClusterFixture fixture)
{
    private IDurableJobGrain Grain(string id) =>
        fixture.Cluster.GrainFactory.GetGrain<IDurableJobGrain>(id);

    private static string NewId() => $"durable-{Guid.NewGuid():N}";

    [Fact]
    public async Task GetJobAsync_WhenUnknownJob_ReturnsNull()
    {
        Assert.Null(await Grain(NewId()).GetJobAsync("missing"));
    }

    [Fact]
    public async Task SubmitAsync_OnSuccess_PersistsTheResult()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("done"));

        await grain.SubmitAsync("j1", new JobRequest("payload"));

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal("done", job.Output);
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    [Fact]
    public async Task SubmitAsync_OnError_PersistsFailedStatus()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseError(new InvalidOperationException("boom"));

        await grain.SubmitAsync("j1", new JobRequest("payload"));

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("boom", job.Error);
    }

    [Fact]
    public async Task SubmitAsync_OnCancel_PersistsCancelledStatus()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseCancel();

        await grain.SubmitAsync("j1", new JobRequest("payload"));

        Assert.Equal(JobStatus.Cancelled, (await WaitForTerminalAsync(grain, "j1")).Status);
    }

    [Fact]
    public async Task SubmitAsync_WhenAlreadyProcessing_IsNoOp()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold();
        await grain.SubmitAsync("j1", new JobRequest("first"));

        // A second submit must not enqueue: the pool would complete it if it did.
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("should-not-run"));
        await grain.SubmitAsync("j1", new JobRequest("second"));

        Assert.Equal(JobStatus.Processing, (await grain.GetJobAsync("j1"))!.Status);
    }

    [Fact]
    public async Task GetJobsAsync_AfterDeactivation_RecoversDurableState()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("kept"));
        await grain.SubmitAsync("j1", new JobRequest("payload"));
        await WaitForTerminalAsync(grain, "j1");

        // Force a fresh activation so state must be replayed from the journal.
        await DeactivateAsync(grain);

        var jobs = await grain.GetJobsAsync();
        var job = Assert.Single(jobs);
        Assert.Equal("kept", job.Output);
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    // ── Recovery of unfinished jobs ───────────────────────────────────────────

    [Fact]
    public async Task RecoverUnfinishedJobsAsync_WhenOwnerLost_ResubmitsOnce()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.Hold();
        await grain.SubmitAsync("j1", new JobRequest("payload"));

        var runs = 0;
        ClusterFixture.Pool.UseResult(ctx =>
        {
            // Count only this grain's work: other tests leave held jobs whose own recovery also uses the shared pool.
            if (ctx.PartitionKey == id)
                Interlocked.Increment(ref runs);
            return new RequestResult(ctx.RequestId, Success: true, Output: "again");
        });
        ClusterFixture.Liveness.SimulateOwnerLoss();

        // The activation-time recovery timer may also race with these calls, so assert on the outcome:
        // however many callers try, the job is claimed and run exactly once.
        var results = await Task.WhenAll(grain.RecoverUnfinishedJobsAsync(), grain.RecoverUnfinishedJobsAsync());

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.True(results.Sum() <= 1, $"job claimed more than once by explicit calls: {string.Join(",", results)}");
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task RecoverUnfinishedJobsAsync_WhenOwnerAlive_DoesNothing()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold();
        await grain.SubmitAsync("j1", new JobRequest("payload"));

        // The pool of a live owner is still running this job; resubmitting would run it twice.
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("should-not-run"));

        Assert.Equal(0, await grain.RecoverUnfinishedJobsAsync());
        await Task.Delay(300, TestContext.Current.CancellationToken); // give the activation-time recovery a chance to misfire
        var job = (await grain.GetJobAsync("j1"))!;
        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.Equal(0, job.Attempts);
    }

    [Fact]
    public async Task RecoverUnfinishedJobsAsync_WhenOwnerKeepsDying_FailsJobAfterMaxAttempts()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold(); // never completes, so every owner is eventually lost
        await grain.SubmitAsync("j1", new JobRequest("payload"));

        // Assert on Attempts rather than on the return value: a late activation-time recovery
        // may claim the job before the explicit call does. Either way each loss is claimed once.
        for (var attempt = 1; attempt <= DurableJobGrain.MaxRecoveryAttempts; attempt++)
        {
            ClusterFixture.Liveness.SimulateOwnerLoss();
            await grain.RecoverUnfinishedJobsAsync();
            Assert.Equal(attempt, await WaitForAttemptsAsync(grain, "j1", attempt));
            Assert.Equal(JobStatus.Processing, (await grain.GetJobAsync("j1"))!.Status);
        }

        ClusterFixture.Liveness.SimulateOwnerLoss();
        await grain.RecoverUnfinishedJobsAsync();

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal(DurableJobGrain.MaxRecoveryAttempts, job.Attempts);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("Abandoned", job.Error);
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    [Fact]
    public async Task RecoverUnfinishedJobsAsync_IgnoresFinishedJobs()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("done"));
        await grain.SubmitAsync("j1", new JobRequest("payload"));
        await WaitForTerminalAsync(grain, "j1");

        ClusterFixture.Liveness.SimulateOwnerLoss();

        Assert.Equal(0, await grain.RecoverUnfinishedJobsAsync());
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    // ── Multiple, duplicate, out-of-order and late results ────────────────────
    // Pool data reaches the grain through the extension and may arrive repeatedly, out of order, and after the
    // run it belongs to was superseded. These tests play the part of such workers by calling the extension directly.

    // ── Several results per job (staged work) ─────────────────────────────────

    /// <summary>Plays a staged handler: <paramref name="stages"/> partial results (progress deltas), then the final result.</summary>
    private static RequestResult RunStaged(RequestContext ctx, int stages, bool redeliver)
    {
        for (var stage = 1; stage <= stages; stage++)
            ctx.OnProgress?.Invoke(stage * 100 / stages, $"Stage {stage}",
                new PartialResultDelta(stage, stages, $"result-{stage}", redeliver));

        return new RequestResult(ctx.RequestId, Success: true, Output: "final", TypedOutput: new TextJobOutput("final"));
    }

    [Fact]
    public async Task SubmitAsync_WithPartialResults_PushesEachResultThenTheFinalOne()
    {
        var grain = Grain(NewId());
        var staged = false;
        ClusterFixture.Pool.UseResult(ctx =>
        {
            staged = ctx is RequestContext<StagedJobRequest>;
            return RunStaged(ctx, stages: 3, redeliver: false);
        });

        await grain.SubmitAsync("j1", new JobRequest("payload", PartialResults: 3));

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.True(staged, "a job with PartialResults must be routed to the staged handler");
        Assert.Equal((JobStatus.Completed, "final"), (job.Status, job.Output));
        Assert.Equal(4, job.ResultsReceived);   // 3 partial results + the final one
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    [Fact]
    public async Task SubmitAsync_WhenPartialResultsAreDeliveredTwice_EachIsAppliedOnce()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseResult(ctx => RunStaged(ctx, stages: 3, redeliver: true));

        await grain.SubmitAsync("j1", new JobRequest("payload", PartialResults: 3));

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(4, job.ResultsReceived);   // the redelivered calls reuse a sequence number and are dropped
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    [Fact]
    public async Task SubmitAsync_WithoutPartialResults_UsesThePlainHandlerAndOneResult()
    {
        var grain = Grain(NewId());
        var plain = false;
        ClusterFixture.Pool.UseResult(ctx =>
        {
            plain = ctx is RequestContext<JobRequest>;
            return new RequestResult(ctx.RequestId, Success: true, Output: "ok", TypedOutput: new TextJobOutput("ok"));
        });

        await grain.SubmitAsync("j1", new JobRequest("payload"));

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.True(plain);
        Assert.Equal(1, job.ResultsReceived);
    }

    // ── Robustness fixes found in review ──────────────────────────────────────

    [Fact]
    public async Task SubmitAsync_WhenThePoolRefusesTheWork_FailsTheRunAndRethrows()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseResult(_ => throw new InvalidOperationException("pool stopped"));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => grain.SubmitAsync("j1", new JobRequest("p", IdempotencyKey: "k1")));

        var job = (await grain.GetJobAsync("j1"))!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("Could not start", job.Error);
        Assert.Equal(1, await grain.GetFinishedCountAsync());

        // The failed start did not burn the client's idempotency key: a retry with the same key is accepted.
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("ok"));
        await grain.SubmitAsync("j1", new JobRequest("p", IdempotencyKey: "k1"));
        Assert.Equal(JobStatus.Completed, (await WaitForTerminalAsync(grain, "j1")).Status);
    }

    [Fact]
    public async Task Recovery_WhenOneJobCannotBeRestarted_FailsItAndStillRestartsTheOthers()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold();
        foreach (var id in new[] { "j1", "j2", "j3" })
            await grain.SubmitAsync(id, new JobRequest("p"));

        ClusterFixture.Pool.UseResult(ctx => ctx.RequestId.Contains("/j2#")
            ? throw new InvalidOperationException("pool refused j2")
            : new RequestResult(ctx.RequestId, Success: true, Output: "ok", TypedOutput: new TextJobOutput("ok")));
        ClusterFixture.Liveness.SimulateOwnerLoss();
        await grain.RecoverUnfinishedJobsAsync();

        var j1 = await WaitForTerminalAsync(grain, "j1");
        var j3 = await WaitForTerminalAsync(grain, "j3");
        var j2 = await WaitForTerminalAsync(grain, "j2");
        Assert.Equal((JobStatus.Completed, JobStatus.Completed), (j1.Status, j3.Status));
        Assert.Equal(JobStatus.Failed, j2.Status);
        Assert.Contains("could not restart", j2.Error);
        Assert.Equal(3, await grain.GetFinishedCountAsync());
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("ok", -1)]
    [InlineData("ok", 101)]
    public async Task SubmitAsync_WithInvalidInput_ThrowsAndCreatesNoState(string jobId, int partialResults)
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => grain.SubmitAsync(jobId, new JobRequest("p", PartialResults: partialResults)));

        Assert.Empty(await grain.GetJobsAsync());
    }

    [Fact]
    public async Task SubmitAsync_WithNullRequestOrOversizedJobId_ThrowsAndCreatesNoState()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => grain.SubmitAsync("j1", null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => grain.SubmitAsync(new string('x', DurableJobGrain.MaxJobIdLength + 1), new JobRequest("p")));

        Assert.Empty(await grain.GetJobsAsync());
    }

    [Fact]
    public async Task SubmitAsync_AtTheLimits_IsAccepted()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold();

        await grain.SubmitAsync(new string('x', DurableJobGrain.MaxJobIdLength), new JobRequest("p", PartialResults: DurableJobGrain.MaxPartialResults));

        Assert.Single(await grain.GetJobsAsync());
    }

    [Fact]
    public async Task SubmitAsync_WithTheSameIdempotencyKey_IsARetryAndDoesNotRunTheJobAgain()
    {
        var grain = Grain(NewId());
        var runs = 0;
        ClusterFixture.Pool.UseResult(ctx =>
        {
            Interlocked.Increment(ref runs);
            return new RequestResult(ctx.RequestId, true, "ok", TypedOutput: new TextJobOutput("ok"));
        });

        await grain.SubmitAsync("j1", new JobRequest("p", IdempotencyKey: "req-1"));
        await grain.SubmitAsync("j1", new JobRequest("p", IdempotencyKey: "req-1"));   // retry after the first finished
        Assert.Equal(1, Volatile.Read(ref runs));

        await grain.SubmitAsync("j1", new JobRequest("p", IdempotencyKey: "req-2"));   // a different request: a new run
        Assert.Equal(2, Volatile.Read(ref runs));

        await grain.SubmitAsync("j1", new JobRequest("p"));                            // no key: the old rerun behaviour
        Assert.Equal(3, Volatile.Read(ref runs));
        Assert.Equal(3, (await grain.GetJobAsync("j1"))!.Epoch);
    }

    [Fact]
    public async Task SubmitAsync_KeepsOnlyTheNewestFinishedJobsAndNeverDropsRunningOnes()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold();
        await grain.SubmitAsync("running", new JobRequest("p"));

        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("ok"));
        for (var i = 1; i <= 9; i++)
        {
            await grain.SubmitAsync($"j{i}", new JobRequest("p"));
            await WaitForTerminalAsync(grain, $"j{i}");
            await Task.Delay(5, TestContext.Current.CancellationToken);   // distinct UpdatedAt ordering
        }

        var jobs = await grain.GetJobsAsync();
        Assert.Equal(JobStatus.Processing, jobs.Single(j => j.JobId == "running").Status);   // never pruned
        Assert.True(jobs.Count(j => j.IsTerminal) <= 6, $"retained {jobs.Count(j => j.IsTerminal)} finished jobs"); // cap 5 + the newest
        Assert.DoesNotContain(jobs, j => j.JobId == "j1");                                    // oldest finished is gone
        Assert.Contains(jobs, j => j.JobId == "j9");                                          // newest is kept
        Assert.Equal(9, await grain.GetFinishedCountAsync());                                 // the counter is independent of retention
    }

    [Fact]
    public void ShouldClearRecoveryMarker_OnlyForTheCheckTheMarkerWasSetFor()
    {
        var due = DateTimeOffset.UtcNow;
        var period = TimeSpan.FromSeconds(1);

        Assert.True(DurableJobGrain.ShouldClearRecoveryMarker(due, due, period));                            // the check we scheduled
        Assert.True(DurableJobGrain.ShouldClearRecoveryMarker(due.AddSeconds(-5), due, period));             // marker older than the fired check
        Assert.False(DurableJobGrain.ShouldClearRecoveryMarker(due + period, due, period));                  // a newer check replaced it: a duplicate of the old one fired
        Assert.False(DurableJobGrain.ShouldClearRecoveryMarker(null, due, period));                          // nothing to clear
    }

    // ── Jobs that outlive the grain's idle timeout ────────────────────────────
    // A job can run in the pool for longer than the grain may stay idle, so the grain can be deactivated while the work
    // is still running. The worker keeps a reference to the grain (not the activation), so whatever it sends next
    // must reactivate the grain and be applied, and nothing may mistake the job for lost and resubmit it.

    private static HeldRequest HeldFor(PoolCapture capture, string grainKey) =>
        capture.Requests.Single(r => r.Context.PartitionKey == grainKey);   // other tests' leftovers also use the pool

    private static async Task WaitForResultsAsync(IDurableJobGrain grain, string jobId, int results)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await grain.GetJobAsync(jobId) is { } job && job.ResultsReceived >= results)
                return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Job '{jobId}' did not reach {results} result(s).");
    }

    // ── Results come back through the grain's in-process mailbox ──────────────
    // The pool capture plays the part of a slow worker: it reports progress and partial results and finally completes, through the same
    // callbacks the real pool uses, possibly long after the job was submitted or after a recovery superseded the run.

    [Fact]
    public async Task Mailbox_PartialResultsThenFinal_AreAppliedInOrderAndCountedOnce()
    {
        var id = NewId();
        var grain = Grain(id);
        var capture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("p", PartialResults: 3));
        var worker = HeldFor(capture, id);

        worker.Report(33, "stage 1", new PartialResultDelta(1, 3, "r1", Redeliver: false));
        await WaitForResultsAsync(grain, "j1", 1);
        Assert.Equal("r1", (await grain.GetJobAsync("j1"))!.Output);

        worker.Report(66, "stage 2", new PartialResultDelta(2, 3, "r2", Redeliver: true));   // delivered twice
        await WaitForResultsAsync(grain, "j1", 2);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await grain.GetJobAsync("j1"))!.ResultsReceived);                   // the duplicate was dropped
        Assert.Equal(0, await grain.GetFinishedCountAsync());

        worker.Report(100, "stage 3", new PartialResultDelta(3, 3, "r3", Redeliver: false));
        await worker.CompleteAsync("final");

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal((JobStatus.Completed, "final", 4), (job.Status, job.Output, job.ResultsReceived));
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    [Fact]
    public async Task Mailbox_DataFromAWorkerThatAlreadyFinished_IsIgnored()
    {
        var id = NewId();
        var grain = Grain(id);
        var capture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("p", PartialResults: 1));
        var worker = HeldFor(capture, id);
        await worker.CompleteAsync("done");
        await WaitForTerminalAsync(grain, "j1");

        worker.Report(50, "late", new PartialResultDelta(1, 1, "late partial", Redeliver: false));
        await worker.CompleteAsync("a second final");   // the worker reports completion twice
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var job = (await grain.GetJobAsync("j1"))!;
        Assert.Equal((JobStatus.Completed, "done", 1), (job.Status, job.Output, job.ResultsReceived));
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    [Fact]
    public async Task Mailbox_LastPartialResultIsAppliedBeforeTheFinalOne()
    {
        // The staged handler reports its last partial result and returns at once, so the partial and the final result
        // are posted back to back. The mailbox keeps them in order; a partial applied after the final one would be ignored.
        var lostPartials = 0;
        for (var round = 0; round < 20; round++)
        {
            var id = NewId();
            var grain = Grain(id);
            var capture = ClusterFixture.Pool.Capture();
            await grain.SubmitAsync("j1", new JobRequest("p", PartialResults: 1));
            var worker = HeldFor(capture, id);

            worker.Report(100, "stage 1", new PartialResultDelta(1, 1, "last-partial", Redeliver: false));
            await worker.CompleteAsync("final");

            if ((await WaitForTerminalAsync(grain, "j1")).ResultsReceived != 2)
                lostPartials++;
        }

        Assert.Equal(0, lostPartials);
    }

    [Fact]
    public async Task Mailbox_DataFromTheRunLostToARecovery_IsIgnoredAndTheNewRunStartsClean()
    {
        var id = NewId();
        var grain = Grain(id);
        var capture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("p", PartialResults: 3));
        var lost = HeldFor(capture, id);
        var lostEpoch = (await grain.GetJobAsync("j1"))!.Epoch;
        lost.Report(33, "stage 1", new PartialResultDelta(1, 3, "lost-partial", Redeliver: false));
        await WaitForResultsAsync(grain, "j1", 1);

        // The owner is presumed dead and the job is restarted from scratch under a new epoch.
        ClusterFixture.Liveness.SimulateOwnerLoss();
        await grain.RecoverUnfinishedJobsAsync();
        await WaitForAttemptsAsync(grain, "j1", 1);
        var fresh = capture.Requests.Where(r => r.Context.PartitionKey == id).Last();
        Assert.NotEqual(lost.Context.RequestId, fresh.Context.RequestId);

        var restarted = (await grain.GetJobAsync("j1"))!;
        Assert.Equal((lostEpoch + 1, 0, (string?)null), (restarted.Epoch, restarted.ResultsReceived, restarted.Output));   // the lost run's partial data is gone

        // The lost run's worker was only slow: its data arrives after the recovery and must not touch the new run.
        lost.Report(99, "stale", new PartialResultDelta(2, 3, "stale partial", Redeliver: false));
        await lost.CompleteAsync("stale final");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var untouched = (await grain.GetJobAsync("j1"))!;
        Assert.Equal((JobStatus.Processing, 0, (string?)null), (untouched.Status, untouched.ResultsReceived, untouched.Output));
        Assert.Equal(0, await grain.GetFinishedCountAsync());

        // The new run delivers its own results, numbered from the start again.
        for (var stage = 1; stage <= 3; stage++)
            fresh.Report(stage * 33, $"stage {stage}", new PartialResultDelta(stage, 3, $"fresh-{stage}", Redeliver: stage == 2));
        await fresh.CompleteAsync("fresh final");

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal((JobStatus.Completed, "fresh final", 1, 4), (job.Status, job.Output, job.Attempts, job.ResultsReceived));
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    [Fact]
    public async Task Mailbox_LateDuplicateFromThePreviousRunOfAResubmittedJob_IsIgnored()
    {
        var id = NewId();
        var grain = Grain(id);
        var capture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("p"));
        var first = HeldFor(capture, id);
        await first.CompleteAsync("run-1");
        var firstRun = await WaitForTerminalAsync(grain, "j1");

        await grain.SubmitAsync("j1", new JobRequest("p"));   // same job id: a new run
        var second = capture.Requests.Where(r => r.Context.PartitionKey == id).Last();
        Assert.NotEqual(first.Context.RequestId, second.Context.RequestId);
        Assert.Equal(firstRun.Epoch + 1, (await grain.GetJobAsync("j1"))!.Epoch);

        await first.CompleteAsync("duplicate of run 1");   // a duplicate final result from the first run
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var running = (await grain.GetJobAsync("j1"))!;
        Assert.Equal((JobStatus.Processing, (string?)null), (running.Status, running.Output));
        Assert.Equal(1, await grain.GetFinishedCountAsync());

        await second.CompleteAsync("run-2");
        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal((JobStatus.Completed, "run-2"), (job.Status, job.Output));
        Assert.Equal(2, await grain.GetFinishedCountAsync());
    }

    // ── Jobs that outlive the grain's idle timeout: the grain stays in memory ─

    [Fact]
    public async Task KeepAlive_AGrainWithAJobInFlightSurvivesCollection_AndIsFreedWhenTheJobEnds()
    {
        var id = NewId();
        var grain = Grain(id);
        var management = fixture.Cluster.Client.GetGrain<IManagementGrain>(0);
        var capture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("p", PartialResults: 1));
        var worker = HeldFor(capture, id);

        // Idle collection (or an administrator) keeps trying to collect it: it must stay, because the worker can only
        // deliver to this activation.
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            await management.ForceActivationCollection(TimeSpan.Zero);
            Assert.True(await IsActiveAsync(grain), "a grain with a job in flight was collected");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        // The long job finally reports and finishes: nothing was lost.
        worker.Report(100, "stage 1", new PartialResultDelta(1, 1, "r1", Redeliver: false));
        await worker.CompleteAsync("final");
        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal((JobStatus.Completed, 0, 2), (job.Status, job.Attempts, job.ResultsReceived));
        Assert.Equal(1, capture.Requests.Count(r => r.Context.PartitionKey == id));   // never resubmitted

        // With nothing in flight the delay is lifted: the grain can be collected again and comes back from the journal.
        await DeactivateAsync(grain);
        var again = (await grain.GetJobAsync("j1"))!;
        Assert.Equal((JobStatus.Completed, "final", 2), (again.Status, again.Output, again.ResultsReceived));
    }

    [Fact]
    public async Task KeepAlive_AJobThatRunsForLong_IsNotResubmittedByTheScheduledChecks()
    {
        var id = NewId();
        var grain = Grain(id);
        var capture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("p"));
        var worker = HeldFor(capture, id);

        await Task.Delay(3500, TestContext.Current.CancellationToken);   // several scheduled checks fire meanwhile

        // The owner (this silo) is alive and the activation still runs the job, so it is not lost.
        Assert.Equal(1, capture.Requests.Count(r => r.Context.PartitionKey == id));
        var running = (await grain.GetJobAsync("j1"))!;
        Assert.Equal((JobStatus.Processing, 0), (running.Status, running.Attempts));

        await worker.CompleteAsync("finally done");
        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal((JobStatus.Completed, 0), (job.Status, job.Attempts));
        Assert.Equal(1, capture.Requests.Count(r => r.Context.PartitionKey == id));
    }

    // ── Request ids and input limits ──────────────────────────────────────────

    [Fact]
    public async Task SubmitAsync_TwoOwnersWithTheSameJobId_RunAtTheSameTimeWithDistinctRequestIds()
    {
        var a = NewId();
        var b = NewId();
        var capture = ClusterFixture.Pool.Capture();   // like the real pool, it rejects a second pending request with the same id

        await Grain(a).SubmitAsync("same", new JobRequest("p"));
        await Grain(b).SubmitAsync("same", new JobRequest("p"));

        var workerA = HeldFor(capture, a);
        var workerB = HeldFor(capture, b);
        Assert.Equal(DurableJobGrain.RunRequestId(a, "same", 1), workerA.Context.RequestId);
        Assert.Equal(DurableJobGrain.RunRequestId(b, "same", 1), workerB.Context.RequestId);
        Assert.NotEqual(workerA.Context.RequestId, workerB.Context.RequestId);

        await workerA.CompleteAsync("from a");
        await workerB.CompleteAsync("from b");
        Assert.Equal("from a", (await WaitForTerminalAsync(Grain(a), "same")).Output);
        Assert.Equal("from b", (await WaitForTerminalAsync(Grain(b), "same")).Output);
    }

    [Fact]
    public async Task SubmitAsync_WithAControlCharacterInTheJobId_ThrowsAndCreatesNoState()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => grain.SubmitAsync("bad\njob", new JobRequest("p")));

        Assert.Empty(await grain.GetJobsAsync());
    }

    [Fact]
    public void ValidateSubmission_RejectsAnOwnerAndJobIdThatDoNotFitInOnePoolRequestId()
    {
        var request = new JobRequest("p");

        DurableJobGrain.ValidateSubmission("owner", new string('x', DurableJobGrain.MaxJobIdLength), request);   // fits

        // "<owner>/<job>#<epoch>" must stay within the pool's 256 characters.
        Assert.Throws<ArgumentException>(() =>
            DurableJobGrain.ValidateSubmission(new string('o', 200), new string('x', DurableJobGrain.MaxJobIdLength), request));
    }

    [Fact]
    public void IsOrphaned_DecidesWhetherNobodyCanDeliverTheResultsOfAProcessingJob()
    {
        var liveness = new FakeJobOwnerLiveness();
        var lostOwner = liveness.SimulateOwnerLoss();
        var now = DateTimeOffset.UtcNow;
        var grace = TimeSpan.FromSeconds(10);
        DurableJobRecord Job(JobStatus status = JobStatus.Processing, string? owner = "gone", double quietSeconds = 60) =>
            new("j", status, 0, null, null, null, now.AddSeconds(-quietSeconds),
                Request: new JobRequest("p"), OwnerSilo: owner == "gone" ? lostOwner : owner);
        bool Orphaned(DurableJobRecord job, TimeSpan? g = null, bool tracked = false) =>
            DurableJobGrain.IsOrphaned(job, liveness, now, g ?? grace, tracked);

        // Owner is another silo that is gone: only after it has been quiet for the grace period.
        Assert.True(Orphaned(Job()));
        Assert.False(Orphaned(Job(quietSeconds: 2)));
        Assert.True(Orphaned(Job(quietSeconds: 2), g: TimeSpan.Zero));

        // Released by a deactivating activation: nobody can deliver, no waiting.
        Assert.True(Orphaned(Job(owner: null, quietSeconds: 0)));

        // Owned by this silo: fine while this activation runs it, lost if the activation does not (a reactivated grain).
        Assert.False(Orphaned(Job(owner: liveness.CurrentOwnerId), tracked: true));
        Assert.True(Orphaned(Job(owner: liveness.CurrentOwnerId, quietSeconds: 0), tracked: false));

        // Never for finished jobs or jobs that cannot be resubmitted.
        Assert.False(Orphaned(Job(status: JobStatus.Completed)));
        Assert.False(Orphaned(Job() with { Request = null }));
    }

    // ── Epochs never repeat within a grain ────────────────────────────────────

    [Fact]
    public async Task SubmitAsync_AJobIdWhoseRecordWasPruned_NeverReusesAnEpoch_SoStaleDataOfTheOldRunIsIgnored()
    {
        var id = NewId();
        var grain = Grain(id);

        // The first run of "j1" finishes; its worker is kept around.
        var firstCapture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("p"));
        var oldWorker = HeldFor(firstCapture, id);
        var oldEpoch = (await grain.GetJobAsync("j1"))!.Epoch;
        await oldWorker.CompleteAsync("old run");
        await WaitForTerminalAsync(grain, "j1");

        // Enough other jobs finish for retention (5 in the test silo) to prune "j1".
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("ok"));
        for (var i = 1; i <= 8; i++)
        {
            await grain.SubmitAsync($"other{i}", new JobRequest("p"));
            await WaitForTerminalAsync(grain, $"other{i}");
            await Task.Delay(5, TestContext.Current.CancellationToken);   // distinct UpdatedAt ordering
        }

        Assert.Null(await grain.GetJobAsync("j1"));   // pruned

        // "j1" is submitted again. Its record is gone, but its epoch must not start over at one it already had.
        var secondCapture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("p"));
        var newWorker = HeldFor(secondCapture, id);
        var again = (await grain.GetJobAsync("j1"))!;
        Assert.True(again.Epoch > oldEpoch, $"epoch {again.Epoch} was already used by the first run ({oldEpoch})");

        // A duplicate final result of the OLD run arrives late: it must not complete the new run.
        await oldWorker.CompleteAsync("STALE: from the first run");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var running = (await grain.GetJobAsync("j1"))!;
        Assert.Equal((JobStatus.Processing, (string?)null), (running.Status, running.Output));

        await newWorker.CompleteAsync("new run");
        Assert.Equal("new run", (await WaitForTerminalAsync(grain, "j1")).Output);
    }

    [Fact]
    public async Task Epochs_OnlyGoUpAcrossTheJobsOfOneGrain_IncludingRecoveries()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Hold();
        await grain.SubmitAsync("a", new JobRequest("p"));
        await grain.SubmitAsync("b", new JobRequest("p"));
        var a = (await grain.GetJobAsync("a"))!.Epoch;
        var b = (await grain.GetJobAsync("b"))!.Epoch;
        Assert.True(b > a);

        ClusterFixture.Liveness.SimulateOwnerLoss();
        await grain.RecoverUnfinishedJobsAsync();
        await WaitForAttemptsAsync(grain, "a", 1);
        await WaitForAttemptsAsync(grain, "b", 1);

        var epochs = new[] { a, b, (await grain.GetJobAsync("a"))!.Epoch, (await grain.GetJobAsync("b"))!.Epoch };
        Assert.Equal(epochs.Length, epochs.Distinct().Count());   // no epoch handed out twice
        Assert.True(epochs[2] > b && epochs[3] > b);              // a recovery always gets a fresh, higher one
    }

    // ── Run timeout: the pure rules ───────────────────────────────────────────

    [Fact]
    public void EffectiveMaxRun_IsOffForNullZeroAndInfinite()
    {
        Assert.Null(DurableJobGrain.EffectiveMaxRun(null));
        Assert.Null(DurableJobGrain.EffectiveMaxRun(TimeSpan.Zero));
        Assert.Null(DurableJobGrain.EffectiveMaxRun(Timeout.InfiniteTimeSpan));
        Assert.Equal(TimeSpan.FromMinutes(5), DurableJobGrain.EffectiveMaxRun(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void RunDeadline_IsTheLimitPlusAClampedAllowance()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(DurableJobGrain.RunDeadline(start, null));
        Assert.Equal(start.AddSeconds(2).AddMilliseconds(500), DurableJobGrain.RunDeadline(start, TimeSpan.FromSeconds(2)));      // a quarter, 500 ms
        Assert.Equal(start.AddMilliseconds(400 + 250), DurableJobGrain.RunDeadline(start, TimeSpan.FromMilliseconds(400)));         // a quarter would be 100 ms: at least 250 ms
        Assert.Equal(start.AddHours(1).AddMinutes(1), DurableJobGrain.RunDeadline(start, TimeSpan.FromHours(1)));                  // a quarter would be 15 min: at most a minute
    }

    [Fact]
    public void SweepPeriod_IsAThirdOfTheKeepAliveSliceOrSoonerForAShortLimit()
    {
        var slice = TimeSpan.FromMinutes(5);

        Assert.Equal(TimeSpan.FromMinutes(5) / 3, DurableJobGrain.SweepPeriod(slice, null));                                  // no limit: just the keep-alive renewal
        Assert.Equal(TimeSpan.FromMinutes(5) / 3, DurableJobGrain.SweepPeriod(slice, TimeSpan.FromHours(1)));                 // a quarter of the limit is longer
        Assert.Equal(TimeSpan.FromMilliseconds(500), DurableJobGrain.SweepPeriod(slice, TimeSpan.FromSeconds(2)));            // a quarter of the limit
        Assert.Equal(TimeSpan.FromMilliseconds(100), DurableJobGrain.SweepPeriod(slice, TimeSpan.FromMilliseconds(100)));     // never faster than 100 ms
    }

    // ── Scheduled recovery check (Orleans durable jobs) ───────────────────────

    [Fact]
    public async Task ScheduledCheck_WhenOwnerLost_RecoversJobWithoutAnyoneCallingTheGrain()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.Hold();
        await grain.SubmitAsync("j1", new JobRequest("payload"));

        var runs = 0;
        ClusterFixture.Pool.UseResult(ctx =>
        {
            // Count only this grain's work: other tests leave held jobs whose own recovery also uses the shared pool.
            if (ctx.PartitionKey == id)
                Interlocked.Increment(ref runs);
            return new RequestResult(ctx.RequestId, Success: true, Output: "by-scheduled-check");
        });
        ClusterFixture.Liveness.SimulateOwnerLoss();

        // Nothing calls the grain from here: only the scheduled durable job can make it notice and resubmit the work.
        await WaitUntilAsync(() => Volatile.Read(ref runs) > 0, TimeSpan.FromSeconds(20));
        Assert.Equal(1, Volatile.Read(ref runs));

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task ScheduledCheck_WhileOwnerAlive_KeepsCheckingWithoutResubmitting()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.Hold();
        await grain.SubmitAsync("j1", new JobRequest("payload"));

        var runs = 0;
        ClusterFixture.Pool.UseResult(ctx =>
        {
            // Count only this grain's work: other tests leave held jobs whose own recovery also uses the shared pool.
            if (ctx.PartitionKey == id)
                Interlocked.Increment(ref runs);
            return new RequestResult(ctx.RequestId, Success: true, Output: "after-owner-loss");
        });

        // Several scheduled checks fire while the owner is alive. They must not resubmit.
        await Task.Delay(4000, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref runs));

        // The chain must still be running: losing the owner now is noticed without anyone calling the grain.
        ClusterFixture.Liveness.SimulateOwnerLoss();
        await WaitUntilAsync(() => Volatile.Read(ref runs) > 0, TimeSpan.FromSeconds(20));
        Assert.Equal(1, Volatile.Read(ref runs));
        Assert.Equal(JobStatus.Completed, (await WaitForTerminalAsync(grain, "j1")).Status);
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_WhenProcessing_CancelsTheJobAndCountsItOnce()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("payload"));
        var epoch = (await grain.GetJobAsync("j1"))!.Epoch;
        var requestId = DurableJobGrain.RunRequestId(id, "j1", epoch);
        ClusterFixture.Monitor.ConfigureCancel(requestId, result: true);

        Assert.True(await grain.CancelAsync("j1"));

        var job = (await grain.GetJobAsync("j1"))!;
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.True(job.IsTerminal);
        Assert.Equal(1, await grain.GetFinishedCountAsync());
        Assert.Contains(requestId, ClusterFixture.Monitor.CancelRequests);   // a queued run is taken out of the pool

        Assert.False(await grain.CancelAsync("j1"));   // already finished
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    [Fact]
    public async Task CancelAsync_WhenUnknownOrFinished_ReturnsFalseAndChangesNothing()
    {
        var grain = Grain(NewId());
        Assert.False(await grain.CancelAsync("missing"));

        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("done"));
        await grain.SubmitAsync("j1", new JobRequest("payload"));
        await WaitForTerminalAsync(grain, "j1");

        Assert.False(await grain.CancelAsync("j1"));
        var job = (await grain.GetJobAsync("j1"))!;
        Assert.Equal((JobStatus.Completed, "done"), (job.Status, job.Output));
    }

    [Fact]
    public async Task CancelAsync_WhenTheRunReportsLater_IgnoresItsData()
    {
        var grain = Grain(NewId());
        var capture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("payload"));
        var held = Assert.Single(capture.Requests);

        Assert.True(await grain.CancelAsync("j1"));

        // The handler was already running and cannot be stopped: whatever it reports is ignored.
        held.Report(50, "halfway");
        await held.CompleteAsync("late result");
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var job = (await grain.GetJobAsync("j1"))!;
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Null(job.Output);
        Assert.Equal(0, job.ResultsReceived);
        Assert.Equal(1, await grain.GetFinishedCountAsync());
    }

    [Fact]
    public async Task CancelAsync_ThenResubmit_StartsANewRun()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("first"));
        var firstEpoch = (await grain.GetJobAsync("j1"))!.Epoch;
        Assert.True(await grain.CancelAsync("j1"));

        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("second"));
        await grain.SubmitAsync("j1", new JobRequest("second"));

        var job = await WaitForTerminalAsync(grain, "j1");
        Assert.Equal((JobStatus.Completed, "second"), (job.Status, job.Output));
        Assert.True(job.Epoch > firstEpoch);
    }

    [Fact]
    public async Task CancelAsync_WhenOwnerWasLost_StopsRecoveryFromRestartingTheJob()
    {
        var grain = Grain(NewId());
        var capture = ClusterFixture.Pool.Capture();
        await grain.SubmitAsync("j1", new JobRequest("payload"));
        Assert.Equal(1, capture.Count);

        // The owner is gone: without the cancel, recovery would resubmit this job.
        ClusterFixture.Liveness.SimulateOwnerLoss();
        Assert.True(await grain.CancelAsync("j1"));

        Assert.Equal(0, await grain.RecoverUnfinishedJobsAsync());
        Assert.Equal(1, capture.Count);   // nothing was enqueued again
        Assert.Equal(JobStatus.Cancelled, (await grain.GetJobAsync("j1"))!.Status);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    private static async Task<DurableJobRecord> WaitForTerminalAsync(IDurableJobGrain grain, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await grain.GetJobAsync(jobId) is { IsTerminal: true } job)
                return job;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Job '{jobId}' did not reach a terminal state.");
    }

    private static async Task<int> WaitForAttemptsAsync(IDurableJobGrain grain, string jobId, int attempts)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await grain.GetJobAsync(jobId) is { } job && job.Attempts >= attempts)
                return job.Attempts;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Job '{jobId}' did not reach {attempts} recovery attempt(s).");
    }

    private async Task<bool> IsActiveAsync(IDurableJobGrain grain)
    {
        var management = fixture.Cluster.Client.GetGrain<IManagementGrain>(0);
        var id = grain.GetGrainId();
        return (await management.GetDetailedGrainStatistics()).Any(s => s.GrainId == id);
    }

    /// <summary>Waits until the grain has an activation, without calling it (a call would activate it).</summary>
    /// <summary>
    /// Collects idle activations and waits until <paramref name="grain"/> itself is gone. Other grains are not waited for:
    /// tests leave held jobs behind whose scheduled recovery checks keep reactivating their grains.
    /// </summary>
    private async Task DeactivateAsync(IDurableJobGrain grain)
    {
        var management = fixture.Cluster.Client.GetGrain<IManagementGrain>(0);
        var id = grain.GetGrainId();
        async Task<bool> IsActiveAsync() =>
            (await management.GetDetailedGrainStatistics()).Any(s => s.GrainId == id);

        Assert.True(await IsActiveAsync(), "expected a live activation before collection");

        // A grain with a held job is also poked by its scheduled recovery checks, so one collection can miss it
        // (it was busy at that moment). Keep collecting until it is observed gone.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        do
        {
            await management.ForceActivationCollection(TimeSpan.Zero);
            if (!await IsActiveAsync())
                return;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail("the activation was not collected");
    }
}
