using Orleans.Journaling;

namespace OrleansSample;

/// <summary>
/// The only place that mutates and persists a <see cref="DurableJobGrain"/>'s durable state in response to data coming
/// back from the request pool. It is an ordinary in-process object: nothing here is callable from outside the silo.
/// </summary>
/// <remarks>
/// Even with a single ordered mailbox every call is still checked against the job's current
/// <see cref="DurableJobRecord.Epoch"/> and the last sequence applied, because the data can come from a run that was
/// superseded (a resubmit, a recovery) and because a worker may deliver the same message twice.
/// </remarks>
internal sealed class DurableJobStore(
    IDurableDictionary<string, DurableJobRecord> jobs,
    IDurableValue<int> finishedCount,
    Func<CancellationToken, ValueTask> persist)
{
    // Writes from the mailbox pump overlap with request turns at await points, so "mutate + write journal" is atomic
    // with respect to reads: callers never observe state that has not been persisted yet (and a deactivation cannot lose it).
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Applies progress. Not flushed on its own; it rides along with the next write (best effort by design).</summary>
    public bool ApplyProgress(string jobId, long epoch, long sequence, int percentComplete, string? message)
    {
        // Progress has its own sequence space: it is ordered against other progress only.
        if (!TryGetApplicable(jobId, epoch, sequence, static r => r.LastProgressSequence, out var current))
            return false;

        // Keep Request/OwnerSilo/Attempts/Epoch: they are what lets an unfinished job be recovered and fenced.
        jobs[jobId] = current with
        {
            Status = JobStatus.Processing,
            PercentComplete = percentComplete,
            Message = message,
            LastProgressSequence = sequence,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return true;
    }

    /// <summary>
    /// Applies a result: <see cref="JobStatus.Processing"/> is a partial result, the three terminal statuses are final.
    /// Returns whether it changed the job (false for stale, duplicate or late data). Any other status throws.
    /// </summary>
    public async Task<bool> ApplyResultAsync(string jobId, long epoch, long sequence, JobStatus status, string? output, string? error)
    {
        // Anything else (Pending, Unknown) would be counted as "final" without making the job terminal, so it could be
        // applied and counted repeatedly.
        if (status is not (JobStatus.Processing or JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(status), status, "A result must be Processing (partial), Completed, Failed or Cancelled.");

        return await TryUpdateAsync(() =>
        {
            if (!TryGetApplicable(jobId, epoch, sequence, static r => r.LastSequence, out var current))
                return false; // stale epoch, duplicate, out of order, or the run already has its final result

            var isFinal = status is not JobStatus.Processing;

            jobs[jobId] = current with
            {
                Status = status,
                PercentComplete = isFinal ? 100 : current.PercentComplete,
                Message = isFinal ? null : current.Message,
                Output = output,
                Error = error,
                LastSequence = sequence,
                ResultsReceived = current.ResultsReceived + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            // Exactly once per run: a final result is only ever applied to a run that is not yet terminal.
            if (isFinal)
                finishedCount.Value++;

            return true;
        });
    }

    /// <summary>
    /// Fails the run (<paramref name="epoch"/>) of a job that could not be started, so it does not sit in
    /// <see cref="JobStatus.Processing"/> under a live owner. Does nothing if the run was already superseded or finished.
    /// The submission key is cleared so a client retry with the same key is accepted instead of being treated as a
    /// duplicate of the failed start.
    /// </summary>
    public Task<bool> FailRunAsync(string jobId, long epoch, string reason) =>
        TryUpdateAsync(() =>
        {
            jobs.TryGetValue(jobId, out var current);
            if (current is null || current.Epoch != epoch || current.IsTerminal)
                return false;

            jobs[jobId] = current with
            {
                Status = JobStatus.Failed,
                PercentComplete = 100,
                Message = null,
                Error = reason,
                SubmissionKey = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            finishedCount.Value++;
            return true;
        });

    /// <summary>
    /// Cancels a job that is still <see cref="JobStatus.Processing"/>: it becomes terminal at once, so late data from its
    /// run is ignored and recovery never restarts it. Returns the epoch of the run that was cancelled, or null if the job is
    /// unknown or already finished. Counted as finished exactly once, like any other final result.
    /// </summary>
    public async Task<long?> CancelRunAsync(string jobId)
    {
        long? cancelledEpoch = null;
        await TryUpdateAsync(() =>
        {
            if (!jobs.TryGetValue(jobId, out var current) || current.Status is not JobStatus.Processing)
                return false;

            jobs[jobId] = current with
            {
                Status = JobStatus.Cancelled,
                Message = "Cancelled",
                Error = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            finishedCount.Value++;
            cancelledEpoch = current.Epoch;
            return true;
        });
        return cancelledEpoch;
    }

    /// <summary>
    /// Gives up the runs this activation can no longer receive results for (it is being deactivated): the jobs stay
    /// <see cref="JobStatus.Processing"/> but with no owner, so recovery restarts them. Runs that were finished or
    /// replaced meanwhile are left alone.
    /// </summary>
    public Task<bool> ReleaseRunsAsync(IReadOnlyCollection<(string JobId, long Epoch)> runs) =>
        TryUpdateAsync(() =>
        {
            var changed = false;
            foreach (var (jobId, epoch) in runs)
            {
                if (!jobs.TryGetValue(jobId, out var current) || current.Epoch != epoch || current.Status is not JobStatus.Processing)
                    continue;

                jobs[jobId] = current with { OwnerSilo = null, Message = "Released: the activation that owned it was deactivated", UpdatedAt = DateTimeOffset.UtcNow };
                changed = true;
            }

            return changed;
        });

    /// <summary>
    /// True if a call for (<paramref name="epoch"/>, <paramref name="sequence"/>) should change the job: the job exists,
    /// the call belongs to its current run, that run has no final result yet, and the call is newer than the last one
    /// applied in its sequence space (<paramref name="lastApplied"/> picks progress or results).
    /// </summary>
    private bool TryGetApplicable(
        string jobId, long epoch, long sequence, Func<DurableJobRecord, long> lastApplied, out DurableJobRecord current)
    {
        jobs.TryGetValue(jobId, out var record);
        if (record is null
            || record.Epoch != epoch
            || record.IsTerminal
            || sequence <= lastApplied(record))
        {
            current = null!;
            return false;
        }

        current = record;
        return true;
    }

    /// <summary>Applies <paramref name="mutate"/> and persists it, serialised with other writes and reads.</summary>
    public Task UpdateAsync(Action mutate) =>
        TryUpdateAsync(() =>
        {
            mutate();
            return true;
        });

    /// <summary>
    /// Like <see cref="UpdateAsync"/>, but the journal is only written if <paramref name="mutate"/> returns true,
    /// so ignored (stale or duplicate) calls cost nothing. Returns what <paramref name="mutate"/> returned.
    /// </summary>
    public async Task<bool> TryUpdateAsync(Func<bool> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            var changed = mutate();
            if (changed)
                await persist(CancellationToken.None);
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads durable state, waiting for any in-flight write so only persisted data is returned.</summary>
    public async Task<T> ReadAsync<T>(Func<T> read)
    {
        await _gate.WaitAsync();
        try
        {
            return read();
        }
        finally
        {
            _gate.Release();
        }
    }
}
