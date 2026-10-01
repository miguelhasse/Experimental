#pragma warning disable ORLEANSEXP005
using Orleans.Journaling;

namespace Orleans.Tests;

/// <summary>
/// The rules that decide whether data coming back from a pool worker changes a job. Plain unit tests: the store works on
/// in-memory stand-ins for the durable collections and counts how often it would have written the journal.
/// </summary>
public sealed class DurableJobStoreTests
{
    private sealed class FakeDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue>
        where TKey : notnull;

    private sealed class FakeValue<T> : IDurableValue<T>
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public T Value { get; set; } = default!;
    }

    private sealed class Rig
    {
        public readonly FakeDictionary<string, DurableJobRecord> Jobs = new();
        public readonly FakeValue<int> Finished = new();
        public int Writes;
        public readonly DurableJobStore Store;

        public Rig() =>
            Store = new DurableJobStore(Jobs, Finished, _ => { Writes++; return ValueTask.CompletedTask; });

        public DurableJobRecord Job(string id = "j1") => Jobs[id];

        /// <summary>A job that is running (epoch 1) and owned by <paramref name="owner"/>.</summary>
        public Rig WithRunning(string id = "j1", long epoch = 1, string? owner = "silo-1")
        {
            Jobs[id] = new DurableJobRecord(id, JobStatus.Processing, 0, null, null, null, DateTimeOffset.UtcNow,
                Request: new JobRequest("p"), OwnerSilo: owner, Epoch: epoch, SubmissionKey: "key");
            return this;
        }
    }

    [Fact]
    public async Task ApplyResult_PartialResultsThenFinal_AreAppliedAndCountedOnce()
    {
        var rig = new Rig().WithRunning();

        Assert.True(await rig.Store.ApplyResultAsync("j1", 1, 1, JobStatus.Processing, "p1", null));
        Assert.True(await rig.Store.ApplyResultAsync("j1", 1, 2, JobStatus.Processing, "p2", null));
        Assert.Equal((JobStatus.Processing, "p2", 0), (rig.Job().Status, rig.Job().Output, rig.Finished.Value));

        Assert.True(await rig.Store.ApplyResultAsync("j1", 1, 3, JobStatus.Completed, "done", null));

        var job = rig.Job();
        Assert.Equal((JobStatus.Completed, "done", 3, 100), (job.Status, job.Output, job.ResultsReceived, job.PercentComplete));
        Assert.Equal(1, rig.Finished.Value);
        Assert.Equal(3, rig.Writes);
    }

    [Fact]
    public async Task ApplyResult_AfterTheFinalResult_NothingElseIsApplied()
    {
        var rig = new Rig().WithRunning();
        await rig.Store.ApplyResultAsync("j1", 1, 3, JobStatus.Completed, "done", null);
        var writes = rig.Writes;

        Assert.False(await rig.Store.ApplyResultAsync("j1", 1, 3, JobStatus.Completed, "done", null));         // exact duplicate
        Assert.False(await rig.Store.ApplyResultAsync("j1", 1, 5, JobStatus.Failed, null, "late failure"));    // another final result
        Assert.False(await rig.Store.ApplyResultAsync("j1", 1, 6, JobStatus.Processing, "late partial", null));
        Assert.False(rig.Store.ApplyProgress("j1", 1, 7, 10, "late progress"));

        var job = rig.Job();
        Assert.Equal((JobStatus.Completed, "done", (string?)null, 3L, 100), (job.Status, job.Output, job.Error, job.LastSequence, job.PercentComplete));
        Assert.Equal(1, rig.Finished.Value);
        Assert.Equal(writes, rig.Writes);   // ignored data costs no journal write
    }

    [Fact]
    public async Task ApplyResult_WithAnOlderOrRepeatedSequence_IsIgnored()
    {
        var rig = new Rig().WithRunning();

        Assert.True(await rig.Store.ApplyResultAsync("j1", 1, 2, JobStatus.Processing, "second", null));
        Assert.False(await rig.Store.ApplyResultAsync("j1", 1, 1, JobStatus.Processing, "first", null));    // arrives late
        Assert.False(await rig.Store.ApplyResultAsync("j1", 1, 2, JobStatus.Processing, "again", null));    // delivered twice

        Assert.Equal(("second", 1), (rig.Job().Output, rig.Job().ResultsReceived));
    }

    [Fact]
    public async Task ProgressAndResults_HaveSeparateSequenceSpaces()
    {
        var rig = new Rig().WithRunning();

        // Progress numbered 3 is applied first, then a partial result numbered 2: the result must not be dropped.
        Assert.True(rig.Store.ApplyProgress("j1", 1, 3, 40, "stage 2"));
        Assert.True(await rig.Store.ApplyResultAsync("j1", 1, 2, JobStatus.Processing, "partial-1", null));
        Assert.Equal(("partial-1", 40, 3L, 2L), (rig.Job().Output, rig.Job().PercentComplete, rig.Job().LastProgressSequence, rig.Job().LastSequence));

        // Within the progress space the order is still enforced.
        Assert.False(rig.Store.ApplyProgress("j1", 1, 2, 10, "older progress"));
        Assert.Equal(40, rig.Job().PercentComplete);
    }

    [Fact]
    public async Task ApplyResult_FromAnotherEpoch_IsIgnored()
    {
        var rig = new Rig().WithRunning(epoch: 2);   // the run that replaced epoch 1

        Assert.False(await rig.Store.ApplyResultAsync("j1", 1, 9, JobStatus.Completed, "from the lost run", null));
        Assert.False(rig.Store.ApplyProgress("j1", 1, 9, 99, "from the lost run"));
        Assert.False(await rig.Store.ApplyResultAsync("j1", 3, 1, JobStatus.Completed, "from a run that does not exist yet", null));

        Assert.Equal((JobStatus.Processing, 0, 0), (rig.Job().Status, rig.Job().ResultsReceived, rig.Finished.Value));
        Assert.Equal(0, rig.Writes);
    }

    [Fact]
    public async Task ApplyResult_ForAnUnknownJob_IsIgnored()
    {
        var rig = new Rig();

        Assert.False(await rig.Store.ApplyResultAsync("ghost", 1, 1, JobStatus.Completed, "x", null));
        Assert.False(rig.Store.ApplyProgress("ghost", 1, 1, 50, "x"));

        Assert.Empty(rig.Jobs);
        Assert.Equal(0, rig.Finished.Value);
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Unknown)]
    public async Task ApplyResult_WithAStatusThatIsNeitherPartialNorFinal_ThrowsAndIsNotCounted(JobStatus status)
    {
        var rig = new Rig().WithRunning();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Store.ApplyResultAsync("j1", 1, 1, status, "x", null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Store.ApplyResultAsync("j1", 1, 2, status, "y", null));

        Assert.Equal((JobStatus.Processing, 0, 0), (rig.Job().Status, rig.Job().ResultsReceived, rig.Finished.Value));
    }

    [Fact]
    public async Task FailRun_FailsTheCurrentRunOnce_AndReleasesTheSubmissionKey()
    {
        var rig = new Rig().WithRunning();

        Assert.True(await rig.Store.FailRunAsync("j1", 1, "Could not start: pool stopped"));
        Assert.False(await rig.Store.FailRunAsync("j1", 1, "again"));   // already finished

        var job = rig.Job();
        Assert.Equal((JobStatus.Failed, "Could not start: pool stopped", (string?)null), (job.Status, job.Error, job.SubmissionKey));
        Assert.Equal(1, rig.Finished.Value);
    }

    [Fact]
    public async Task FailRun_LeavesARunThatWasReplacedAlone()
    {
        var rig = new Rig().WithRunning(epoch: 2);

        Assert.False(await rig.Store.FailRunAsync("j1", 1, "old run"));

        Assert.Equal((JobStatus.Processing, "key", 0), (rig.Job().Status, rig.Job().SubmissionKey, rig.Finished.Value));
    }

    [Fact]
    public async Task ReleaseRuns_RemovesTheOwnerOfRunsInFlight_AndSkipsTheRest()
    {
        var rig = new Rig().WithRunning("running").WithRunning("replaced", epoch: 2).WithRunning("done");
        await rig.Store.ApplyResultAsync("done", 1, 1, JobStatus.Completed, "x", null);
        var writes = rig.Writes;

        var changed = await rig.Store.ReleaseRunsAsync([("running", 1), ("replaced", 1), ("done", 1), ("unknown", 1)]);

        Assert.True(changed);
        Assert.Null(rig.Job("running").OwnerSilo);                      // nobody owns it any more: recovery restarts it
        Assert.Equal(JobStatus.Processing, rig.Job("running").Status);  // still unfinished
        Assert.Equal("silo-1", rig.Job("replaced").OwnerSilo);          // a newer run: not released
        Assert.Equal("silo-1", rig.Job("done").OwnerSilo);              // finished: not released
        Assert.Equal(writes + 1, rig.Writes);
    }

    [Fact]
    public async Task ReleaseRuns_WithNothingToRelease_DoesNotWrite()
    {
        var rig = new Rig().WithRunning(epoch: 2);

        Assert.False(await rig.Store.ReleaseRunsAsync([("j1", 1)]));

        Assert.Equal(0, rig.Writes);
    }

    [Fact]
    public async Task ReadAsync_WaitsForAWriteThatIsInFlight()
    {
        var writeStarted = new TaskCompletionSource();
        var finishWrite = new TaskCompletionSource();
        var jobs = new FakeDictionary<string, DurableJobRecord>();
        var store = new DurableJobStore(jobs, new FakeValue<int>(), async _ =>
        {
            writeStarted.SetResult();
            await finishWrite.Task;
        });

        var update = store.UpdateAsync(() => jobs["j1"] = new DurableJobRecord("j1", JobStatus.Completed, 100, null, "done", null, DateTimeOffset.UtcNow));
        await writeStarted.Task;

        var read = store.ReadAsync(() => jobs["j1"].Output);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(read.IsCompleted);   // state that is not persisted yet is not shown to anyone

        finishWrite.SetResult();
        await update;
        Assert.Equal("done", await read);
    }
}
