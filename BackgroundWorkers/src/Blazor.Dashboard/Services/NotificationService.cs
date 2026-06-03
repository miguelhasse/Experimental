namespace BlazorDashboard.Services;

/// <summary>
/// Singleton service that tracks <see cref="INotificationGrain"/> instances submitted from
/// the Blazor dashboard and bridges channel-dispatch calls to the Orleans cluster.
/// </summary>
/// <remarks>
/// Registered as a singleton so notification state is shared across all browser circuits
/// and persists across page refreshes within the same server process.
/// </remarks>
public sealed class NotificationService(IClusterClient client)
{
    private static readonly char[] IdChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    /// <summary>Snapshot of a tracked notification and the current status of each channel.</summary>
    public record NotificationEntry(
        string Id,
        DateTimeOffset CreatedAt,
        JobStatus EmailStatus = JobStatus.Unknown,
        JobStatus SmsStatus = JobStatus.Unknown,
        JobStatus PushStatus = JobStatus.Unknown,
        JobProgressSnapshot? EmailProgress = null,
        JobProgressSnapshot? SmsProgress = null,
        JobProgressSnapshot? PushProgress = null);

    private readonly List<NotificationEntry> _notifications = [];
    private readonly Lock _lock = new();

    /// <summary>Returns a snapshot of all tracked notifications, most-recent first.</summary>
    public IReadOnlyList<NotificationEntry> Notifications
    {
        get { lock (_lock) { return [.. _notifications]; } }
    }

