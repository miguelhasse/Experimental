namespace Orleans.Tests;

/// <summary>
/// Pure unit tests for <see cref="InMemoryJobTracker"/> — no Orleans cluster required.
/// </summary>
public sealed class InMemoryJobTrackerTests
{
    private readonly InMemoryJobTracker _tracker = new();

    // ── Status ────────────────────────────────────────────────────────────────

    [Fact]
    public void GetStatus_WhenNoEntry_ReturnsUnknown()
    {
        Assert.Equal(JobStatus.Unknown, _tracker.GetStatus("nonexistent"));
    }

    [Fact]
    public void SetStatus_ThenGetStatus_ReturnsSetValue()
    {
        _tracker.SetStatus("j1", JobStatus.Processing);
        Assert.Equal(JobStatus.Processing, _tracker.GetStatus("j1"));
    }

    [Fact]
    public void SetStatus_CanOverwritePreviousValue()
    {
        _tracker.SetStatus("j2", JobStatus.Processing);
        _tracker.SetStatus("j2", JobStatus.Completed);
        Assert.Equal(JobStatus.Completed, _tracker.GetStatus("j2"));
    }

    [Fact]
    public void MultipleJobIds_TrackIndependently()
    {
        _tracker.SetStatus("a", JobStatus.Processing);
        _tracker.SetStatus("b", JobStatus.Completed);

        Assert.Equal(JobStatus.Processing, _tracker.GetStatus("a"));
        Assert.Equal(JobStatus.Completed, _tracker.GetStatus("b"));
        Assert.Equal(JobStatus.Unknown, _tracker.GetStatus("c")); // never set
    }

    // ── Snapshot ──────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_ReturnsAllEntries()
    {
        _tracker.SetStatus("snap-a", JobStatus.Completed);
        _tracker.SetStatus("snap-b", JobStatus.Failed);

        var snapshot = _tracker.Snapshot();

        Assert.Equal(JobStatus.Completed, snapshot["snap-a"]);
        Assert.Equal(JobStatus.Failed, snapshot["snap-b"]);
    }

    // ── Progress ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetProgress_WhenNoEntry_ReturnsNull()
    {
        Assert.Null(_tracker.GetProgress("missing"));
    }

    [Fact]
    public void SetProgress_ThenGetProgress_ReturnsSnapshot()
    {
        _tracker.SetProgress("p1", 75, "three-quarters");

        var snap = _tracker.GetProgress("p1");

        Assert.NotNull(snap);
        Assert.Equal(75, snap.PercentComplete);
        Assert.Equal("three-quarters", snap.Message);
    }

    [Fact]
    public void SetProgress_OverwritesPreviousEntry()
    {
        _tracker.SetProgress("p2", 50, "halfway");
        _tracker.SetProgress("p2", 100, "done");

        var snap = _tracker.GetProgress("p2");
        Assert.Equal(100, snap!.PercentComplete);
    }

    [Fact]
    public void ClearProgress_RemovesSnapshot()
    {
        _tracker.SetProgress("p3", 50, "halfway");
        _tracker.ClearProgress("p3");

        Assert.Null(_tracker.GetProgress("p3"));
    }

    [Fact]
    public void ClearProgress_OnMissingKey_IsNoOp()
    {
        // Must not throw.
        _tracker.ClearProgress("never-set");
    }

    // ── Output ────────────────────────────────────────────────────────────────

    [Fact]
    public void GetOutput_WhenNoEntry_ReturnsNull()
    {
        Assert.Null(_tracker.GetOutput("missing"));
    }

    [Fact]
    public void SetOutput_ThenGetOutput_ReturnsSameInstance()
    {
        var output = new TextJobOutput("result text");
        _tracker.SetOutput("o1", output);

        Assert.Same(output, _tracker.GetOutput("o1"));
    }

    [Fact]
    public void SetOutput_WithNull_RemovesEntry()
    {
        _tracker.SetOutput("o2", new TextJobOutput("old"));
        _tracker.SetOutput("o2", null);

        Assert.Null(_tracker.GetOutput("o2"));
    }

    [Fact]
    public void SetOutput_CanStoreConcreteSubtype()
    {
        var output = new ExtractedContentOutput("raw text", 150);
        _tracker.SetOutput("o3", output);

        var retrieved = _tracker.GetOutput("o3");
        Assert.IsType<ExtractedContentOutput>(retrieved);
        Assert.Equal("raw text", ((ExtractedContentOutput)retrieved!).RawText);
    }
}
