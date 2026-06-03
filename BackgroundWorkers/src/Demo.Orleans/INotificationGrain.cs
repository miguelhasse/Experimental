namespace OrleansSample;

/// <summary>
/// Grain that sends a notification via three independent channels — Email, SMS, and Push.
/// Each channel method is idempotent: calling it when the channel is already
/// <see cref="JobStatus.Pending"/> or <see cref="JobStatus.Processing"/> is a no-op
/// and returns the current status without re-enqueuing.
/// </summary>
/// <remarks>
/// The grain key is the notification ID (string).
/// Job IDs in <see cref="IJobTracker"/> follow the pattern <c>{notificationId}:{channel}</c>.
/// </remarks>
[Alias("Grains.NotificationGrain")]
public interface INotificationGrain : IGrainWithStringKey
{
    /// <summary>
    /// Dispatches an email notification for this grain's notification ID.
    /// </summary>
    /// <returns>
    /// <see cref="JobStatus.Pending"/> if newly enqueued;
    /// the current channel status if the call was a no-op.
    /// </returns>
    [Alias("SendEmailAsync")]
    Task<JobStatus> SendEmailAsync(string recipient, string subject);

    /// <summary>
    /// Dispatches an SMS notification for this grain's notification ID.
    /// </summary>
    /// <returns>
    /// <see cref="JobStatus.Pending"/> if newly enqueued;
    /// the current channel status if the call was a no-op.
    /// </returns>
    [Alias("SendSmsAsync")]
    Task<JobStatus> SendSmsAsync(string phoneNumber, string body);

    /// <summary>
    /// Dispatches a push notification for this grain's notification ID.
    /// </summary>
    /// <returns>
    /// <see cref="JobStatus.Pending"/> if newly enqueued;
    /// the current channel status if the call was a no-op.
    /// </returns>
    [Alias("SendPushAsync")]
    Task<JobStatus> SendPushAsync(string deviceToken, string title);

    /// <summary>Returns a point-in-time status snapshot for all three channels.</summary>
    [Alias("GetAllStatusAsync")]
    Task<NotificationSnapshot> GetAllStatusAsync();

    /// <summary>
    /// Attempts to cancel all queued (not yet dispatched) channel jobs for this notification.
    /// </summary>
    /// <returns>
    /// <c>true</c> if at least one queued channel was cancelled;
    /// <c>false</c> if no cancellable channels were found.
    /// </returns>
    [Alias("CancelAsync")]
    Task<bool> CancelAsync();
}
