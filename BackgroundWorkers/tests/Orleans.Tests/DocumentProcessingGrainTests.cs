namespace Orleans.Tests;

[Collection("ClusterCollection")]
public sealed class DocumentProcessingGrainTests(ClusterFixture fixture)
{
    private IDocumentProcessingGrain Grain(string id) =>
        fixture.Cluster.GrainFactory.GetGrain<IDocumentProcessingGrain>(id);

    private static string NewId() => $"pipe-{Guid.NewGuid():N}";

    // ── GetSummaryAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_WhenNoHistory_ReturnsAllUnknown()
    {
        var summary = await Grain(NewId()).GetSummaryAsync();

        Assert.Equal(JobStatus.Unknown, summary.Step1Status);
        Assert.Equal(JobStatus.Unknown, summary.Step2Status);
        Assert.Equal(JobStatus.Unknown, summary.Step3Status);
    }

    // ── RunAsync idempotency guards ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenStep1Processing_IsNoOp()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:step1", JobStatus.Processing);
        ClusterFixture.Pool.Hold(); // must not be called

        var result = await grain.RunAsync();

        // Returns the step1 status (Processing), no new dispatch.
        Assert.Equal(JobStatus.Processing, result);
        // Other steps are untouched.
        Assert.Equal(JobStatus.Unknown, ClusterFixture.Tracker.GetStatus($"{id}:step2"));
    }

    [Fact]
    public async Task RunAsync_WhenStep2Processing_IsNoOp()
    {
        var id = NewId();
        var grain = Grain(id);

        // Any step in Processing prevents restart (not just step 1).
        ClusterFixture.Tracker.SetStatus($"{id}:step2", JobStatus.Processing);
        ClusterFixture.Pool.Hold();

        // RunAsync returns the step1 status when it bails out.
        await grain.RunAsync();

        // Step2 should still be Processing (not reset).
        Assert.Equal(JobStatus.Processing, ClusterFixture.Tracker.GetStatus($"{id}:step2"));
    }

    [Fact]
    public async Task RunAsync_WhenStep3Processing_IsNoOp()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:step3", JobStatus.Processing);
        ClusterFixture.Pool.Hold();

        await grain.RunAsync();

        Assert.Equal(JobStatus.Processing, ClusterFixture.Tracker.GetStatus($"{id}:step3"));
    }

    // ── RunAsync restart / reset ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenPreviouslyCompleted_ResetsAllStepsAndRestarts()
    {
        var id = NewId();
        var grain = Grain(id);

        // Pre-seed a "completed" state.
        ClusterFixture.Tracker.SetStatus($"{id}:step1", JobStatus.Completed);
        ClusterFixture.Tracker.SetStatus($"{id}:step2", JobStatus.Completed);
        ClusterFixture.Tracker.SetStatus($"{id}:step3", JobStatus.Completed);
        ClusterFixture.Tracker.SetOutput($"{id}:step1", new ExtractedContentOutput("old", 1));

        // Hold so the new step1 callback never fires — we just want to see the reset.
        ClusterFixture.Pool.Hold();

        await grain.RunAsync();

        // All steps should be reset.
        Assert.Equal(JobStatus.Unknown, ClusterFixture.Tracker.GetStatus($"{id}:step2"));
        Assert.Equal(JobStatus.Unknown, ClusterFixture.Tracker.GetStatus($"{id}:step3"));
        Assert.Null(ClusterFixture.Tracker.GetOutput($"{id}:step1"));
        // Step1 itself has been set to Processing by the new dispatch.
        Assert.Equal(JobStatus.Processing, ClusterFixture.Tracker.GetStatus($"{id}:step1"));
    }

    [Fact]
    public async Task RunAsync_WhenPreviouslyFailed_ResetsAndRestarts()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:step1", JobStatus.Failed);
        ClusterFixture.Pool.Hold();

        var result = await grain.RunAsync();

        Assert.Equal(JobStatus.Processing, result);
        Assert.Equal(JobStatus.Processing, ClusterFixture.Tracker.GetStatus($"{id}:step1"));
    }

    // ── Full pipeline ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FullPipeline_ExtractTransformIndex_CompletesAllThreeSteps()
    {
        var id = NewId();
        var grain = Grain(id);

        // Route each step to the correct typed output based on request ID suffix.
        ClusterFixture.Pool.UseResult(ctx =>
        {
            if (ctx.RequestId.EndsWith(":step1"))
                return new RequestResult(ctx.RequestId, Success: true, Output: null,
                    TypedOutput: new ExtractedContentOutput("raw text", 200));
            if (ctx.RequestId.EndsWith(":step2"))
                return new RequestResult(ctx.RequestId, Success: true, Output: null,
                    TypedOutput: new TransformedContentOutput(new List<string> { "keyword" }, 0.9));
            // step3
            return new RequestResult(ctx.RequestId, Success: true, Output: null,
                TypedOutput: new IndexedContentOutput("idx-001", 3));
        });

        await grain.RunAsync();

        // The pipeline chains asynchronously via [OneWay] observer calls; poll until done.
        await GrainTestExtensions.WaitForConditionAsync(async () =>
        {
            var s = await grain.GetSummaryAsync();
            return s.Step3Status == JobStatus.Completed;
        });

        var summary = await grain.GetSummaryAsync();
        Assert.Equal(JobStatus.Completed, summary.Step1Status);
        Assert.Equal(JobStatus.Completed, summary.Step2Status);
        Assert.Equal(JobStatus.Completed, summary.Step3Status);

        // Verify typed outputs were persisted.
        Assert.IsType<ExtractedContentOutput>(summary.Step1Output);
        Assert.IsType<TransformedContentOutput>(summary.Step2Output);
        Assert.IsType<IndexedContentOutput>(summary.Step3Output);
    }

    [Fact]
    public async Task Pipeline_WhenStep1Fails_SetsStep1FailedAndStopsChain()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Pool.UseError(new InvalidOperationException("extract failed"));

        await grain.RunAsync();

        // Step1 closure writes Failed synchronously; step2/3 remain Unknown.
        Assert.Equal(JobStatus.Failed, ClusterFixture.Tracker.GetStatus($"{id}:step1"));
        Assert.Equal(JobStatus.Unknown, ClusterFixture.Tracker.GetStatus($"{id}:step2"));
        Assert.Equal(JobStatus.Unknown, ClusterFixture.Tracker.GetStatus($"{id}:step3"));
    }

    [Fact]
    public async Task Pipeline_WhenStep1Cancelled_SetsStep1CancelledAndStopsChain()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Pool.UseCancel();

        await grain.RunAsync();

        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus($"{id}:step1"));
        Assert.Equal(JobStatus.Unknown, ClusterFixture.Tracker.GetStatus($"{id}:step2"));
    }

    // ── CancelAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_WhenNoStepsActive_ReturnsFalse()
    {
        Assert.False(await Grain(NewId()).CancelAsync());
    }

    [Fact]
    public async Task CancelAsync_WhenStepQueued_AndMonitorSucceeds_ReturnsTrueAndSetsCancelled()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:step1", JobStatus.Processing);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:step1", result: true);

        var cancelled = await grain.CancelAsync();

        Assert.True(cancelled);
        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus($"{id}:step1"));
    }

    [Fact]
    public async Task CancelAsync_WhenAllStepsQueued_AndMonitorSucceeds_ReturnsTrueAndCancelsAll()
    {
        var id = NewId();
        var grain = Grain(id);

        foreach (var step in new[] { "step1", "step2", "step3" })
        {
            ClusterFixture.Tracker.SetStatus($"{id}:{step}", JobStatus.Processing);
            ClusterFixture.Monitor.ConfigureCancel($"{id}:{step}", result: true);
        }

        Assert.True(await grain.CancelAsync());

        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus($"{id}:step1"));
        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus($"{id}:step2"));
        Assert.Equal(JobStatus.Cancelled, ClusterFixture.Tracker.GetStatus($"{id}:step3"));
    }

    [Fact]
    public async Task CancelAsync_WhenMonitorFails_ReturnsFalseAndStatusUnchanged()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:step1", JobStatus.Processing);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:step1", result: false);

        Assert.False(await grain.CancelAsync());
        Assert.Equal(JobStatus.Processing, ClusterFixture.Tracker.GetStatus($"{id}:step1"));
    }

}
