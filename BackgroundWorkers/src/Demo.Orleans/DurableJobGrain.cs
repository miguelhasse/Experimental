using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;

namespace OrleansSample;

/// <summary>
/// Job grain backed by Orleans Journaling. Flow:
/// <list type="number">
///   <item><see cref="SubmitAsync"/> validates the request, records <see cref="JobStatus.Processing"/> (with the request and
///         the owning silo) in durable state and enqueues the work in the <see cref="IRequestPool"/>.</item>
///   <item><b>Results come back through an in-process mailbox, not through a grain method.</b> Pool workers run outside the grain
///         scheduler, so they only write progress and results into this activation's channel. A pump started in
///         <see cref="OnActivateAsync"/> drains it: its continuations run on the grain's own scheduler, so state is only
///         ever touched from grain turns, with no polling delay. Because no grain method receives results, nothing outside the
///         silo process can forge one.</item>
///   <item><b>Keep-alive:</b> only the activation that started a run can receive its results, so while any run is in flight
///         the grain delays its own deactivation (<see cref="JobRecoveryOptions.KeepAliveSlice"/>, renewed by a timer),
///         however long the job runs and whatever Orleans' idle collection age is. When the last run ends the delay is lifted.</item>
///   <item><b>Activation lost anyway</b> (silo shutdown, an administrator collecting it): <see cref="OnDeactivateAsync"/> applies what
///         already reached the mailbox, cancels the runs that are still queued in the pool (a run a worker already picked up cannot be
///         cancelled through the pool monitor and finishes with its data dropped) and releases them (no owner), and recovery restarts them.
///         The same happens if a reactivated grain finds a job owned by this silo that its activation is not running.</item>
///   <item><b>Recovery:</b> pool work lives in memory, so if the owning silo dies the job stays
///         <see cref="JobStatus.Processing"/> forever. On activation (and via <see cref="RecoverUnfinishedJobsAsync"/>) the grain
///         resubmits orphaned jobs (see <see cref="IsOrphaned(DurableJobRecord, IJobOwnerLiveness, DateTimeOffset, TimeSpan, bool)"/>),
///         up to <see cref="MaxRecoveryAttempts"/> times, then marks them <see cref="JobStatus.Failed"/>.</item>
///   <item><b>Scheduled recovery check:</b> activation only happens if something calls the grain, so while a grain
///         has <see cref="JobStatus.Processing"/> jobs it keeps one Orleans <em>durable job</em> scheduled
///         (<see cref="JobRecoveryOptions.CheckPeriod"/> ahead). When it fires, Orleans reactivates the grain and
///         calls <see cref="ExecuteJobAsync"/>, which runs recovery and schedules the next check if jobs are still
///         processing. Durable jobs are one-shot, so the chain simply ends once nothing is processing.</item>
///   <item><b>Run timeout:</b> a running job pins its grain, so a hung handler would pin it forever. A run that has not reported back
///         within <see cref="JobRecoveryOptions.MaxRunDuration"/> (plus a small allowance) is failed, whether or not the pool ever calls back;
///         the pool's own timeout, set to the same limit, additionally stops handlers that honour cancellation.</item>
///   <item><b>Failures:</b> a run that cannot be started is failed immediately instead of being left
///         <see cref="JobStatus.Processing"/> under a live owner, where recovery would never look at it.</item>
/// </list>
/// </summary>
public sealed partial class DurableJobGrain : DurableGrain, IDurableJobGrain, IDurableJobHandler
{
    /// <summary>A job that keeps losing its owner is failed instead of being retried forever.</summary>
    internal const int MaxRecoveryAttempts = 3;

    /// <summary>Upper bound for <see cref="JobRequest.PartialResults"/>: each partial result is a journal write.</summary>
    internal const int MaxPartialResults = 100;

    internal const int MaxJobIdLength = 128;

    /// <summary>The pool rejects request ids longer than this.</summary>
    private const int MaxRequestIdLength = 256;

    /// <summary>Name of the scheduled recovery-check job.</summary>
    internal const string RecoveryJobName = "recover-unfinished-jobs";

