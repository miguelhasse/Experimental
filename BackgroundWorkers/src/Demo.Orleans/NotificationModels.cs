namespace OrleansSample;

/// <summary>
/// Request to send a notification via the email channel.
/// </summary>
[GenerateSerializer]
public record EmailNotificationRequest(
    [property: Id(0)] string NotificationId,
    [property: Id(1)] string Recipient,
    [property: Id(2)] string Subject,
    [property: Id(3)] RequestPriority Priority = RequestPriority.Normal) : IJobRequest;

/// <summary>
/// Request to send a notification via the SMS channel.
/// </summary>
[GenerateSerializer]
public record SmsNotificationRequest(
    [property: Id(0)] string NotificationId,
    [property: Id(1)] string PhoneNumber,
    [property: Id(2)] string Body,
    [property: Id(3)] RequestPriority Priority = RequestPriority.Normal) : IJobRequest;

/// <summary>
/// Request to send a notification via the push channel.
/// </summary>
[GenerateSerializer]
public record PushNotificationRequest(
    [property: Id(0)] string NotificationId,
    [property: Id(1)] string DeviceToken,
    [property: Id(2)] string Title,
    [property: Id(3)] RequestPriority Priority = RequestPriority.Normal) : IJobRequest;

/// <summary>
/// Point-in-time status and progress snapshot for all three notification channels
/// of a single <see cref="INotificationGrain"/> instance.
/// Progress is non-null only when a channel is in <see cref="JobStatus.Processing"/> state.
/// </summary>
[GenerateSerializer]
public record NotificationSnapshot(
    [property: Id(0)] JobStatus EmailStatus,
    [property: Id(1)] JobStatus SmsStatus,
    [property: Id(2)] JobStatus PushStatus,
    [property: Id(3)] JobProgressSnapshot? EmailProgress = null,
    [property: Id(4)] JobProgressSnapshot? SmsProgress = null,
    [property: Id(5)] JobProgressSnapshot? PushProgress = null);
