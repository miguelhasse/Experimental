namespace Orleans.Tests;

[Collection("ClusterCollection")]
public sealed class BatchWorkerGrainTests(ClusterFixture fixture)
{
    private IBatchWorkerGrain Grain(string key) =>
        fixture.Cluster.GrainFactory.GetGrain<IBatchWorkerGrain>(key);

    private static string WorkerKey(string batchId, int workerIndex) =>
        $"{batchId}:worker-{workerIndex}";

    private static string ItemKey(string batchId, int itemIndex) =>
        $"{batchId}:item-{itemIndex}";

    private static string NewBatchId() => $"batch-{Guid.NewGuid():N}";

    // ── CancelAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_WhenNoMetadata_ReturnsFalse()
    {
        // Worker key exists but no BatchMetaOutput has been seeded — nothing to cancel.
        var batchId = NewBatchId();
        Assert.False(await Grain(WorkerKey(batchId, 0)).CancelAsync());
    }

    [Fact]
    public async Task CancelAsync_WhenWorkerItemPending_AndMonitorSucceeds_ReturnsTrueAndSetsCancelled()
    {
        var batchId = NewBatchId();
        const int workerIndex = 0;
        const int workerCount = 1;

        // Worker 0 owns items 0, 1, 2 (round-robin with 1 worker).
        ClusterFixture.Tracker.SetOutput(batchId, new BatchMetaOutput(ItemCount: 3, WorkerCount: workerCount));

        for (int i = 0; i < 3; i++)
        {
            var key = ItemKey(batchId, i);
            ClusterFixture.Tracker.SetStatus(key, JobStatus.Pending);
            ClusterFixture.Monitor.ConfigureCancel(key, result: true);
        }

        var cancelled = await Grain(WorkerKey(batchId, workerIndex)).CancelAsync();

        Assert.True(cancelled);
        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus(ItemKey(batchId, 0)));
        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus(ItemKey(batchId, 1)));
        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus(ItemKey(batchId, 2)));
    }

    [Fact]
    public async Task CancelAsync_WhenWorkerOwnsSubsetOfItems_CancelsOnlyOwnItems()
    {
        var batchId = NewBatchId();
        // 4 items, 2 workers: worker-0 owns items 0, 2; worker-1 owns items 1, 3.
        ClusterFixture.Tracker.SetOutput(batchId, new BatchMetaOutput(ItemCount: 4, WorkerCount: 2));

        for (int i = 0; i < 4; i++)
        {
            ClusterFixture.Tracker.SetStatus(ItemKey(batchId, i), JobStatus.Pending);
            ClusterFixture.Monitor.ConfigureCancel(ItemKey(batchId, i), result: true);
        }

        // Only cancel via worker-0.
        var cancelled = await Grain(WorkerKey(batchId, workerIndex: 0)).CancelAsync();

        Assert.True(cancelled);
        // Worker-0 items should be cancelled.
        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus(ItemKey(batchId, 0)));
        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus(ItemKey(batchId, 2)));
        // Worker-1 items must remain untouched.
        Assert.Equal(JobStatus.Pending, ClusterFixture.Tracker.GetStatus(ItemKey(batchId, 1)));
        Assert.Equal(JobStatus.Pending, ClusterFixture.Tracker.GetStatus(ItemKey(batchId, 3)));
    }

    [Fact]
    public async Task CancelAsync_WhenMonitorFails_ReturnsFalseAndStatusUnchanged()
    {
        var batchId = NewBatchId();
        ClusterFixture.Tracker.SetOutput(batchId, new BatchMetaOutput(ItemCount: 1, WorkerCount: 1));
        ClusterFixture.Tracker.SetStatus(ItemKey(batchId, 0), JobStatus.Pending);
        ClusterFixture.Monitor.ConfigureCancel(ItemKey(batchId, 0), result: false);

        Assert.False(await Grain(WorkerKey(batchId, 0)).CancelAsync());
        Assert.Equal(JobStatus.Pending, ClusterFixture.Tracker.GetStatus(ItemKey(batchId, 0)));
    }

    [Fact]
    public async Task CancelAsync_WhenItemAlreadyCompleted_ReturnsFalse()
    {
        var batchId = NewBatchId();
        ClusterFixture.Tracker.SetOutput(batchId, new BatchMetaOutput(ItemCount: 1, WorkerCount: 1));
        ClusterFixture.Tracker.SetStatus(ItemKey(batchId, 0), JobStatus.Completed);

        Assert.False(await Grain(WorkerKey(batchId, 0)).CancelAsync());
    }
}