    private readonly IRequestPool _pool;
    private readonly IRequestPoolMonitor _monitor;
    private readonly IJobOwnerLiveness _liveness;
    private readonly ILocalDurableJobManager _scheduler;
    private readonly JobRecoveryOptions _options;
    private readonly ILogger<DurableJobGrain> _logger;
    private readonly IDurableDictionary<string, DurableJobRecord> _jobs;
    private readonly IDurableValue<int> _finishedCount;
    private readonly IDurableValue<DateTimeOffset?> _recoveryDueAt;
    private readonly IDurableValue<long> _lastEpoch;
    private readonly DurableJobStore _store;

    // Workers write here from pool threads; the pump reads on the grain's scheduler. In-process only.
    private readonly Channel<MailboxMessage> _mailbox =
        Channel.CreateUnbounded<MailboxMessage>(new UnboundedChannelOptions { SingleReader = true });

    // Runs handed to the pool by this activation. Only touched from grain turns (requests, timers, the pump).
    private readonly Dictionary<string, InFlightRun> _inFlight = new(StringComparer.Ordinal);

    private Task _pump = Task.CompletedTask;
    private IGrainTimer? _keepAliveTimer;

    // Serialises scheduling, and lets the decision read state *inside* the gate, so two callers
    // (for example a submit and a firing recovery job) cannot both schedule the next check.
    private readonly SemaphoreSlim _scheduleGate = new(1, 1);

