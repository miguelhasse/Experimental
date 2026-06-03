namespace OrleansSample;

/// <summary>Handles <see cref="JobRequest"/> (simple string-payload jobs).</summary>
internal sealed partial class JobRequestHandler(ILogger<JobRequestHandler> logger) : IRequestHandler<JobRequest>
{
    // Named phases: (label, sub-step count, min delay ms, max delay ms)
    private static readonly (string Name, int SubSteps, int MinMs, int MaxMs)[] Phases =
    [
        ("Initializing",       2, 30,  80),
        ("Loading data",       3, 40, 100),
        ("Processing",         6, 50, 130),
        ("Validating results", 3, 30,  90),
        ("Finalizing",         2, 25,  70),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<JobRequest> context,
        CancellationToken cancellationToken)
    {
        var category = context.Data.Category ?? "default";
        LogDispatchingJob(category, context.RequestId, context.Data.Payload);

        int totalSteps = Phases.Sum(p => p.SubSteps); // 16
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

        var output = $"[{category}] Job {context.RequestId} done at {DateTimeOffset.UtcNow:O}";
        return new RequestResult(context.RequestId, Success: true, Output: output, TypedOutput: new TextJobOutput(output));
    }

    [LoggerMessage(1, LogLevel.Information, "[{Category}] Dispatching job {JobId}: {Payload}")]
    private partial void LogDispatchingJob(string category, string jobId, string? payload);
}

/// <summary>Handles <see cref="BatchJobRequest"/> (multi-item batch jobs).</summary>
internal sealed partial class BatchJobRequestHandler(ILogger<BatchJobRequestHandler> logger)
    : IRequestHandler<BatchJobRequest>
{
    private static readonly string[] SubOps = ["Validating", "Processing", "Committing"];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<BatchJobRequest> context,
        CancellationToken cancellationToken)
    {
        var category = context.Data.Category ?? "batch";
        var items = context.Data.Items;
        LogProcessingBatch(category, context.RequestId, items.Count);

        int totalSteps = items.Count * SubOps.Length;

        for (int i = 0; i < items.Count; i++)
        {
            for (int s = 0; s < SubOps.Length; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(Random.Shared.Next(15, 60), cancellationToken);

                int overallStep = i * SubOps.Length + s + 1;
                FaultInjector.MaybeThrow(overallStep, context.RequestId);
                int pct = overallStep * 100 / totalSteps;
                var msg = $"{SubOps[s]} item {i + 1}/{items.Count}: {items[i]}";
                context.OnProgress?.Invoke(pct, msg,
                    new BatchItemProgressDelta(i + 1, items.Count, items[i]));
            }
        }

        var output = $"[{category}] Batch {context.RequestId}: {items.Count} items done at {DateTimeOffset.UtcNow:O}";
        return new RequestResult(context.RequestId, Success: true, Output: output, TypedOutput: new TextJobOutput(output));
    }

    [LoggerMessage(1, LogLevel.Information, "[{Category}] Processing batch job {JobId} with {Count} items")]
    private partial void LogProcessingBatch(string category, string jobId, int count);
}

/// <summary>Handles <see cref="ScheduledJobRequest"/> (deferred/low-priority jobs).</summary>
internal sealed partial class ScheduledJobRequestHandler(ILogger<ScheduledJobRequestHandler> logger)
    : IRequestHandler<ScheduledJobRequest>
{
    private static readonly (string Phase, int SubSteps, int MinMs, int MaxMs)[] ScheduledPhases =
    [
        ("validate-schedule",  2, 20,  60),
        ("acquire-resources",  2, 30,  80),
        ("execute",            5, 40, 120),
        ("post-process",       3, 25,  70),
        ("cleanup",            1, 15,  40),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<ScheduledJobRequest> context,
        CancellationToken cancellationToken)
    {
        var lag = DateTimeOffset.UtcNow - context.Data.ScheduledAt;
        LogScheduledJobRunning(context.RequestId, lag.TotalMilliseconds);

        int totalSteps = ScheduledPhases.Sum(p => p.SubSteps); // 13
        int step = 0;

        foreach (var (phase, subSteps, minMs, maxMs) in ScheduledPhases)
        {
            for (int s = 1; s <= subSteps; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);
                step++;
                FaultInjector.MaybeThrow(step, context.RequestId);
                int pct = step * 100 / totalSteps;
                var label = subSteps > 1 ? $"{phase} ({s}/{subSteps})" : phase;
                context.OnProgress?.Invoke(pct, label, new ScheduledJobProgressDelta(phase));
            }
        }

        var output = $"[scheduled] Job {context.RequestId} completed at {DateTimeOffset.UtcNow:O} " +
                     $"(was scheduled for {context.Data.ScheduledAt:O})";
        return new RequestResult(context.RequestId, Success: true, Output: output, TypedOutput: new TextJobOutput(output));
    }

    [LoggerMessage(1, LogLevel.Information, "[scheduled] Job {JobId} running {Lag:F0}ms after scheduled time")]
    private partial void LogScheduledJobRunning(string jobId, double lag);
}

/// <summary>
/// Injects random faults into sample request handlers to exercise the failure path.
/// Not for production use.
/// </summary>
internal static class FaultInjector
{
    // ~1% chance per step for single-job handlers.
    private const double FaultProbability = 0.01;

    // ~0.2% per item for batch handlers — ~18% chance of ≥1 failure in a 100-item batch.
    private const double BatchItemFaultProbability = 0.002;

    private static readonly Func<string, Exception>[] FaultFactories =
    [
        id => new InvalidOperationException($"Unexpected state encountered processing job {id}."),
        id => new TimeoutException($"Operation timed out while processing job {id}."),
        id => new IOException($"I/O error reading data for job {id}."),
        id => new ApplicationException($"Downstream service returned an error for job {id}."),
    ];

    /// <summary>
    /// Randomly throws one of several realistic exceptions (~1% per call).
    /// Call at each processing step to simulate transient failures.
    /// </summary>
    public static void MaybeThrow(int step, string jobId)
    {
        if (Random.Shared.NextDouble() >= FaultProbability) return;

        var factory = FaultFactories[Random.Shared.Next(FaultFactories.Length)];
        throw factory(jobId);
    }

    /// <summary>
    /// Low-rate fault injection (~0.2% per call) for batch item handlers.
    /// Apply once per item (at the final step) to keep per-batch failure rates low but observable.
    /// </summary>
    public static void MaybeThrowLowRate(int step, string jobId)
    {
        if (Random.Shared.NextDouble() >= BatchItemFaultProbability) return;

        var factory = FaultFactories[step % FaultFactories.Length];
        throw factory(jobId);
    }
}