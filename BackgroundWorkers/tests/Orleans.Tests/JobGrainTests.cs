namespace Orleans.Tests;

[Collection("ClusterCollection")]
public sealed class JobGrainTests(ClusterFixture fixture)
{
    private IJobGrain Grain(string id) =>
        fixture.Cluster.GrainFactory.GetGrain<IJobGrain>(id);

    private static string NewId() => $"job-{Guid.NewGuid():N}";

    // ── GetStatusAsync / GetProgressAsync ─────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_WhenNoHistory_ReturnsUnknown()
    {
        Assert.Equal(JobStatus.Unknown, await Grain(NewId()).GetStatusAsync());
    }

    [Fact]
    public async Task GetProgressAsync_WhenNoProgress_ReturnsNull()
    {
        Assert.Null(await Grain(NewId()).GetProgressAsync());
    }

    // ── SubmitAsync idempotency ────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAsync_WhenAlreadyProcessing_IsNoOp()
    {
        var id = NewId();
        var grain = Grain(id);

        // Pre-seed Processing — the grain's idempotency guard checks this.
        ClusterFixture.Tracker.SetStatus(id, JobStatus.Processing);

        // If the guard doesn't fire, UseSuccess would enqueue and complete.
        // Holding the pool would cause the test to time out if the grain somehow
        // reaches EnqueueAsync, making the test self-documenting on failure.
        ClusterFixture.Pool.Hold();

        await grain.SubmitAsync(new JobRequest("payload"));

        // Guard should have returned early; status stays Processing.
        Assert.Equal(JobStatus.Processing, await grain.GetStatusAsync());
    }

    // ── SubmitAsync completion paths ──────────────────────────────────────────

    [Fact]
    public async Task SubmitAsync_WithJobRequest_CompletesSuccessfully()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("done"));

        await grain.SubmitAsync(new JobRequest("payload"));

        await GrainTestExtensions.WaitForStatusAsync(grain, JobStatus.Completed);
    }

    [Fact]
    public async Task SubmitAsync_WithBatchJobRequest_CompletesSuccessfully()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("batch done"));

        await grain.SubmitAsync(new BatchJobRequest(new[] { "item1", "item2" }));

        await GrainTestExtensions.WaitForStatusAsync(grain, JobStatus.Completed);
    }

    [Fact]
    public async Task SubmitAsync_WithScheduledJobRequest_CompletesSuccessfully()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess(typedOutput: new TextJobOutput("scheduled done"));

        await grain.SubmitAsync(new ScheduledJobRequest("payload", DateTimeOffset.UtcNow.AddMinutes(5)));

        await GrainTestExtensions.WaitForStatusAsync(grain, JobStatus.Completed);
    }

    [Fact]
    public async Task SubmitAsync_WithCancellation_SetsCancelledStatus()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseCancel();

        await grain.SubmitAsync(new JobRequest("payload"));

        await GrainTestExtensions.WaitForStatusAsync(grain, JobStatus.Cancelled);
    }

    [Fact]
    public async Task SubmitAsync_WithError_SetsFailedStatus()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseError(new InvalidOperationException("boom"));

        await grain.SubmitAsync(new JobRequest("payload"));

        await GrainTestExtensions.WaitForStatusAsync(grain, JobStatus.Failed);
    }

    // ── Progress ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAsync_WhenPoolReportsProgress_StoresProgressInTracker()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Pool.UseResultWithProgress(
            percent: 75,
            msg: "three-quarters",
            factory: ctx => new RequestResult(ctx.RequestId, Success: true, Output: null,
                TypedOutput: new TextJobOutput("done")));

        await grain.SubmitAsync(new JobRequest("payload"));

        // Wait for the completion observer so we know all one-way messages have been delivered.
        await GrainTestExtensions.WaitForStatusAsync(grain, JobStatus.Completed);

        // OnProgress runs before OnCompleted (messages arrive in that order), so the
        // tracker should already have the progress snapshot at this point.
        await GrainTestExtensions.WaitForConditionAsync(async () =>
        {
            var snap = await grain.GetProgressAsync();
            return snap is { PercentComplete: 75 };
        });

        var progress = await grain.GetProgressAsync();
        Assert.NotNull(progress);
        Assert.Equal(75, progress.PercentComplete);
        Assert.Equal("three-quarters", progress.Message);
    }

    // ── TryCancelJobAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task TryCancelJobAsync_WhenStatusUnknown_ReturnsFalse()
    {
        Assert.False(await Grain(NewId()).TryCancelAsync());
    }

    [Fact]
    public async Task TryCancelJobAsync_WhenCompleted_ReturnsFalse()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Tracker.SetStatus(id, JobStatus.Completed);

        Assert.False(await grain.TryCancelAsync());
    }

    [Fact]
    public async Task TryCancelJobAsync_WhenProcessing_AndMonitorSucceeds_ReturnsTrueAndSetsCancelled()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus(id, JobStatus.Processing);
        ClusterFixture.Monitor.ConfigureCancel(id, result: true);

        var cancelled = await grain.TryCancelAsync();

        Assert.True(cancelled);
        Assert.Equal(JobStatus.Cancelled, await grain.GetStatusAsync());
    }

    [Fact]
    public async Task TryCancelJobAsync_WhenProcessing_AndMonitorFails_ReturnsFalseAndStatusUnchanged()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus(id, JobStatus.Processing);
        ClusterFixture.Monitor.ConfigureCancel(id, result: false);

        var cancelled = await grain.TryCancelAsync();

        Assert.False(cancelled);
        Assert.Equal(JobStatus.Processing, await grain.GetStatusAsync());
    }
}