    public DurableJobGrain(
        IRequestPool pool,
        IRequestPoolMonitor monitor,
        IJobOwnerLiveness liveness,
        ILocalDurableJobManager scheduler,
        IOptions<JobRecoveryOptions> options,
        ILogger<DurableJobGrain> logger,
        [FromKeyedServices("jobs")] IDurableDictionary<string, DurableJobRecord> jobs,
        [FromKeyedServices("finishedCount")] IDurableValue<int> finishedCount,
        [FromKeyedServices("recoveryDueAt")] IDurableValue<DateTimeOffset?> recoveryDueAt,
        [FromKeyedServices("lastEpoch")] IDurableValue<long> lastEpoch)
    {
        _pool = pool;
        _monitor = monitor;
        _liveness = liveness;
        _scheduler = scheduler;
        _options = options.Value;
        _logger = logger;
        _jobs = jobs;
        _finishedCount = finishedCount;
        _recoveryDueAt = recoveryDueAt;
        _lastEpoch = lastEpoch;
        _store = new DurableJobStore(_jobs, _finishedCount, WriteStateAsync);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Start the pump from inside the activation: its continuations resume on this grain's scheduler.
        _pump = PumpAsync();

        // Recover after activation completes. Running it inline could deadlock with a pool that completes synchronously.
        this.RegisterGrainTimer(
            async ct => await RecoverUnfinishedJobsAsync(),
            new GrainTimerCreationOptions { DueTime = TimeSpan.Zero, Period = Timeout.InfiniteTimeSpan, Interleave = true });

        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // No more messages after this: a worker that writes later finds the mailbox closed and its data is dropped.
        _mailbox.Writer.TryComplete();

        try
        {
            // Apply what already reached the mailbox before giving up on the rest.
            await _pump.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Deactivation took too long: release whatever is left.
        }

        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;

        if (_inFlight.Count > 0)
        {
            var lost = _inFlight.Select(kv => (kv.Key, kv.Value.Epoch)).ToList();
            foreach (var run in _inFlight.Values)
                _monitor.TryCancelRequest(run.RequestId);   // its results can no longer be delivered. Only stops a run still queued: one a worker is executing cannot be cancelled this way and finishes with its data dropped
            _inFlight.Clear();

            try
            {
                // Persist "no owner": recovery restarts these runs. If this write fails (the silo is going down), the
                // owner is dead or unknown to the next activation and recovery finds them that way instead.
                await _store.ReleaseRunsAsync(lost);
                LogRunsReleased(lost.Count, reason.ReasonCode.ToString());
            }
            catch (Exception ex)
            {
                LogReleaseFailed(ex);
            }
        }

        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    public async Task SubmitAsync(string jobId, JobRequest request)
    {
        // Reject bad input before any state is written: a record created for a request that then fails
        // (for example a null request) would be left Processing and could never be recovered.
        ValidateSubmission(this.GetPrimaryKeyString(), jobId, request);

        var duplicate = false;
        var epoch = 0L;
        var owner = _liveness.CurrentOwnerId;
        await _store.UpdateAsync(() =>
        {
            _jobs.TryGetValue(jobId, out var existing);

            // Already running, or the same client request submitted again (a retry): nothing to do.
            if (existing?.Status is JobStatus.Processing
                || (request.IdempotencyKey is not null && existing?.SubmissionKey == request.IdempotencyKey))
            {
                duplicate = true;
                return;
            }

            PruneFinishedJobs(keepJobId: jobId);

            // A resubmit of a finished job id is a new run: a new epoch fences off any late data from the old one.
            epoch = NextEpoch(atLeast: existing?.Epoch ?? 0);
            _jobs[jobId] = new DurableJobRecord(
                jobId, JobStatus.Processing, PercentComplete: 0, Message: null,
                Output: null, Error: null, DateTimeOffset.UtcNow,
                Request: request, OwnerSilo: owner, Attempts: 0, Epoch: epoch, LastSequence: 0,
                SubmissionKey: request.IdempotencyKey);

            // Tracked in the same step as the record, so recovery never sees a Processing job this silo owns
            // that the activation is not (yet) running.
            Track(jobId, epoch);
        });

        if (duplicate)
        {
            LogDuplicateSubmit(jobId);
            return;
        }

        // Schedule the recovery check before enqueueing: if the silo dies right after, that scheduled job
        // is what wakes this grain so the orphaned job is found.
        await EnsureRecoveryCheckScheduledAsync();

        try
        {
            await EnqueueAsync(jobId, request, epoch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The pool refused the work. The owner (this silo) is alive, so recovery would never pick the job up:
            // fail the run now so the client sees the outcome instead of a job that is "Processing" forever.
            LogEnqueueFailed(jobId, ex);
            await _store.FailRunAsync(jobId, epoch, $"Could not start: {ex.Message}");
            Untrack(jobId, epoch);   // only once the failure is recorded: an owned but untracked Processing job looks lost to recovery
            throw;
        }

        LogJobEnqueued(jobId);
    }

    /// <summary>
    /// Durable job entry point: the scheduled recovery check fired. Orleans may have reactivated the grain for this.
    /// Jobs are at-least-once, so this must be (and is) idempotent: recovery claims orphans under the write gate.
    /// </summary>
    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        if (context.Job.Name != RecoveryJobName)
            return;

        LogRecoveryCheckFired(context.DequeueCount);
        UpdateKeepAlive();   // renew it with every check as well

        // This check has fired, so clear the marker; recovery below schedules the next one if still needed.
        // A duplicate delivery of an older check must not clear the marker of the check that replaced it.
        await _store.TryUpdateAsync(() =>
        {
            if (!ShouldClearRecoveryMarker(_recoveryDueAt.Value, context.Job.DueTime, _options.CheckPeriod))
                return false;

            _recoveryDueAt.Value = null;
            return true;
        });

        await RecoverUnfinishedJobsAsync();
    }

    public async Task<int> RecoverUnfinishedJobsAsync()
    {
        // Cheap read first so a normal activation does not cause a journal write.
        var anyOrphan = await _store.ReadAsync(() => _jobs.Values.Any(IsOrphaned));
        if (!anyOrphan)
        {
            await EnsureRecoveryCheckScheduledAsync(); // keeps the chain going while jobs are processing
            return 0;
        }

        var owner = _liveness.CurrentOwnerId;
        var claimed = new List<(string JobId, JobRequest Request, long Epoch)>();

        // Claim orphans under the write gate so concurrent recoveries cannot resubmit the same job twice:
        // once claimed, the job is owned by this silo and tracked by this activation, so IsOrphaned turns false.
        await _store.UpdateAsync(() =>
        {
            foreach (var job in _jobs.Values.Where(IsOrphaned).ToList())
            {
                if (job.Attempts >= MaxRecoveryAttempts)
                {
                    _jobs[job.JobId] = job with
                    {
                        Status = JobStatus.Failed,
                        Error = $"Abandoned: owner was lost {job.Attempts} times.",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    _finishedCount.Value++;
                    LogJobAbandoned(job.JobId, job.Attempts);
                    continue;
                }

                // Recovery restarts the job from scratch under a new epoch. Partial data from the lost run is
                // dropped, and anything that run's workers still send later is ignored because its epoch is old.
                var epoch = NextEpoch(atLeast: job.Epoch);
                _jobs[job.JobId] = job with
                {
                    OwnerSilo = owner,
                    Attempts = job.Attempts + 1,
                    Epoch = epoch,
                    LastSequence = 0,
                    LastProgressSequence = 0,
                    ResultsReceived = 0,
                    PercentComplete = 0,
                    Output = null,
                    Error = null,
                    Message = "Recovered after owner loss",
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                Track(job.JobId, epoch);
                claimed.Add((job.JobId, job.Request!, epoch));
            }
        });

        var restarted = 0;
        foreach (var (jobId, request, epoch) in claimed)
        {
            LogJobRecovered(jobId);
            try
            {
                await EnqueueAsync(jobId, request, epoch);
                restarted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Do not strand the jobs claimed after this one, and do not leave this one Processing under a live
                // owner: fail it and carry on with the rest.
                LogEnqueueFailed(jobId, ex);
                await _store.FailRunAsync(jobId, epoch, $"Recovery could not restart the job: {ex.Message}");
                Untrack(jobId, epoch);
            }
        }

        await EnsureRecoveryCheckScheduledAsync();
        return restarted;
    }

    /// <summary>
    /// While any job is <see cref="JobStatus.Processing"/>, makes sure exactly one recovery check is scheduled.
    /// <see cref="_recoveryDueAt"/> (journaled) records when the pending check is due; a check that is more than
    /// one period overdue is presumed lost (for example non-durable job storage) and is scheduled again.
    /// Nothing is scheduled when no job is processing, which ends the chain.
    /// Best effort: a scheduling failure is logged instead of failing job submission.
    /// </summary>
    private async Task EnsureRecoveryCheckScheduledAsync()
    {
        await _scheduleGate.WaitAsync();
        try
        {
            var (anyProcessing, dueAt) = await _store.ReadAsync(
                () => (_jobs.Values.Any(j => j.Status is JobStatus.Processing), _recoveryDueAt.Value));
            if (!anyProcessing)
                return;

            var now = DateTimeOffset.UtcNow;
            var period = _options.CheckPeriod;
            if (dueAt is { } pending && pending > now - period)
                return; // already scheduled and not overdue

            var next = now + period;
            await _scheduler.ScheduleJobAsync(
                new ScheduleJobRequest { Target = this.GetGrainId(), JobName = RecoveryJobName, DueTime = next },
                CancellationToken.None);
            await _store.UpdateAsync(() => _recoveryDueAt.Value = next);
            LogRecoveryCheckScheduled(next);
        }
        catch (Exception ex)
        {
            LogRecoveryCheckFailed(ex);
        }
        finally
        {
            _scheduleGate.Release();
        }
    }

    public Task<DurableJobRecord?> GetJobAsync(string jobId) =>
        _store.ReadAsync(() => _jobs.TryGetValue(jobId, out var record) ? record : null);

    public Task<IReadOnlyList<DurableJobRecord>> GetJobsAsync() =>
        _store.ReadAsync<IReadOnlyList<DurableJobRecord>>(() => _jobs.Values.OrderBy(j => j.JobId).ToList());

    public Task<int> GetFinishedCountAsync() => _store.ReadAsync(() => _finishedCount.Value);

    public async Task<bool> CancelAsync(string jobId)
    {
        // Record the cancellation first and stop tracking afterwards: an owned but untracked Processing job looks lost
        // to recovery, which would restart it in the gap. Once the job is terminal, late data from its run (epoch fencing)
        // and the recovery scan (only Processing jobs) both leave it alone.
        if (await _store.CancelRunAsync(jobId) is not { } epoch)
            return false;

        if (_inFlight.TryGetValue(jobId, out var run) && run.Epoch == epoch)
            _monitor.TryCancelRequest(run.RequestId);   // frees a request that is still queued; a running handler cannot be stopped from here

        Untrack(jobId, epoch);
        LogJobCancelled(jobId, epoch);
        return true;
    }

    // ── The mailbox pump ──────────────────────────────────────────────────────

    /// <summary>
    /// Drains the mailbox. It is started from inside the activation, so every continuation after an <c>await</c> runs on the
    /// grain's scheduler as a normal turn: single-threaded, interleaving with requests only at await points (the store's gate
    /// keeps "mutate and persist" atomic). It ends when the mailbox is completed on deactivation, after applying what is left.
    /// </summary>
    private async Task PumpAsync()
    {
        var reader = _mailbox.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var message))
            {
                try
                {
                    await ApplyAsync(message);
                }
                catch (Exception ex)
                {
                    LogMailboxMessageFailed(message.JobId, ex);   // one bad message must not stop the pump
                }
            }
        }
    }

