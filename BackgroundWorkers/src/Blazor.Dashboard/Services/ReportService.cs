namespace BlazorDashboard.Services;

/// <summary>
/// Singleton service that tracks <see cref="IReportGrain"/> instances submitted from
/// the Blazor dashboard and bridges operation-dispatch calls to the Orleans cluster.
/// </summary>
/// <remarks>
/// Registered as a singleton so report state is shared across all browser circuits
/// and persists across page refreshes within the same server process.
/// </remarks>
public sealed class ReportService(IClusterClient client)
{
    private static readonly char[] IdChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    /// <summary>Snapshot of a tracked report and the current status of each operation.</summary>
    public record ReportEntry(
        string Id,
        DateTimeOffset CreatedAt,
        JobStatus GenerateStatus = JobStatus.Unknown,
        JobStatus ReviewStatus = JobStatus.Unknown,
        JobStatus PublishStatus = JobStatus.Unknown,
        JobProgressSnapshot? GenerateProgress = null,
        JobProgressSnapshot? ReviewProgress = null,
        JobProgressSnapshot? PublishProgress = null);

    private readonly List<ReportEntry> _reports = [];
    private readonly Lock _lock = new();

    /// <summary>Returns a snapshot of all tracked reports, most-recent first.</summary>
    public IReadOnlyList<ReportEntry> Reports
    {
        get { lock (_lock) { return [.. _reports]; } }
    }

    /// <summary>
    /// Generates a unique report ID in the format <c>report-XXXX</c>
    /// where <c>XXXX</c> is 4 random lower-alphanumeric characters.
    /// </summary>
    public static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return $"report-{IdChars[bytes[0] % IdChars.Length]}{IdChars[bytes[1] % IdChars.Length]}{IdChars[bytes[2] % IdChars.Length]}{IdChars[bytes[3] % IdChars.Length]}";
    }

    /// <summary>
    /// Generates <paramref name="count"/> report entries with auto-generated IDs
    /// and immediately starts each report in parallel.
    /// </summary>
    public async Task<IReadOnlyList<string>> CreateBatchAsync(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => GenerateId()).ToList();
        await Task.WhenAll(ids.Select(RunAllAsync));
        return ids;
    }

    /// <summary>
    /// Registers a report ID locally (if not already tracked) and dispatches
    /// all three operations (Generate, Review, Publish) in parallel.
    /// Each operation is idempotent at the grain level.
    /// </summary>
    public async Task RunAllAsync(string reportId)
    {
        var id = reportId.Trim();
        lock (_lock)
        {
            var existing = _reports.FindIndex(e => e.Id == id);
            if (existing < 0)
                _reports.Insert(0, new ReportEntry(id, DateTimeOffset.UtcNow));
        }

        var grain = client.GetGrain<IReportGrain>(id);

        // Dispatch all three operations in parallel; each has its own tracker key
        // so they don't interfere with each other.
        var generateStatus = await grain.GenerateAsync();
        var reviewStatus = await grain.ReviewAsync();
        var publishStatus = await grain.PublishAsync();

        UpdateStatuses(id, generateStatus, reviewStatus, publishStatus);
    }

    /// <summary>Polls <see cref="IReportGrain.GetSummaryAsync"/> for all tracked entries.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        List<ReportEntry> snapshot;
        lock (_lock) { snapshot = [.. _reports]; }
        if (snapshot.Count == 0) return;

        var tasks = snapshot.Select(async e =>
        {
            var grain = client.GetGrain<IReportGrain>(e.Id);
            var summary = await grain.GetSummaryAsync();
            return (e.Id, summary);
        });

        var results = await Task.WhenAll(tasks);

        lock (_lock)
        {
            for (int i = 0; i < _reports.Count; i++)
            {
                var match = results.FirstOrDefault(r => r.Id == _reports[i].Id);
                if (match.Id is not null)
                {
                    _reports[i] = _reports[i] with
                    {
                        GenerateStatus = match.summary.GenerateStatus,
                        ReviewStatus = match.summary.ReviewStatus,
                        PublishStatus = match.summary.PublishStatus,
                        GenerateProgress = match.summary.GenerateProgress,
                        ReviewProgress = match.summary.ReviewProgress,
                        PublishProgress = match.summary.PublishProgress,
                    };
                }
            }
        }
    }

    /// <summary>Removes a report entry from the local tracking list.</summary>
    public void Remove(string reportId)
    {
        lock (_lock) { _reports.RemoveAll(e => e.Id == reportId); }
    }

    /// <summary>Removes all report entries where every operation is no longer active.</summary>
    public void RemoveAll()
    {
        lock (_lock)
        {
            _reports.RemoveAll(r =>
                r.GenerateStatus is not (JobStatus.Pending or JobStatus.Processing) &&
                r.ReviewStatus is not (JobStatus.Pending or JobStatus.Processing) &&
                r.PublishStatus is not (JobStatus.Pending or JobStatus.Processing));
        }
    }

    /// <summary>
    /// Cancels all queued (not yet dispatched) operations across all tracked reports.
    /// Only operations still in the pool queue are affected; already-dispatched operations are skipped.
    /// </summary>
    public async Task CancelAllPendingAsync()
    {
        List<string> ids;
        lock (_lock)
        {
            ids = _reports
                .Where(r =>
                    r.GenerateStatus is JobStatus.Pending or JobStatus.Processing ||
                    r.ReviewStatus is JobStatus.Pending or JobStatus.Processing ||
                    r.PublishStatus is JobStatus.Pending or JobStatus.Processing)
                .Select(r => r.Id)
                .ToList();
        }

        if (ids.Count == 0) return;

        await Task.WhenAll(ids.Select(id =>
            client.GetGrain<IReportGrain>(id).CancelAsync()));

        await RefreshAllAsync();
    }

    /// <summary>Returns <c>true</c> when the report can be restarted.</summary>
    public static bool IsRestartable(ReportEntry entry) =>
        entry.GenerateStatus is not (JobStatus.Processing or JobStatus.Pending) &&
        (entry.GenerateStatus is JobStatus.Failed or JobStatus.Cancelled
         || entry.ReviewStatus is JobStatus.Failed or JobStatus.Cancelled
         || entry.PublishStatus is JobStatus.Failed or JobStatus.Cancelled);

    private void UpdateStatuses(string id,
        JobStatus? generateStatus = null,
        JobStatus? reviewStatus = null,
        JobStatus? publishStatus = null)
    {
        lock (_lock)
        {
            var idx = _reports.FindIndex(e => e.Id == id);
            if (idx < 0) return;
            var e = _reports[idx];
            _reports[idx] = e with
            {
                GenerateStatus = generateStatus ?? e.GenerateStatus,
                ReviewStatus = reviewStatus ?? e.ReviewStatus,
                PublishStatus = publishStatus ?? e.PublishStatus,
            };
        }
    }
}
