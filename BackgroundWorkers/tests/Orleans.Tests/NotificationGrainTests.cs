namespace Orleans.Tests;

[Collection("ClusterCollection")]
public sealed class NotificationGrainTests(ClusterFixture fixture)
{
    private INotificationGrain Grain(string id) =>
        fixture.Cluster.GrainFactory.GetGrain<INotificationGrain>(id);

    private static string NewId() => $"notif-{Guid.NewGuid():N}";

    // ── GetAllStatusAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllStatusAsync_WhenNoHistory_ReturnsAllUnknown()
    {
        var snapshot = await Grain(NewId()).GetAllStatusAsync();

        Assert.Equal(JobStatus.Unknown, snapshot.EmailStatus);
        Assert.Equal(JobStatus.Unknown, snapshot.SmsStatus);
        Assert.Equal(JobStatus.Unknown, snapshot.PushStatus);
    }

    // ── SendEmailAsync idempotency ────────────────────────────────────────────

    [Fact]
    public async Task SendEmailAsync_WhenUnknown_DispatchesAndReturnsProcessing()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess();

        Assert.Equal(JobStatus.Processing, await grain.SendEmailAsync("a@b.com", "Hi"));
    }

    [Fact]
    public async Task SendEmailAsync_WhenAlreadyProcessing_IsNoOp()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:email", JobStatus.Processing);
        ClusterFixture.Pool.Hold(); // must not be called

        Assert.Equal(JobStatus.Processing, await grain.SendEmailAsync("a@b.com", "Hi"));
    }

    [Fact]
    public async Task SendEmailAsync_WhenAlreadyCompleted_IsNoOp()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:email", JobStatus.Completed);
        ClusterFixture.Pool.Hold(); // must not be called

        Assert.Equal(JobStatus.Completed, await grain.SendEmailAsync("a@b.com", "Hi"));
    }

    [Fact]
    public async Task SendEmailAsync_WhenFailed_Retries()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:email", JobStatus.Failed);
        ClusterFixture.Pool.UseSuccess();

        Assert.Equal(JobStatus.Processing, await grain.SendEmailAsync("a@b.com", "Hi"));
    }

    // ── SendSmsAsync / SendPushAsync dispatch ─────────────────────────────────

    [Fact]
    public async Task SendSmsAsync_WhenUnknown_DispatchesAndReturnsProcessing()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess();

        Assert.Equal(JobStatus.Processing, await grain.SendSmsAsync("+1234", "body"));
    }

    [Fact]
    public async Task SendPushAsync_WhenUnknown_DispatchesAndReturnsProcessing()
    {
        var grain = Grain(NewId());
        ClusterFixture.Pool.UseSuccess();

        Assert.Equal(JobStatus.Processing, await grain.SendPushAsync("device-token", "title"));
    }

    [Fact]
    public async Task SendSmsAsync_WhenCancelled_Retries()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:sms", JobStatus.Cancelled);
        ClusterFixture.Pool.UseSuccess();

        Assert.Equal(JobStatus.Processing, await grain.SendSmsAsync("+1", "body"));
    }

    // ── Full lifecycle ────────────────────────────────────────────────────────

    [Fact]
    public async Task AllChannels_CompleteIndependently()
    {
        var id = NewId();
        var grain = Grain(id);

        // The pool callback closure commits Completed synchronously.
        ClusterFixture.Pool.UseSuccess();

        await grain.SendEmailAsync("a@b.com", "subj");
        await grain.SendSmsAsync("+1", "body");
        await grain.SendPushAsync("dev", "title");

        var snapshot = await grain.GetAllStatusAsync();
        Assert.Equal(JobStatus.Completed, snapshot.EmailStatus);
        Assert.Equal(JobStatus.Completed, snapshot.SmsStatus);
        Assert.Equal(JobStatus.Completed, snapshot.PushStatus);
    }

    [Fact]
    public async Task SendEmailAsync_WhenError_SetsFailedInTracker()
    {
        var id = NewId();
        var grain = Grain(id);
        ClusterFixture.Pool.UseError(new InvalidOperationException("email fail"));

        await grain.SendEmailAsync("a@b.com", "Hi");

        Assert.Equal(JobStatus.Failed, (await grain.GetAllStatusAsync()).EmailStatus);
    }

    [Fact]
    public async Task ChannelsAreTrackedIndependently_OneFailDoesNotAffectOthers()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Pool.UseError(new InvalidOperationException("fail"));
        await grain.SendEmailAsync("a@b.com", "Hi");

        ClusterFixture.Pool.UseSuccess();
        await grain.SendSmsAsync("+1", "body");
        await grain.SendPushAsync("dev", "title");

        var snapshot = await grain.GetAllStatusAsync();
        Assert.Equal(JobStatus.Failed, snapshot.EmailStatus);
        Assert.Equal(JobStatus.Completed, snapshot.SmsStatus);
        Assert.Equal(JobStatus.Completed, snapshot.PushStatus);
    }

    // ── CancelAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_WhenNoChannelsActive_ReturnsFalse()
    {
        Assert.False(await Grain(NewId()).CancelAsync());
    }

    [Fact]
    public async Task CancelAsync_WhenOneChannelQueued_AndMonitorSucceeds_ReturnsTrueAndSetsCancelled()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:email", JobStatus.Processing);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:email", result: true);

        Assert.True(await grain.CancelAsync());
        Assert.Equal(JobStatus.Cancelled, (await grain.GetAllStatusAsync()).EmailStatus);
    }

    [Fact]
    public async Task CancelAsync_WhenAllChannelsQueued_AndMonitorSucceeds_ReturnsTrueAndCancelsAll()
    {
        var id = NewId();
        var grain = Grain(id);

        foreach (var channel in new[] { "email", "sms", "push" })
        {
            ClusterFixture.Tracker.SetStatus($"{id}:{channel}", JobStatus.Processing);
            ClusterFixture.Monitor.ConfigureCancel($"{id}:{channel}", result: true);
        }

        Assert.True(await grain.CancelAsync());

        var snapshot = await grain.GetAllStatusAsync();
        Assert.Equal(JobStatus.Cancelled, snapshot.EmailStatus);
        Assert.Equal(JobStatus.Cancelled, snapshot.SmsStatus);
        Assert.Equal(JobStatus.Cancelled, snapshot.PushStatus);
    }

    [Fact]
    public async Task CancelAsync_WhenMonitorFails_ReturnsFalseAndStatusUnchanged()
    {
        var id = NewId();
        var grain = Grain(id);

        ClusterFixture.Tracker.SetStatus($"{id}:sms", JobStatus.Processing);
        ClusterFixture.Monitor.ConfigureCancel($"{id}:sms", result: false);

        Assert.False(await grain.CancelAsync());
        Assert.Equal(JobStatus.Processing, (await grain.GetAllStatusAsync()).SmsStatus);
    }
}