    private async Task ApplyAsync(MailboxMessage message)
    {
        switch (message)
        {
            case ProgressMessage p:
                _store.ApplyProgress(p.JobId, p.Epoch, p.Sequence, p.PercentComplete, p.Message);
                break;

            case ResultMessage r:
                var applied = await _store.ApplyResultAsync(r.JobId, r.Epoch, r.Sequence, r.Status, r.Output, r.Error);
                if (applied && r.IsFinal)
                    Untrack(r.JobId, r.Epoch);
                break;
        }
    }

    // ── Runs in flight and keep-alive ─────────────────────────────────────────

    private void Track(string jobId, long epoch)
    {
        _inFlight[jobId] = new InFlightRun(
            epoch, RunRequestId(jobId, epoch), RunDeadline(DateTimeOffset.UtcNow, _options.MaxRunDuration));
        UpdateKeepAlive();
    }

    private void Untrack(string jobId, long epoch)
    {
        if (_inFlight.TryGetValue(jobId, out var run) && run.Epoch == epoch)
        {
            _inFlight.Remove(jobId);
            UpdateKeepAlive();
        }
    }

    /// <summary>
    /// Keeps the activation in memory while it has runs in flight (their results can only be delivered here) and lifts
    /// the delay when the last run ends. A timer renews the delay, so a job may run far longer than the slice.
    /// </summary>
    private void UpdateKeepAlive()
    {
        if (_inFlight.Count > 0)
        {
            this.DelayDeactivation(_options.KeepAliveSlice);
            var tick = SweepPeriod(_options.KeepAliveSlice, _options.MaxRunDuration);
            _keepAliveTimer ??= this.RegisterGrainTimer(
                async _ => await KeepAliveTickAsync(),
                new GrainTimerCreationOptions { DueTime = tick, Period = tick, Interleave = true });
        }
        else
        {
            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;
            this.DelayDeactivation(TimeSpan.Zero);   // eligible for idle collection again
        }
    }

