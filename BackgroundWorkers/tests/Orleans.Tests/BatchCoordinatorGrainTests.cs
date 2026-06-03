namespace Orleans.Tests;

[Collection("ClusterCollection")]
public sealed class BatchCoordinatorGrainTests(ClusterFixture fixture)
{
    private IBatchCoordinatorGrain Grain(string id) =>
        fixture.Cluster.GrainFactory.GetGrain<IBatchCoordinatorGrain>(id);

    private static string NewId() => $"batch-{Guid.NewGuid():N}";

    // ── GetSummaryAsync — no history ──────────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_WhenNoHistory_ReturnsUnknownAndZeroCounts()
    {
        var summary = await Grain(NewId()).GetSummaryAsync();

        Assert.Equal(JobStatus.Unknown, summary.OverallStatus);
        Assert.Equal(0, summary.ItemCount);
        Assert.Equal(0, summary.TotalCompleted);
    }

    // ── ProcessBatchAsync — fan-out ───────────────────────────────────────────

    [Fact]
    public async Task ProcessBatchAsync_SuccessPath_SetsCompletedForAllItems()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.UseSuccess();

        await grain.ProcessBatchAsync(itemCount: 3, workerCount: 2);

        var summary = await grain.GetSummaryAsync();

        Assert.Equal(3, summary.ItemCount);
        Assert.Equal(2, summary.WorkerCount);
        Assert.Equal(3, summary.TotalCompleted);
        Assert.Equal(0, summary.TotalFailed);
        Assert.Equal(0, summary.TotalCancelled);
        Assert.Equal(JobStatus.Completed, summary.OverallStatus);
    }

    [Fact]
    public async Task ProcessBatchAsync_ErrorPath_SetsFailedForAllItems()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.UseError(new InvalidOperationException("boom"));

        await grain.ProcessBatchAsync(itemCount: 2, workerCount: 1);

        var summary = await grain.GetSummaryAsync();

        Assert.Equal(2, summary.TotalFailed);
        Assert.Equal(0, summary.TotalCompleted);
        Assert.Equal(JobStatus.Failed, summary.OverallStatus);
    }

    [Fact]
    public async Task ProcessBatchAsync_CancelPath_SetsCancelledForAllItems()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.UseCancel();

        await grain.ProcessBatchAsync(itemCount: 2, workerCount: 2);

        var summary = await grain.GetSummaryAsync();

        Assert.Equal(2, summary.TotalCancelled);
        Assert.Equal(0, summary.TotalCompleted);
        Assert.Equal(JobStatus.Cancelled, summary.OverallStatus);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenAlreadyProcessing_IsNoOp()
    {
        var id = NewId();
        var grain = Grain(id);

        // Pre-seed as Processing so the guard fires.
        ClusterFixture.Tracker.SetStatus(id, JobStatus.Processing);
        ClusterFixture.Tracker.SetOutput(id, new BatchMetaOutput(5, 2));
        ClusterFixture.Pool.Hold(); // must not be called

        // Should return immediately without re-enqueueing items.
        var summary = await grain.ProcessBatchAsync(itemCount: 5, workerCount: 2);

        Assert.Equal(JobStatus.Processing, summary.OverallStatus);
    }

    // ── GetSummaryAsync — aggregation ─────────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_MixedResults_AggregatesCorrectly()
    {
        var id = NewId();

        // Pre-seed metadata and per-item statuses directly in tracker.
        ClusterFixture.Tracker.SetOutput(id, new BatchMetaOutput(4, 2));
        ClusterFixture.Tracker.SetStatus(id, JobStatus.Processing);
        ClusterFixture.Tracker.SetStatus($"{id}:item-0", JobStatus.Completed);
        ClusterFixture.Tracker.SetStatus($"{id}:item-1", JobStatus.Failed);
        ClusterFixture.Tracker.SetStatus($"{id}:item-2", JobStatus.Cancelled);
        ClusterFixture.Tracker.SetStatus($"{id}:item-3", JobStatus.Completed);

        var summary = await Grain(id).GetSummaryAsync();

        Assert.Equal(4, summary.ItemCount);
        Assert.Equal(2, summary.TotalCompleted);
        Assert.Equal(1, summary.TotalFailed);
        Assert.Equal(1, summary.TotalCancelled);
        // Any failure → overall Failed
        Assert.Equal(JobStatus.Failed, summary.OverallStatus);
    }

    [Fact]
    public async Task GetSummaryAsync_AllCancelled_OverallStatusIsCancelled()
    {
        var id = NewId();

        ClusterFixture.Tracker.SetOutput(id, new BatchMetaOutput(2, 1));
        ClusterFixture.Tracker.SetStatus(id, JobStatus.Cancelled);
        ClusterFixture.Tracker.SetStatus($"{id}:item-0", JobStatus.Cancelled);
        ClusterFixture.Tracker.SetStatus($"{id}:item-1", JobStatus.Cancelled);

        var summary = await Grain(id).GetSummaryAsync();

        Assert.Equal(JobStatus.Cancelled, summary.OverallStatus);
    }

    // ── CancelAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_WhenNoHistory_ReturnsFalse()
    {
        Assert.False(await Grain(NewId()).CancelAsync());
    }

    [Fact]
    public async Task CancelAsync_WhenItemsPending_AndMonitorSucceeds_ReturnsTrueAndCancelsAll()
    {
        var id = NewId();
        var itemCount = 3;

        ClusterFixture.Tracker.SetStatus(id, JobStatus.Processing);
        ClusterFixture.Tracker.SetOutput(id, new BatchMetaOutput(itemCount, 2));

        for (int i = 0; i < itemCount; i++)
        {
            var key = $"{id}:item-{i}";
            ClusterFixture.Tracker.SetStatus(key, JobStatus.Pending);
            ClusterFixture.Monitor.ConfigureCancel(key, result: true);
        }

        var cancelled = await Grain(id).CancelAsync();

        Assert.True(cancelled);

        var summary = await Grain(id).GetSummaryAsync();
        Assert.Equal(itemCount, summary.TotalCancelled);
        Assert.Equal(JobStatus.Cancelled, summary.OverallStatus);
    }

    [Fact]
    public async Task CancelAsync_WhenAlreadyCompleted_ReturnsFalse()
    {
        var id = NewId();
        ClusterFixture.Tracker.SetStatus(id, JobStatus.Completed);
        ClusterFixture.Tracker.SetOutput(id, new BatchMetaOutput(2, 1));

        Assert.False(await Grain(id).CancelAsync());
    }

    [Fact]
    public async Task CancelAsync_PartialCancel_ReturnsTrueForAtLeastOneCancelled()
    {
        var id = NewId();

        ClusterFixture.Tracker.SetStatus(id, JobStatus.Processing);
        ClusterFixture.Tracker.SetOutput(id, new BatchMetaOutput(3, 1));
        // item-0 still pending (cancellable), item-1 already dispatched (not cancellable), item-2 pending
        ClusterFixture.Tracker.SetStatus($"{id}:item-0", JobStatus.Pending);
        ClusterFixture.Tracker.SetStatus($"{id}:item-1", JobStatus.Processing);
        ClusterFixture.Tracker.SetStatus($"{id}:item-2", JobStatus.Pending);

        ClusterFixture.Monitor.ConfigureCancel($"{id}:item-0", result: true);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:item-1", result: false); // already dispatched
        ClusterFixture.Monitor.ConfigureCancel($"{id}:item-2", result: true);

        var cancelled = await Grain(id).CancelAsync();

        Assert.True(cancelled);
    }

    // ── Worker statuses ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_WorkerStatuses_ReflectsRoundRobinAssignment()
    {
        var id = NewId();
        const int itemCount = 4;
        const int workerCount = 2;

        ClusterFixture.Tracker.SetOutput(id, new BatchMetaOutput(itemCount, workerCount));
        ClusterFixture.Tracker.SetStatus(id, JobStatus.Processing);
        // Items 0 and 2 → worker 0; items 1 and 3 → worker 1; all completed.
        for (int i = 0; i < itemCount; i++)
            ClusterFixture.Tracker.SetStatus($"{id}:item-{i}", JobStatus.Completed);

        var summary = await Grain(id).GetSummaryAsync();

        Assert.Equal(workerCount, summary.WorkerStatuses.Count);
        // Worker 0 processed items 0 and 2 → 2 items.
        Assert.Equal(2, summary.WorkerStatuses[0].ItemsProcessed);
        // Worker 1 processed items 1 and 3 → 2 items.
        Assert.Equal(2, summary.WorkerStatuses[1].ItemsProcessed);
    }
}
