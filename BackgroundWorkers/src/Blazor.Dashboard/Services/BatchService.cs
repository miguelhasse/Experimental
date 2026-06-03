namespace BlazorDashboard.Services;

/// <summary>
/// Singleton service that tracks <see cref="IBatchCoordinatorGrain"/> instances submitted from
/// the Blazor dashboard and bridges batch-dispatch calls to the Orleans cluster.
/// </summary>
/// <remarks>
/// Registered as a singleton so batch state is shared across all browser circuits
/// and persists across page refreshes within the same server process.
/// </remarks>
public sealed class BatchService(IClusterClient client)
{
    private static readonly char[] IdChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    /// <summary>Snapshot of a tracked batch and the current coordination status.</summary>
    public record BatchEntry(
        string Id,
        DateTimeOffset CreatedAt,
        int ItemCount,
        int WorkerCount,
        JobStatus Status = JobStatus.Unknown,
        BatchCoordinationSummary? Summary = null);

    private readonly List<BatchEntry> _batches = [];
    private readonly Lock _lock = new();

    /// <summary>Returns a snapshot of all tracked batches, most-recent first.</summary>
    public IReadOnlyList<BatchEntry> Batches
    {
        get { lock (_lock) { return [.. _batches]; } }
    }

    /// <summary>
    /// Generates a unique batch ID in the format <c>batch-XXXX</c>
    /// where <c>XXXX</c> is 4 random lower-alphanumeric characters.
    /// </summary>
    public static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return $"batch-{IdChars[bytes[0] % IdChars.Length]}{IdChars[bytes[1] % IdChars.Length]}{IdChars[bytes[2] % IdChars.Length]}{IdChars[bytes[3] % IdChars.Length]}";
    }

    /// <summary>
    /// Creates a batch by generating an ID, registering it locally,
    /// calling <see cref="IBatchCoordinatorGrain.ProcessBatchAsync"/> on the grain,
    /// and returning the batch ID.
    /// </summary>
    public Task<string> CreateBatchAsync(int itemCount, int workerCount)
    {
        var batchId = GenerateId();

        lock (_lock)
        {
            _batches.Insert(0, new BatchEntry(batchId, DateTimeOffset.UtcNow, itemCount, workerCount));
        }

        // Fire-and-forget: the coordinator fans out all items and deactivates quickly.
        // The caller gets the batch ID immediately; status arrives through polling.
        var grain = client.GetGrain<IBatchCoordinatorGrain>(batchId);
        _ = grain.ProcessBatchAsync(itemCount, workerCount)
                 .ContinueWith(
                     t => { if (t.IsCompletedSuccessfully) UpdateEntry(batchId, t.Result.OverallStatus, t.Result); },
                     TaskScheduler.Default);

        return Task.FromResult(batchId);
    }

    /// <summary>Polls <see cref="IBatchCoordinatorGrain.GetSummaryAsync"/> for all tracked entries.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        List<BatchEntry> snapshot;
        lock (_lock) { snapshot = [.. _batches]; }
        if (snapshot.Count == 0) return;

        var tasks = snapshot.Select(async e =>
        {
            var grain = client.GetGrain<IBatchCoordinatorGrain>(e.Id);
            var summary = await grain.GetSummaryAsync();
            return (e.Id, summary);
        });

        var results = await Task.WhenAll(tasks);

        lock (_lock)
        {
            for (int i = 0; i < _batches.Count; i++)
            {
                var match = results.FirstOrDefault(r => r.Id == _batches[i].Id);
                if (match.Id is not null)
                {
                    _batches[i] = _batches[i] with
                    {
                        Status = match.summary.OverallStatus,
                        Summary = match.summary,
                    };
                }
            }
        }
    }

    /// <summary>Removes a batch entry from the local tracking list.</summary>
    public void Remove(string batchId)
    {
        lock (_lock) { _batches.RemoveAll(e => e.Id == batchId); }
    }

    /// <summary>Removes all non-active batch entries from the local tracking list.</summary>
    public void RemoveAll()
    {
        lock (_lock) { _batches.RemoveAll(e => e.Status is not (JobStatus.Pending or JobStatus.Processing)); }
    }

    /// <summary>
    /// Cancels all pending batches across all tracked batches.
    /// Only batches still in the pool queue are affected; already-dispatched batches are skipped.
    /// </summary>
    public async Task CancelAllPendingAsync()
    {
        List<string> ids;
        lock (_lock)
        {
            ids = _batches
                .Where(b => b.Status is JobStatus.Pending or JobStatus.Processing)
                .Select(b => b.Id)
                .ToList();
        }

        if (ids.Count == 0) return;

        await Task.WhenAll(ids.Select(id =>
            client.GetGrain<IBatchCoordinatorGrain>(id).CancelAsync()));

        await RefreshAllAsync();
    }

    /// <summary>Returns <c>true</c> when the batch can be restarted.</summary>
    public static bool IsRestartable(BatchEntry entry) =>
        entry.Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Completed;

    private void UpdateEntry(string id, JobStatus? status = null, BatchCoordinationSummary? summary = null)
    {
        lock (_lock)
        {
            var idx = _batches.FindIndex(e => e.Id == id);
            if (idx < 0) return;
            var e = _batches[idx];
            _batches[idx] = e with
            {
                Status = status ?? e.Status,
                Summary = summary ?? e.Summary,
            };
        }
    }
}
