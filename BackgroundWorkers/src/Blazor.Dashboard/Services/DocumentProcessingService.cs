namespace BlazorDashboard.Services;

/// <summary>
/// Singleton service that tracks <see cref="IDocumentProcessingGrain"/> pipeline instances
/// submitted from the Blazor dashboard and bridges run calls to the Orleans cluster.
/// </summary>
public sealed class DocumentProcessingService(IClusterClient client)
{
    private static readonly char[] IdChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    /// <summary>Snapshot of a tracked pipeline and the current status of each step.</summary>
    public record PipelineEntry(
        string Id,
        DateTimeOffset CreatedAt,
        JobStatus Step1Status = JobStatus.Unknown,
        JobStatus Step2Status = JobStatus.Unknown,
        JobStatus Step3Status = JobStatus.Unknown,
        JobProgressSnapshot? Step1Progress = null,
        JobProgressSnapshot? Step2Progress = null,
        JobProgressSnapshot? Step3Progress = null,
        ExtractedContentOutput? Step1Output = null,
        TransformedContentOutput? Step2Output = null,
        IndexedContentOutput? Step3Output = null);

    private readonly List<PipelineEntry> _pipelines = [];
    private readonly Lock _lock = new();

    /// <summary>Returns a snapshot of all tracked pipelines, most-recent first.</summary>
    public IReadOnlyList<PipelineEntry> Pipelines
    {
        get { lock (_lock) { return [.. _pipelines]; } }
    }

    /// <summary>
    /// Generates a unique pipeline ID in the format <c>pipe-XXXX</c>
    /// where <c>XXXX</c> is 4 random lower-alphanumeric characters.
    /// </summary>
    public static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return $"pipe-{IdChars[bytes[0] % IdChars.Length]}{IdChars[bytes[1] % IdChars.Length]}{IdChars[bytes[2] % IdChars.Length]}{IdChars[bytes[3] % IdChars.Length]}";
    }

    /// <summary>
    /// Generates <paramref name="count"/> pipeline entries with auto-generated IDs
    /// and immediately starts each pipeline in parallel.
    /// </summary>
    public async Task<IReadOnlyList<string>> CreateBatchAsync(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => GenerateId()).ToList();
        await Task.WhenAll(ids.Select(RunAsync));
        return ids;
    }

    /// <summary>
    /// Registers a pipeline ID locally (if not already tracked) and starts it.
    /// If the pipeline already exists, restarts it from step 1.
    /// </summary>
    public async Task RunAsync(string pipelineId)
    {
        var id = pipelineId.Trim();
        lock (_lock)
        {
            var existing = _pipelines.FindIndex(e => e.Id == id);
            if (existing < 0)
                _pipelines.Insert(0, new PipelineEntry(id, DateTimeOffset.UtcNow));
        }

        var grain = client.GetGrain<IDocumentProcessingGrain>(id);
        var status = await grain.RunAsync();
        UpdateStep1Status(id, status);
    }

    /// <summary>Polls <see cref="IDocumentProcessingGrain.GetSummaryAsync"/> for all tracked entries.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        List<PipelineEntry> snapshot;
        lock (_lock) { snapshot = [.. _pipelines]; }
        if (snapshot.Count == 0) return;

        var tasks = snapshot.Select(async e =>
        {
            var grain = client.GetGrain<IDocumentProcessingGrain>(e.Id);
            var summary = await grain.GetSummaryAsync();
            return (e.Id, summary);
        });

        var results = await Task.WhenAll(tasks);

        lock (_lock)
        {
            for (int i = 0; i < _pipelines.Count; i++)
            {
                var match = results.FirstOrDefault(r => r.Id == _pipelines[i].Id);
                if (match.Id is not null)
                {
                    _pipelines[i] = _pipelines[i] with
                    {
                        Step1Status = match.summary.Step1Status,
                        Step2Status = match.summary.Step2Status,
                        Step3Status = match.summary.Step3Status,
                        Step1Progress = match.summary.Step1Progress,
                        Step2Progress = match.summary.Step2Progress,
                        Step3Progress = match.summary.Step3Progress,
                        Step1Output = match.summary.Step1Output,
                        Step2Output = match.summary.Step2Output,
                        Step3Output = match.summary.Step3Output,
                    };
                }
            }
        }
    }

    /// <summary>Removes a pipeline entry from the local tracking list.</summary>
    public void Remove(string pipelineId)
    {
        lock (_lock) { _pipelines.RemoveAll(e => e.Id == pipelineId); }
    }

    /// <summary>Removes all pipeline entries where every step is no longer active.</summary>
    public void RemoveAll()
    {
        lock (_lock)
        {
            _pipelines.RemoveAll(p =>
                p.Step1Status is not (JobStatus.Pending or JobStatus.Processing) &&
                p.Step2Status is not (JobStatus.Pending or JobStatus.Processing) &&
                p.Step3Status is not (JobStatus.Pending or JobStatus.Processing));
        }
    }

    /// <summary>
    /// Cancels all queued (not yet dispatched) steps across all tracked pipelines.
    /// Only steps still in the pool queue are affected; already-dispatched steps are skipped.
    /// </summary>
    public async Task CancelAllPendingAsync()
    {
        List<string> ids;
        lock (_lock)
        {
            ids = _pipelines
                .Where(p =>
                    p.Step1Status is JobStatus.Pending or JobStatus.Processing ||
                    p.Step2Status is JobStatus.Pending or JobStatus.Processing ||
                    p.Step3Status is JobStatus.Pending or JobStatus.Processing)
                .Select(p => p.Id)
                .ToList();
        }

        if (ids.Count == 0) return;

        await Task.WhenAll(ids.Select(id =>
            client.GetGrain<IDocumentProcessingGrain>(id).CancelAsync()));

        await RefreshAllAsync();
    }

    /// <summary>Returns <c>true</c> when the pipeline can be restarted.</summary>
    public static bool IsRestartable(PipelineEntry entry) =>
        entry.Step1Status is not (JobStatus.Processing or JobStatus.Pending) &&
        (entry.Step1Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Completed
         || entry.Step2Status is JobStatus.Failed or JobStatus.Cancelled
         || entry.Step3Status is JobStatus.Failed or JobStatus.Cancelled);

    private void UpdateStep1Status(string id, JobStatus status)
    {
        lock (_lock)
        {
            var idx = _pipelines.FindIndex(e => e.Id == id);
            if (idx < 0) return;
            _pipelines[idx] = _pipelines[idx] with { Step1Status = status };
        }
    }
}