    /// <summary>Renews the keep-alive and enforces the run deadlines; one timer does both while any run is in flight.</summary>
    private async Task KeepAliveTickAsync()
    {
        try
        {
            if (_inFlight.Count > 0)
                this.DelayDeactivation(_options.KeepAliveSlice);

            await ExpireOverdueRunsAsync();
        }
        catch (Exception ex)
        {
            LogSweepFailed(ex);
        }
    }

    /// <summary>
    /// Fails runs that have not reported back by their deadline. The pool's own timeout only cancels a handler that honours its
    /// cancellation token; a handler that hangs never returns, so the pool never calls back and the job would stay
    /// <see cref="JobStatus.Processing"/> with the grain pinned for good. Whatever that run still sends later is ignored,
    /// because the run is now finished.
    /// </summary>
    private async Task ExpireOverdueRunsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var overdue = _inFlight.Where(kv => kv.Value.Deadline is { } d && d <= now).Select(kv => (JobId: kv.Key, Run: kv.Value)).ToList();

        foreach (var (jobId, run) in overdue)
        {
            _monitor.TryCancelRequest(run.RequestId);   // frees a request that is still queued; a running handler cannot be stopped from here

            // Record the failure first and stop tracking afterwards: an owned but untracked Processing job looks lost to recovery,
            // which would restart it in the gap.
            var failed = await _store.FailRunAsync(jobId, run.Epoch, $"Timed out: the run did not finish within {_options.MaxRunDuration}.");
            Untrack(jobId, run.Epoch);
            if (failed)
                LogRunTimedOut(jobId, run.Epoch);
        }
    }

    // ── Rules that are easy to get wrong, kept as small pure functions so they can be tested directly ────────

    /// <summary>
    /// The next epoch: always above every epoch this grain has handed out, and above <paramref name="atLeast"/> (the job's current
    /// one, for journals written before the counter existed). It comes from a per-grain counter, not from the job's own record,
    /// so an id whose record was pruned cannot start over at an epoch it already had, which would let late data of its first
    /// run be accepted by the new one.
    /// </summary>
    private long NextEpoch(long atLeast)
    {
        var epoch = Math.Max(_lastEpoch.Value, atLeast) + 1;
        _lastEpoch.Value = epoch;
        return epoch;
    }

    /// <summary>The limit that applies, or null when none is configured (null, zero or infinite).</summary>
    internal static TimeSpan? EffectiveMaxRun(TimeSpan? maxRunDuration) =>
        maxRunDuration is { } m && m > TimeSpan.Zero ? m : null;

    /// <summary>
    /// When the grain gives up on a run that started at <paramref name="start"/>: the limit plus an allowance (a quarter of it,
    /// between 250 ms and a minute), so a handler that honours cancellation is stopped by the pool, which reports the outcome,
    /// before the grain fails the run itself.
    /// </summary>
    internal static DateTimeOffset? RunDeadline(DateTimeOffset start, TimeSpan? maxRunDuration)
    {
        if (EffectiveMaxRun(maxRunDuration) is not { } max)
            return null;

        var allowance = TimeSpan.FromTicks(Math.Clamp(max.Ticks / 4, TimeSpan.FromMilliseconds(250).Ticks, TimeSpan.FromMinutes(1).Ticks));
        return start + max + allowance;
    }

    /// <summary>How often the keep-alive is renewed and deadlines are checked: a third of the slice, or sooner for a short limit.</summary>
    internal static TimeSpan SweepPeriod(TimeSpan keepAliveSlice, TimeSpan? maxRunDuration)
    {
        var period = keepAliveSlice / 3;
        if (EffectiveMaxRun(maxRunDuration) is { } max)
        {
            var forDeadlines = TimeSpan.FromTicks(Math.Max(max.Ticks / 4, TimeSpan.FromMilliseconds(100).Ticks));
            period = period < forDeadlines ? period : forDeadlines;
        }

        return period;
    }

    /// <summary>
    /// The pool identifies requests by id and rejects a second pending request with the same id, so the id must be unique across
    /// grains, jobs and runs: a job id is only unique within its grain, and a recovered run can be enqueued while the
    /// run it replaces is still pending somewhere.
    /// </summary>
    internal static string RunRequestId(string ownerKey, string jobId, long epoch) => $"{ownerKey}/{jobId}#{epoch}";

    private string RunRequestId(string jobId, long epoch) => RunRequestId(this.GetPrimaryKeyString(), jobId, epoch);

    /// <summary>Throws <see cref="ArgumentException"/> for a request that must not create any state.</summary>
    internal static void ValidateSubmission(string ownerKey, string jobId, JobRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (jobId.Length > MaxJobIdLength)
            throw new ArgumentException($"The job id is longer than {MaxJobIdLength} characters.", nameof(jobId));
        if (jobId.Any(char.IsControl) || ownerKey.Any(char.IsControl))
            throw new ArgumentException("The job id and the owner key must not contain control characters.", nameof(jobId));

        // "<owner>/<job>#<epoch>": 20 is the most digits a long epoch can have.
        if (RunRequestId(ownerKey, jobId, 0).Length + 20 > MaxRequestIdLength)
            throw new ArgumentException($"The owner key and job id together are too long (the pool allows {MaxRequestIdLength} characters per request).", nameof(jobId));

        ArgumentNullException.ThrowIfNull(request);
        if (request.PartialResults is < 0 or > MaxPartialResults)
            throw new ArgumentOutOfRangeException(
                nameof(request), request.PartialResults, $"PartialResults must be between 0 and {MaxPartialResults}.");
    }

    /// <summary>
    /// A Processing job that nobody can deliver results for, so it must be restarted:
    /// <list type="bullet">
    ///   <item>it has no owner: the activation that ran it was deactivated and released it;</item>
    ///   <item>it is owned by this silo but not run by this activation: results only reach the activation that started the run
    ///         (a reactivated grain that finds such a job missed the clean release);</item>
    ///   <item>its owner is another silo that is gone (dead or unknown) and the job has been quiet for at least
    ///         <paramref name="grace"/>. The grace period keeps a job whose owner is merely missing from this silo's membership view
    ///         (lag) or still shutting down from being resubmitted while it is actually still running.</item>
    /// </list>
    /// </summary>
    internal static bool IsOrphaned(
        DurableJobRecord job, IJobOwnerLiveness liveness, DateTimeOffset now, TimeSpan grace, bool trackedByThisActivation)
    {
        if (job.Status is not JobStatus.Processing || job.Request is null)
            return false;

        if (job.OwnerSilo is null)
            return true;

        if (job.OwnerSilo == liveness.CurrentOwnerId)
            return !trackedByThisActivation;

        return !liveness.IsAlive(job.OwnerSilo) && now - job.UpdatedAt >= grace;
    }

    private bool IsOrphaned(DurableJobRecord job) =>
        IsOrphaned(
            job, _liveness, DateTimeOffset.UtcNow, _options.OrphanGracePeriod,
            trackedByThisActivation: _inFlight.TryGetValue(job.JobId, out var run) && run.Epoch == job.Epoch);

    /// <summary>
    /// A firing check clears the pending-check marker only if the marker is the one it was scheduled for. A duplicate
    /// of an older check (at-least-once delivery) fires after a newer one was scheduled; the marker is then about one
    /// period later than the fired job's due time and must be left alone, or two chains would run side by side.
    /// </summary>
    internal static bool ShouldClearRecoveryMarker(DateTimeOffset? marker, DateTimeOffset firedJobDue, TimeSpan period) =>
        marker is { } m && m <= firedJobDue + period / 2;

    /// <summary>Removes the oldest finished jobs so that at most <see cref="JobRecoveryOptions.MaxRetainedFinishedJobs"/> remain.</summary>
    private void PruneFinishedJobs(string keepJobId)
    {
        var max = Math.Max(0, _options.MaxRetainedFinishedJobs);
        var finished = _jobs.Values.Where(j => j.IsTerminal && j.JobId != keepJobId).OrderBy(j => j.UpdatedAt).ToList();
        foreach (var old in finished.Take(Math.Max(0, finished.Count - max)))
            _jobs.Remove(old.JobId);
    }

    private async Task EnqueueAsync(string jobId, JobRequest request, long epoch)
    {
        var requestId = RunRequestId(jobId, epoch);

        // Captured now: this activation's mailbox. If the activation is gone by the time a worker writes, the write is
        // refused and the data dropped (the run was released and recovery restarts the job).
        var mailbox = _mailbox.Writer;

        // Progress and results have separate sequence spaces. The mailbox keeps a worker's messages in order; the
        // sequence numbers still drop a message that is delivered twice (at-least-once) and data from superseded runs.
        long progressSequence = 0;
        long resultSequence = 0;

        void Post(MailboxMessage message)
        {
            if (!mailbox.TryWrite(message))
                LogResultDropped(message.JobId, message.GetType().Name);
        }

        RequestProgressReporter reporter = (pct, msg, delta) =>
        {
            Post(new ProgressMessage(jobId, epoch, Interlocked.Increment(ref progressSequence), pct, msg));

            if (delta is PartialResultDelta partial)
            {
                var seq = Interlocked.Increment(ref resultSequence);
                var result = new ResultMessage(jobId, epoch, seq, JobStatus.Processing, partial.Output, null);
                Post(result);
                if (partial.Redeliver)
                    Post(result);   // simulated at-least-once delivery: same sequence number, applied once
            }
        };

        // A job that asks for partial results runs through the staged handler; everything else uses the plain one.
        RequestContext context = request.PartialResults > 0
            ? new RequestContext<StagedJobRequest>(
                requestId,
                new StagedJobRequest(request.Payload, request.PartialResults, request.Priority),
                Priority: request.Priority,
                OnProgress: reporter)
            { PartitionKey = this.GetPrimaryKeyString(), Timeout = EffectiveMaxRun(_options.MaxRunDuration) }
            : new RequestContext<JobRequest>(requestId, request, Priority: request.Priority, OnProgress: reporter)
            { PartitionKey = this.GetPrimaryKeyString(), Timeout = EffectiveMaxRun(_options.MaxRunDuration) };

        await _pool.EnqueueAsync(context, result =>
        {
            // Runs on a pool worker, after every progress and partial result of this run: only post to the mailbox.
            var seq = Interlocked.Increment(ref resultSequence);
            var (status, output, error) = result.Error switch
            {
                OperationCanceledException => (JobStatus.Cancelled, (string?)null, (string?)null),
                not null => (JobStatus.Failed, null, result.Error.Message),
                _ => (JobStatus.Completed, OutputText(result), null),
            };

            Post(new ResultMessage(jobId, epoch, seq, status, output, error));
            return Task.CompletedTask;
        });
    }

    private static string? OutputText(RequestResult result) =>
        result.TypedOutput is TextJobOutput text ? text.Text : result.Output;

    [LoggerMessage(1, LogLevel.Warning, "Durable job {JobId} is already processing or was already submitted with this idempotency key; ignoring")]
    private partial void LogDuplicateSubmit(string jobId);

    [LoggerMessage(2, LogLevel.Information, "Durable job {JobId} enqueued; results return through the grain's in-process mailbox")]
    private partial void LogJobEnqueued(string jobId);

    [LoggerMessage(3, LogLevel.Warning, "Durable job {JobId} was unfinished and nobody can deliver its results; resubmitting")]
    private partial void LogJobRecovered(string jobId);

    [LoggerMessage(4, LogLevel.Error, "Durable job {JobId} abandoned after {Attempts} lost owners")]
    private partial void LogJobAbandoned(string jobId, int attempts);

    [LoggerMessage(5, LogLevel.Debug, "Recovery check scheduled for {DueAt:O}")]
    private partial void LogRecoveryCheckScheduled(DateTimeOffset dueAt);

    [LoggerMessage(6, LogLevel.Warning, "Could not schedule the recovery check; orphaned jobs will only be recovered on activation")]
    private partial void LogRecoveryCheckFailed(Exception exception);

    [LoggerMessage(7, LogLevel.Debug, "Recovery check fired (dequeue count {DequeueCount})")]
    private partial void LogRecoveryCheckFired(int dequeueCount);

    [LoggerMessage(9, LogLevel.Error, "The pool refused durable job {JobId}; failing the run")]
    private partial void LogEnqueueFailed(string jobId, Exception exception);

    [LoggerMessage(12, LogLevel.Warning, "A {MessageType} for durable job {JobId} reached a mailbox that is closed (the activation is gone); dropped, the job is recovered by rerunning it")]
    private partial void LogResultDropped(string jobId, string messageType);

    [LoggerMessage(13, LogLevel.Error, "Applying a mailbox message for durable job {JobId} failed")]
    private partial void LogMailboxMessageFailed(string jobId, Exception exception);

    [LoggerMessage(14, LogLevel.Warning, "Activation deactivating ({Reason}): released {Count} run(s) in flight; recovery will restart them")]
    private partial void LogRunsReleased(int count, string reason);

    [LoggerMessage(16, LogLevel.Warning, "Durable job {JobId} (epoch {Epoch}) did not finish in time and was failed; whatever its worker sends later is ignored")]
    private partial void LogRunTimedOut(string jobId, long epoch);

    [LoggerMessage(18, LogLevel.Information, "Durable job {JobId} (epoch {Epoch}) was cancelled; whatever its worker sends later is ignored")]
    private partial void LogJobCancelled(string jobId, long epoch);

    [LoggerMessage(17, LogLevel.Error, "Checking the run deadlines failed")]
    private partial void LogSweepFailed(Exception exception);

    [LoggerMessage(15, LogLevel.Error, "Could not persist the release of runs in flight while deactivating; recovery will find them through their dead owner")]
    private partial void LogReleaseFailed(Exception exception);
}
