namespace OrleansSample;

/// <summary>
/// Handles <see cref="EmailNotificationRequest"/>: simulates composing, validating, and
/// delivering an email via a multi-phase workflow.
/// </summary>
internal sealed partial class EmailNotificationHandler(ILogger<EmailNotificationHandler> logger)
    : IRequestHandler<EmailNotificationRequest>
{
    private static readonly (string Name, int SubSteps, int MinMs, int MaxMs)[] Phases =
    [
        ("Composing message",  2, 20,  60),
        ("Validating address", 2, 15,  40),
        ("Connecting to SMTP", 1, 30,  80),
        ("Delivering",         2, 40, 120),
        ("Confirming receipt", 1, 20,  50),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<EmailNotificationRequest> context,
        CancellationToken cancellationToken)
    {
        var req = context.Data;
        LogDispatchingEmail(req.NotificationId, req.Recipient, req.Subject);

        int totalSteps = Phases.Sum(p => p.SubSteps);
        int step = 0;

        foreach (var (name, subSteps, minMs, maxMs) in Phases)
        {
            for (int s = 1; s <= subSteps; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);
                step++;
                FaultInjector.MaybeThrow(step, context.RequestId);
                int pct = step * 100 / totalSteps;
                var msg = subSteps > 1 ? $"{name} ({s}/{subSteps})" : name;
                context.OnProgress?.Invoke(pct, msg, new JobStepProgressDelta(step, totalSteps));
            }
        }

        var output = $"Email sent to {req.Recipient} for notification {req.NotificationId}";
        LogEmailDelivered(req.NotificationId, req.Recipient);
        return new RequestResult(context.RequestId, Success: true, Output: output, TypedOutput: new TextJobOutput(output));
    }

    [LoggerMessage(1, LogLevel.Information, "[email] Dispatching notification {NotificationId} → {Recipient}: {Subject}")]
    private partial void LogDispatchingEmail(string notificationId, string recipient, string subject);

    [LoggerMessage(2, LogLevel.Information, "[email] Delivered notification {NotificationId} to {Recipient}")]
    private partial void LogEmailDelivered(string notificationId, string recipient);
}

/// <summary>
/// Handles <see cref="SmsNotificationRequest"/>: simulates routing, sending, and confirming
/// an SMS message.
/// </summary>
internal sealed partial class SmsNotificationHandler(ILogger<SmsNotificationHandler> logger)
    : IRequestHandler<SmsNotificationRequest>
{
    private static readonly (string Name, int SubSteps, int MinMs, int MaxMs)[] Phases =
    [
        ("Routing to carrier", 1, 20, 60),
        ("Sending message",    2, 30, 90),
        ("Awaiting delivery",  2, 25, 70),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<SmsNotificationRequest> context,
        CancellationToken cancellationToken)
    {
        var req = context.Data;
        LogDispatchingSms(req.NotificationId, req.PhoneNumber);

        int totalSteps = Phases.Sum(p => p.SubSteps);
        int step = 0;

        foreach (var (name, subSteps, minMs, maxMs) in Phases)
        {
            for (int s = 1; s <= subSteps; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);
                step++;
                FaultInjector.MaybeThrow(step, context.RequestId);
                int pct = step * 100 / totalSteps;
                var msg = subSteps > 1 ? $"{name} ({s}/{subSteps})" : name;
                context.OnProgress?.Invoke(pct, msg, new JobStepProgressDelta(step, totalSteps));
            }
        }

        var output = $"SMS sent to {req.PhoneNumber} for notification {req.NotificationId}";
        LogSmsDelivered(req.NotificationId, req.PhoneNumber);
        return new RequestResult(context.RequestId, Success: true, Output: output, TypedOutput: new TextJobOutput(output));
    }

    [LoggerMessage(1, LogLevel.Information, "[sms] Dispatching notification {NotificationId} → {PhoneNumber}")]
    private partial void LogDispatchingSms(string notificationId, string phoneNumber);

    [LoggerMessage(2, LogLevel.Information, "[sms] Delivered notification {NotificationId} to {PhoneNumber}")]
    private partial void LogSmsDelivered(string notificationId, string phoneNumber);
}

/// <summary>
/// Handles <see cref="PushNotificationRequest"/>: simulates tokenising, dispatching, and
/// acknowledging a push notification.
/// </summary>
internal sealed partial class PushNotificationHandler(ILogger<PushNotificationHandler> logger)
    : IRequestHandler<PushNotificationRequest>
{
    private static readonly (string Name, int SubSteps, int MinMs, int MaxMs)[] Phases =
    [
        ("Validating token",  1, 15, 40),
        ("Dispatching",       2, 25, 80),
        ("Awaiting ack",      1, 20, 60),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<PushNotificationRequest> context,
        CancellationToken cancellationToken)
    {
        var req = context.Data;
        LogDispatchingPush(req.NotificationId, req.DeviceToken, req.Title);

        int totalSteps = Phases.Sum(p => p.SubSteps);
        int step = 0;

        foreach (var (name, subSteps, minMs, maxMs) in Phases)
        {
            for (int s = 1; s <= subSteps; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);
                step++;
                FaultInjector.MaybeThrow(step, context.RequestId);
                int pct = step * 100 / totalSteps;
                var msg = subSteps > 1 ? $"{name} ({s}/{subSteps})" : name;
                context.OnProgress?.Invoke(pct, msg, new JobStepProgressDelta(step, totalSteps));
            }
        }

        var output = $"Push sent to device {req.DeviceToken[..Math.Min(8, req.DeviceToken.Length)]}… for notification {req.NotificationId}";
        LogPushDelivered(req.NotificationId, req.DeviceToken);
        return new RequestResult(context.RequestId, Success: true, Output: output, TypedOutput: new TextJobOutput(output));
    }

    [LoggerMessage(1, LogLevel.Information, "[push] Dispatching notification {NotificationId} → device {DeviceToken}: {Title}")]
    private partial void LogDispatchingPush(string notificationId, string deviceToken, string title);

    [LoggerMessage(2, LogLevel.Information, "[push] Delivered notification {NotificationId} to device {DeviceToken}")]
    private partial void LogPushDelivered(string notificationId, string deviceToken);
}
