namespace Orleans.Tests;

[Collection("ClusterCollection")]
public sealed class ReportGrainTests(ClusterFixture fixture)
{
    private IReportGrain Grain(string id) =>
        fixture.Cluster.GrainFactory.GetGrain<IReportGrain>(id);

    private static string NewId() => $"report-{Guid.NewGuid():N}";

    // ── GetSummaryAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_WhenNoHistory_ReturnsAllUnknown()
    {
        var grain = Grain(NewId());
        var summary = await grain.GetSummaryAsync();

        Assert.Equal(JobStatus.Unknown, summary.GenerateStatus);
        Assert.Equal(JobStatus.Unknown, summary.ReviewStatus);
        Assert.Equal(JobStatus.Unknown, summary.PublishStatus);
    }

    // ── GenerateAsync idempotency ─────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WhenUnknown_DispatchesAndReturnsProcessing()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess();

        var result = await grain.GenerateAsync();

        Assert.Equal(JobStatus.Processing, result);
    }

    [Fact]
    public async Task GenerateAsync_WhenAlreadyProcessing_IsNoOp()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:generate", JobStatus.Processing);
        ClusterFixture.Pool.Hold(); // must not be called

        var result = await grain.GenerateAsync();

        Assert.Equal(JobStatus.Processing, result);
    }

    [Fact]
    public async Task GenerateAsync_WhenAlreadyCompleted_IsNoOp()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:generate", JobStatus.Completed);
        ClusterFixture.Pool.Hold(); // must not be called

        var result = await grain.GenerateAsync();

        Assert.Equal(JobStatus.Completed, result);
    }

    [Fact]
    public async Task GenerateAsync_WhenFailed_Retries()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:generate", JobStatus.Failed);
        ClusterFixture.Pool.UseSuccess();

        var result = await grain.GenerateAsync();

        Assert.Equal(JobStatus.Processing, result);
    }

    [Fact]
    public async Task GenerateAsync_WhenCancelled_Retries()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:generate", JobStatus.Cancelled);
        ClusterFixture.Pool.UseSuccess();

        var result = await grain.GenerateAsync();

        Assert.Equal(JobStatus.Processing, result);
    }

    // ── ReviewAsync / PublishAsync dispatch ───────────────────────────────────

    [Fact]
    public async Task ReviewAsync_WhenUnknown_DispatchesAndReturnsProcessing()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess();

        Assert.Equal(JobStatus.Processing, await grain.ReviewAsync());
    }

    [Fact]
    public async Task PublishAsync_WhenUnknown_DispatchesAndReturnsProcessing()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess();

        Assert.Equal(JobStatus.Processing, await grain.PublishAsync());
    }

    // ── Full lifecycle ────────────────────────────────────────────────────────

    [Fact]
    public async Task AllOperations_CompletedIndependently()
    {
        var id = NewId();
        var grain = Grain(id);

        // The pool callback closure sets tracker status synchronously, so by the time
        // each grain method returns the tracker already holds Completed for that key.
        ClusterFixture.Pool.UseSuccess();

        await grain.GenerateAsync();
        await grain.ReviewAsync();
        await grain.PublishAsync();

        var summary = await grain.GetSummaryAsync();
        Assert.Equal(JobStatus.Completed, summary.GenerateStatus);
        Assert.Equal(JobStatus.Completed, summary.ReviewStatus);
        Assert.Equal(JobStatus.Completed, summary.PublishStatus);
    }

    [Fact]
    public async Task GenerateAsync_WhenError_SetsFailedInTracker()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.UseError(new InvalidOperationException("gen fail"));

        await grain.GenerateAsync();

        // The closure writes Failed synchronously; GetSummaryAsync reads from tracker.
        var summary = await grain.GetSummaryAsync();
        Assert.Equal(JobStatus.Failed, summary.GenerateStatus);
    }

    [Fact]
    public async Task GenerateAsync_WhenCancelled_SetsCancelledInTracker()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.UseCancel();

        await grain.GenerateAsync();

        var summary = await grain.GetSummaryAsync();
        Assert.Equal(JobStatus.Cancelled, summary.GenerateStatus);
    }

    // ── CancelAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_WhenNoOperationsActive_ReturnsFalse()
    {
        Assert.False(await Grain(NewId()).CancelAsync());
    }

    [Fact]
    public async Task CancelAsync_WhenOneOperationProcessing_AndMonitorSucceeds_ReturnsTrueAndSetsCancelled()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:generate", JobStatus.Processing);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:generate", result: true);

        var cancelled = await grain.CancelAsync();

        Assert.True(cancelled);
        var summary = await grain.GetSummaryAsync();
        Assert.Equal(JobStatus.Cancelled, summary.GenerateStatus);
        Assert.Equal(JobStatus.Unknown, summary.ReviewStatus);
        Assert.Equal(JobStatus.Unknown, summary.PublishStatus);
    }

    [Fact]
    public async Task CancelAsync_WhenAllOperationsProcessing_AndMonitorSucceeds_ReturnsTrueAndSetsAllCancelled()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:generate", JobStatus.Processing);
        ClusterFixture.Tracker.SetStatus($"{id}:review", JobStatus.Processing);
        ClusterFixture.Tracker.SetStatus($"{id}:publish", JobStatus.Processing);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:generate", result: true);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:review", result: true);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:publish", result: true);

        var cancelled = await grain.CancelAsync();

        Assert.True(cancelled);
        var summary = await grain.GetSummaryAsync();
        Assert.Equal(JobStatus.Cancelled, summary.GenerateStatus);
        Assert.Equal(JobStatus.Cancelled, summary.ReviewStatus);
        Assert.Equal(JobStatus.Cancelled, summary.PublishStatus);
    }

    [Fact]
    public async Task CancelAsync_WhenMonitorFails_ReturnsFalseAndStatusUnchanged()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:generate", JobStatus.Processing);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:generate", result: false);

        var cancelled = await grain.CancelAsync();

        Assert.False(cancelled);
        var summary = await grain.GetSummaryAsync();
        Assert.Equal(JobStatus.Processing, summary.GenerateStatus);
    }
}