    /// <summary>
    /// Generates a unique notification ID in the format <c>notif-XXXX</c>
    /// where <c>XXXX</c> is 4 random lower-alphanumeric characters.
    /// </summary>
    public static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return $"notif-{IdChars[bytes[0] % IdChars.Length]}{IdChars[bytes[1] % IdChars.Length]}{IdChars[bytes[2] % IdChars.Length]}{IdChars[bytes[3] % IdChars.Length]}";
    }

    /// <summary>
    /// Generates <paramref name="count"/> notification grain entries with auto-generated IDs
    /// and immediately dispatches all three channels for each in parallel.
    /// </summary>
    public async Task<IReadOnlyList<string>> CreateBatchAsync(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => GenerateId()).ToList();

        await Task.WhenAll(ids.Select(async id =>
        {
            await CreateAsync(id);
            await SendAllAsync(id);
        }));

        return ids;
    }

    /// <summary>
    /// Registers a notification ID locally. Idempotent — if the ID is already tracked,
    /// returns without adding a duplicate.
    /// </summary>
    public Task CreateAsync(string notificationId)
    {
        var id = notificationId.Trim();
        lock (_lock)
        {
            if (_notifications.Any(e => e.Id == id)) return Task.CompletedTask;
            _notifications.Insert(0, new NotificationEntry(id, DateTimeOffset.UtcNow));
        }
        return Task.CompletedTask;
    }

    /// <summary>Dispatches the email channel for the given notification ID.</summary>
    public async Task<JobStatus> SendEmailAsync(string notificationId)
    {
        var grain = client.GetGrain<INotificationGrain>(notificationId);
        var status = await grain.SendEmailAsync(
            recipient: "user@example.com",
            subject: $"Notification: {notificationId}");
        UpdateEntry(notificationId, emailStatus: status);
        return status;
    }

    /// <summary>Dispatches the SMS channel for the given notification ID.</summary>
    public async Task<JobStatus> SendSmsAsync(string notificationId)
    {
        var grain = client.GetGrain<INotificationGrain>(notificationId);
        var status = await grain.SendSmsAsync(
            phoneNumber: "+15550001234",
            body: $"Notification {notificationId} received");
        UpdateEntry(notificationId, smsStatus: status);
        return status;
    }

    /// <summary>Dispatches the push channel for the given notification ID.</summary>
    public async Task<JobStatus> SendPushAsync(string notificationId)
    {
        var safeToken = notificationId.Length >= 8
            ? notificationId[..8]
            : notificationId.PadRight(8, '0');
        var grain = client.GetGrain<INotificationGrain>(notificationId);
        var status = await grain.SendPushAsync(
            deviceToken: $"device-{safeToken}",
            title: $"Notification: {notificationId}");
        UpdateEntry(notificationId, pushStatus: status);
        return status;
    }

    /// <summary>Dispatches all retryable channels (Unknown, Failed, or Cancelled) in parallel.</summary>
    public async Task SendAllAsync(string notificationId)
    {
        List<Task> tasks;
        lock (_lock)
        {
            var entry = _notifications.FirstOrDefault(e => e.Id == notificationId);
            if (entry is null) return;

            // Build the task list inside the lock so the retryability check and the
            // decision to dispatch are atomic with respect to concurrent RefreshAllAsync
            // or UpdateEntry calls on other threads.
            tasks = new List<Task>(3);
            if (IsRetryable(entry.EmailStatus)) tasks.Add(SendEmailAsync(notificationId));
            if (IsRetryable(entry.SmsStatus)) tasks.Add(SendSmsAsync(notificationId));
            if (IsRetryable(entry.PushStatus)) tasks.Add(SendPushAsync(notificationId));
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    /// <summary>Returns <c>true</c> when a channel status allows a new dispatch attempt.</summary>
    public static bool IsRetryable(JobStatus status) =>
        status is JobStatus.Unknown or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>Polls <see cref="INotificationGrain.GetAllStatusAsync"/> for all tracked entries.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        List<NotificationEntry> snapshot;
        lock (_lock) { snapshot = [.. _notifications]; }
        if (snapshot.Count == 0) return;

        var tasks = snapshot.Select(async e =>
        {
            var grain = client.GetGrain<INotificationGrain>(e.Id);
            var statuses = await grain.GetAllStatusAsync();
            return (e.Id, statuses);
        });

        var results = await Task.WhenAll(tasks);

        lock (_lock)
        {
            for (int i = 0; i < _notifications.Count; i++)
            {
                var match = results.FirstOrDefault(r => r.Id == _notifications[i].Id);
                if (match.Id is not null)
                {
                    _notifications[i] = _notifications[i] with
                    {
                        EmailStatus = match.statuses.EmailStatus,
                        SmsStatus = match.statuses.SmsStatus,
                        PushStatus = match.statuses.PushStatus,
                        EmailProgress = match.statuses.EmailProgress,
                        SmsProgress = match.statuses.SmsProgress,
                        PushProgress = match.statuses.PushProgress,
                    };
                }
            }
        }
    }

    /// <summary>Removes a notification entry from the local tracking list.</summary>
    public void Remove(string notificationId)
    {
        lock (_lock) { _notifications.RemoveAll(e => e.Id == notificationId); }
    }

    /// <summary>
    /// Removes all notification entries where no channel is active or retryable
    /// (i.e., all channels have completed successfully).
    /// </summary>
    public void RemoveAll()
    {
        lock (_lock)
        {
            _notifications.RemoveAll(n =>
                !IsRetryable(n.EmailStatus) && !IsRetryable(n.SmsStatus) && !IsRetryable(n.PushStatus) &&
                n.EmailStatus is not (JobStatus.Pending or JobStatus.Processing) &&
                n.SmsStatus is not (JobStatus.Pending or JobStatus.Processing) &&
                n.PushStatus is not (JobStatus.Pending or JobStatus.Processing));
        }
    }

    /// <summary>
    /// Cancels all queued (not yet dispatched) channels across all tracked notifications.
    /// Only channels still in the pool queue are affected; already-dispatched channels are skipped.
    /// </summary>
    public async Task CancelAllPendingAsync()
    {
        List<string> ids;
        lock (_lock)
        {
            ids = _notifications
                .Where(n =>
                    n.EmailStatus is JobStatus.Pending or JobStatus.Processing ||
                    n.SmsStatus is JobStatus.Pending or JobStatus.Processing ||
                    n.PushStatus is JobStatus.Pending or JobStatus.Processing)
                .Select(n => n.Id)
                .ToList();
        }

        if (ids.Count == 0) return;

        await Task.WhenAll(ids.Select(id =>
            client.GetGrain<INotificationGrain>(id).CancelAsync()));

        await RefreshAllAsync();
    }

    private void UpdateEntry(string id,
        JobStatus? emailStatus = null,
        JobStatus? smsStatus = null,
        JobStatus? pushStatus = null)
    {
        lock (_lock)
        {
            var idx = _notifications.FindIndex(e => e.Id == id);
            if (idx < 0) return;
            var e = _notifications[idx];
            _notifications[idx] = e with
            {
                EmailStatus = emailStatus ?? e.EmailStatus,
                SmsStatus = smsStatus ?? e.SmsStatus,
                PushStatus = pushStatus ?? e.PushStatus,
            };
        }
    }
}
