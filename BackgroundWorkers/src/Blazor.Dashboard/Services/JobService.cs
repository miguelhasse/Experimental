using RequestProcessor;

namespace BlazorDashboard.Services;

/// <summary>
/// Scoped service (one instance per Blazor circuit / browser tab) that manages the
/// list of jobs submitted from the current session and bridges to the Orleans cluster
/// via <see cref="IClusterClient"/>.
/// </summary>
public sealed class JobService(IClusterClient client)
{
    public record JobEntry(
        string Id,
        string Payload,
        string? Category,
        DateTimeOffset SubmittedAt,
        JobStatus Status,
        string JobType = "Job",
        int? PercentComplete = null,
        string? ProgressMessage = null);

    private readonly List<JobEntry> _jobs = [];
    private readonly Lock _lock = new();

    // Cached snapshot of _jobs handed out by the Jobs getter.
    // Rebuilt on first read after any mutation; cleared by setting _snapshotDirty=true
    // (under _lock) in every mutator below.
    private JobEntry[]? _snapshot;
    private bool _snapshotDirty = true;

    /// <summary>Returns a snapshot of all jobs submitted in this session, most-recent first.</summary>
    public IReadOnlyList<JobEntry> Jobs
    {
        get
        {
            lock (_lock)
            {
                if (_snapshotDirty || _snapshot is null)
                {
                    _snapshot = [.. _jobs];
                    _snapshotDirty = false;
                }
                return _snapshot;
            }
        }
    }

    /// <summary>Submits a <see cref="JobRequest"/> and tracks it locally.</summary>
    public async Task SubmitJobRequestAsync(
        string payload, string? category,
        RequestPriority priority = RequestPriority.Normal,
        CancellationToken ct = default)
    {
        var jobId = $"ui-{Guid.NewGuid():N}";
        var entry = new JobEntry(jobId, payload, category, DateTimeOffset.UtcNow, JobStatus.Pending, "Job");
        lock (_lock) { _jobs.Insert(0, entry); _snapshotDirty = true; }
        await client.GetGrain<IJobGrain>(jobId).SubmitAsync(new JobRequest(payload, category, priority));
    }

    /// <summary>Submits a <see cref="BatchJobRequest"/> and tracks it locally.</summary>
    public async Task SubmitBatchJobRequestAsync(
        IReadOnlyList<string> items, string? category,
        RequestPriority priority = RequestPriority.High,
        CancellationToken ct = default)
    {
        var jobId = $"ui-{Guid.NewGuid():N}";
        var displayPayload = items.Count == 1
            ? $"1 item: {items[0]}"
            : $"{items.Count} items: {string.Join(", ", items.Take(3))}{(items.Count > 3 ? "…" : "")}";
        var entry = new JobEntry(jobId, displayPayload, category, DateTimeOffset.UtcNow, JobStatus.Pending, "Batch");
        lock (_lock) { _jobs.Insert(0, entry); _snapshotDirty = true; }
        await client.GetGrain<IJobGrain>(jobId).SubmitAsync(new BatchJobRequest(items, category, priority));
    }

    /// <summary>Submits a <see cref="ScheduledJobRequest"/> and tracks it locally.</summary>
    public async Task SubmitScheduledJobRequestAsync(
        string payload, DateTimeOffset scheduledAt, string? category,
        RequestPriority priority = RequestPriority.Low,
        CancellationToken ct = default)
    {
        var jobId = $"ui-{Guid.NewGuid():N}";
        var displayPayload = $"{payload} @ {scheduledAt.LocalDateTime:HH:mm}";
        var entry = new JobEntry(jobId, displayPayload, category, DateTimeOffset.UtcNow, JobStatus.Pending, "Scheduled");
        lock (_lock) { _jobs.Insert(0, entry); _snapshotDirty = true; }
        await client.GetGrain<IJobGrain>(jobId).SubmitAsync(new ScheduledJobRequest(payload, scheduledAt, priority));
    }

    /// <summary>Polls every tracked job grain for its current status and updates the local list.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        List<JobEntry> snapshot;
        lock (_lock) { snapshot = [.. _jobs]; }

        if (snapshot.Count == 0) return;

        var statusTasks = snapshot.Select(async e =>
        {
            var grain = client.GetGrain<IJobGrain>(e.Id);
            var status = await grain.GetStatusAsync();
            var progress = await grain.GetProgressAsync();
            return (e.Id, status, progress);
        });

        var updates = await Task.WhenAll(statusTasks);

        lock (_lock)
        {
            for (int i = 0; i < _jobs.Count; i++)
            {
                var match = updates.FirstOrDefault(u => u.Id == _jobs[i].Id);
                if (match.Id is not null)
                {
                    _jobs[i] = _jobs[i] with
                    {
                        Status = match.status,
                        PercentComplete = match.progress?.PercentComplete,
                        ProgressMessage = match.progress?.Message,
                    };
                    _snapshotDirty = true;
                }
            }
        }
    }

    /// <summary>
    /// Attempts to cancel a queued durable job.
    /// Returns <c>true</c> if the grain accepted the cancellation.
    /// </summary>
    public async Task<bool> CancelAsync(string jobId)
    {
        var cancelled = await client.GetGrain<IJobGrain>(jobId).TryCancelAsync();
        if (cancelled)
        {
            lock (_lock)
            {
                var idx = _jobs.FindIndex(e => e.Id == jobId);
                if (idx >= 0)
                {
                    _jobs[idx] = _jobs[idx] with { Status = JobStatus.Cancelled };
                    _snapshotDirty = true;
                }
            }
        }
        return cancelled;
    }

    /// <summary>Removes a completed/cancelled/failed job from the local list.</summary>
    public void Remove(string jobId)
    {
        lock (_lock)
        {
            if (_jobs.RemoveAll(e => e.Id == jobId) > 0)
                _snapshotDirty = true;
        }
    }

    /// <summary>Removes all completed, failed, or cancelled jobs from the local list.</summary>
    public void RemoveAll()
    {
        lock (_lock)
        {
            if (_jobs.RemoveAll(e => e.Status is not (JobStatus.Pending or JobStatus.Processing)) > 0)
                _snapshotDirty = true;
        }
    }

    /// <summary>
    /// Fetches a point-in-time statistics snapshot from the pool grain service.
    /// Returns <c>null</c> if the silo is unreachable or the call fails.
    /// </summary>
    public async Task<RequestPoolStatsSnapshot?> GetPoolStatsAsync(CancellationToken ct = default)
    {
        try
        {
            return await client.GetGrain<IPoolStatsGrain>("default").GetSnapshotAsync();
        }
        catch
        {
            return null;
        }
    }
}
