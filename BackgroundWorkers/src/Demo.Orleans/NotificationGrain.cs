namespace OrleansSample;

/// <summary>
/// Grain that sends a notification via three independent channels — Email, SMS, and Push.
///
/// <para>
/// Each channel method is <em>idempotent</em>: if the channel is already
/// <see cref="JobStatus.Pending"/>, <see cref="JobStatus.Processing"/>, or
/// <see cref="JobStatus.Completed"/>, the call is a no-op and returns the current
/// status without re-enqueuing. Channels in <see cref="JobStatus.Failed"/> or
/// <see cref="JobStatus.Cancelled"/> state are retried on the next call.
/// </para>
///
/// <para>
/// Job IDs in <see cref="IJobTracker"/> follow the pattern <c>{notificationId}:{channel}</c>
/// where <c>channel</c> is <c>"email"</c>, <c>"sms"</c>, or <c>"push"</c>.
/// </para>
///
/// Flow per channel:
/// <list type="number">
///   <item>Client calls <c>Send*Async()</c> → grain checks idempotency guard in tracker.</item>
///   <item>If not already queued: enqueues the typed request, captures
///         <c>this.AsReference&lt;IJobCompletionObserver&gt;()</c> and
///         <c>this.AsReference&lt;IJobProgressObserver&gt;()</c> in the callback closure,
///         sets status to <see cref="JobStatus.Processing"/>, then calls
///         <see cref="Grain.DeactivateOnIdle"/>.</item>
///   <item>The pool worker completes the channel; the closure commits channel-specific
///         tracker state and logging, then fires the observer via Orleans messaging.</item>
///   <item>Orleans re-activates the grain; the observer turn handles cross-cutting concerns.</item>
/// </list>
/// </summary>
public sealed partial class NotificationGrain(
    IRequestPool pool,
    IRequestPoolMonitor monitor,
    IJobTracker tracker,
    ILogger<NotificationGrain> logger) : Grain, INotificationGrain, IJobCompletionObserver, IJobProgressObserver
{
    private const string EmailChannel = "email";
    private const string SmsChannel = "sms";
    private const string PushChannel = "push";

    // -------------------------------------------------------------------------
    // INotificationGrain
    // -------------------------------------------------------------------------

    public Task<JobStatus> SendEmailAsync(string recipient, string subject)
    {
        var notificationId = this.GetPrimaryKeyString();
        return DispatchChannelAsync(
            EmailChannel,
            channelJobId => new RequestContext<EmailNotificationRequest>(
                channelJobId,
                new EmailNotificationRequest(notificationId, recipient, subject)),
            notificationId);
    }

    public Task<JobStatus> SendSmsAsync(string phoneNumber, string body)
    {
        var notificationId = this.GetPrimaryKeyString();
        return DispatchChannelAsync(
            SmsChannel,
            channelJobId => new RequestContext<SmsNotificationRequest>(
                channelJobId,
                new SmsNotificationRequest(notificationId, phoneNumber, body)),
            notificationId);
    }

    public Task<JobStatus> SendPushAsync(string deviceToken, string title)
    {
        var notificationId = this.GetPrimaryKeyString();
        return DispatchChannelAsync(
            PushChannel,
            channelJobId => new RequestContext<PushNotificationRequest>(
                channelJobId,
                new PushNotificationRequest(notificationId, deviceToken, title)),
            notificationId);
    }

    public Task<NotificationSnapshot> GetAllStatusAsync()
    {
        var notificationId = this.GetPrimaryKeyString();
        var emailId = $"{notificationId}:{EmailChannel}";
        var smsId = $"{notificationId}:{SmsChannel}";
        var pushId = $"{notificationId}:{PushChannel}";

        return Task.FromResult(new NotificationSnapshot(
            EmailStatus: tracker.GetStatus(emailId),
            SmsStatus: tracker.GetStatus(smsId),
            PushStatus: tracker.GetStatus(pushId),
            EmailProgress: tracker.GetProgress(emailId),
            SmsProgress: tracker.GetProgress(smsId),
            PushProgress: tracker.GetProgress(pushId)));
    }

    public Task<bool> CancelAsync()
    {
        var notificationId = this.GetPrimaryKeyString();
        var cancelled = 0;

        foreach (var channel in new[] { EmailChannel, SmsChannel, PushChannel })
        {
            var channelJobId = $"{notificationId}:{channel}";
            if (monitor.TryCancelRequest(channelJobId))
            {
                tracker.SetStatus(channelJobId, JobStatus.Cancelled);
                LogChannelCancelledWhileQueued(notificationId, channel);
                cancelled++;
            }
        }

        LogCancelAllChannels(notificationId, cancelled);
        return Task.FromResult(cancelled > 0);
    }

    // -------------------------------------------------------------------------
    // IJobCompletionObserver + IJobProgressObserver — called via Orleans messaging
    // -------------------------------------------------------------------------

    /// <remarks>
    /// Channel-specific tracker writes and logging are committed in the pool callback
    /// closure (thread-safe via <see cref="IJobTracker"/>'s <c>ConcurrentDictionary</c>).
    /// These observer methods handle cross-cutting concerns at the notification level.
    /// </remarks>
    public Task OnProgress(string jobId, JobProgressUpdate progress)
    {
        tracker.SetProgress(jobId, progress.PercentComplete, progress.Message);
        LogChannelProgress(jobId, progress.PercentComplete, progress.Message);
        return Task.CompletedTask;
    }

    public Task OnCompleted(string jobId, JobOutput output)
    {
        LogChannelCompleted(this.GetPrimaryKeyString(), output.ToString());
        return Task.CompletedTask;
    }

    public Task OnCanceled(string jobId)
    {
        LogChannelCancelled(this.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public Task OnFaulted(string jobId, Exception exception)
    {
        LogChannelFailed(this.GetPrimaryKeyString(), exception.Message);
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<JobStatus> DispatchChannelAsync(
        string channel,
        Func<string, RequestContext> buildContext,
        string notificationId)
    {
        var channelJobId = $"{notificationId}:{channel}";
        var current = tracker.GetStatus(channelJobId);

        // Idempotency guard — skip re-submission if already queued, running, or completed successfully.
        // Failed and Cancelled statuses fall through to allow retries.
        if (current is JobStatus.Pending or JobStatus.Processing or JobStatus.Completed)
        {
            LogChannelAlreadyQueued(notificationId, channel, current);
            return current;
        }

        // Clear any stale progress from a previous attempt before re-submitting.
        // Set Processing *before* EnqueueAsync so that if the handler completes
        // extremely fast the [AlwaysInterleave] observer turn cannot fire and
        // then be overwritten back to Processing by this turn.
        tracker.ClearProgress(channelJobId);
        tracker.SetStatus(channelJobId, JobStatus.Processing);

        var completionRef = this.AsReference<IJobCompletionObserver>();
        var progressRef = this.AsReference<IJobProgressObserver>();
        var context = buildContext(channelJobId);

        RequestProgressReporter progressReporter = (pct, msg, delta) =>
            progressRef.OnProgress(channelJobId, new JobProgressUpdate(pct, msg, delta as JobProgressDelta));

        // Build a new context with the progress reporter attached.
        context = context with { OnProgress = progressReporter };

        await pool.EnqueueAsync(context, async result =>
        {
            // Discriminator-aware tracker write in closure (ConcurrentDictionary is thread-safe).
            if (result.Error is OperationCanceledException)
            {
                tracker.SetStatus(channelJobId, JobStatus.Cancelled);
                LogChannelCancelledWithChannel(notificationId, channel);
                await completionRef.OnCanceled(channelJobId);
            }
            else if (result.Error is not null)
            {
                tracker.SetStatus(channelJobId, JobStatus.Failed);
                LogChannelFailedWithChannel(notificationId, channel, result.Error.Message);
                await completionRef.OnFaulted(channelJobId, result.Error);
            }
            else
            {
                tracker.SetStatus(channelJobId, JobStatus.Completed);
                var output = result.TypedOutput as JobOutput ?? new TextJobOutput(result.Output ?? string.Empty);
                LogChannelCompletedWithChannel(notificationId, channel, output.ToString());
                await completionRef.OnCompleted(channelJobId, output);
            }
        });

        LogChannelEnqueued(notificationId, channel);

        DeactivateOnIdle();
        return JobStatus.Processing;
    }

    [LoggerMessage(1, LogLevel.Warning, "Notification {NotificationId} channel '{Channel}' is already {Status}; ignoring re-dispatch")]
    private partial void LogChannelAlreadyQueued(string notificationId, string channel, JobStatus status);

    [LoggerMessage(2, LogLevel.Information, "Notification {NotificationId} channel '{Channel}' enqueued; grain will deactivate")]
    private partial void LogChannelEnqueued(string notificationId, string channel);

    [LoggerMessage(3, LogLevel.Debug, "Notification {JobId} progress: {Percent}% — {Message}")]
    private partial void LogChannelProgress(string jobId, int percent, string? message);

    [LoggerMessage(4, LogLevel.Information, "Notification {NotificationId} channel '{Channel}' completed: {Output}")]
    private partial void LogChannelCompletedWithChannel(string notificationId, string channel, string? output);

    [LoggerMessage(5, LogLevel.Information, "Notification {NotificationId} channel '{Channel}' cancelled")]
    private partial void LogChannelCancelledWithChannel(string notificationId, string channel);

    [LoggerMessage(6, LogLevel.Error, "Notification {NotificationId} channel '{Channel}' failed: {Error}")]
    private partial void LogChannelFailedWithChannel(string notificationId, string channel, string? error);

    [LoggerMessage(7, LogLevel.Information, "Notification {NotificationId}: a channel job completed — {Output}")]
    private partial void LogChannelCompleted(string notificationId, string? output);

    [LoggerMessage(8, LogLevel.Information, "Notification {NotificationId}: a channel job cancelled")]
    private partial void LogChannelCancelled(string notificationId);

    [LoggerMessage(9, LogLevel.Error, "Notification {NotificationId}: a channel job failed — {Error}")]
    private partial void LogChannelFailed(string notificationId, string? error);

    [LoggerMessage(10, LogLevel.Information, "Notification {NotificationId} channel '{Channel}' cancelled while queued")]
    private partial void LogChannelCancelledWhileQueued(string notificationId, string channel);

    [LoggerMessage(11, LogLevel.Information, "Notification {NotificationId}: TryCancelAllChannels cancelled {Count} channel(s)")]
    private partial void LogCancelAllChannels(string notificationId, int count);
}
