using System.Collections.Concurrent;

namespace OrleansSample;

/// <summary>
/// Thread-safe store for job statuses and progress, shared between grain activations
/// (which run on the Orleans scheduler) and request-pool callbacks
/// (which run on thread-pool threads).
/// </summary>
public interface IJobTracker
{
    void SetStatus(string jobId, JobStatus status);

    JobStatus GetStatus(string jobId);

    IReadOnlyDictionary<string, JobStatus> Snapshot();

    /// <summary>Stores the latest progress snapshot for a running job.</summary>
    void SetProgress(string jobId, int percentComplete, string? message);

    /// <summary>Returns the latest progress for a job, or <c>null</c> if none has been reported yet.</summary>
    JobProgressSnapshot? GetProgress(string jobId);

    /// <summary>Stores the typed output produced by a completed job step.</summary>
    void SetOutput(string jobId, JobOutput? output);

    /// <summary>Returns the typed output for a job step, or <c>null</c> if none has been set.</summary>
    JobOutput? GetOutput(string jobId);

    /// <summary>Clears any stored progress for a job (called on submit/reset).</summary>
    void ClearProgress(string jobId);
}

internal sealed class InMemoryJobTracker : IJobTracker
{
    private readonly ConcurrentDictionary<string, JobStatus> _map = new();
    private readonly ConcurrentDictionary<string, JobProgressSnapshot> _progress = new();
    private readonly ConcurrentDictionary<string, JobOutput> _outputs = new();

    public void SetStatus(string jobId, JobStatus status) => _map[jobId] = status;

    public JobStatus GetStatus(string jobId) => _map.GetValueOrDefault(jobId, JobStatus.Unknown);

    public IReadOnlyDictionary<string, JobStatus> Snapshot() => new Dictionary<string, JobStatus>(_map);

    public void SetProgress(string jobId, int percentComplete, string? message) =>
        _progress[jobId] = new JobProgressSnapshot(percentComplete, message);

    public JobProgressSnapshot? GetProgress(string jobId) =>
        _progress.TryGetValue(jobId, out var snap) ? snap : null;

    public void ClearProgress(string jobId) => _progress.TryRemove(jobId, out _);

    public void SetOutput(string jobId, JobOutput? output)
    {
        if (output is null) _outputs.TryRemove(jobId, out _);
        else _outputs[jobId] = output;
    }

    public JobOutput? GetOutput(string jobId) =>
        _outputs.TryGetValue(jobId, out var o) ? o : null;
}
