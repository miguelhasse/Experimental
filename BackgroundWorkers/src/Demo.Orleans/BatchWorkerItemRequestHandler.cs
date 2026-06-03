namespace OrleansSample;

/// <summary>
/// Handles a <see cref="BatchWorkerItemRequest"/> submitted by a <see cref="BatchWorkerGrain"/>.
/// Simulates a realistic multi-step workload with progress reporting and fault injection.
/// </summary>
public sealed partial class BatchWorkerItemRequestHandler(
    ILogger<BatchWorkerItemRequestHandler> logger)
    : IRequestHandler<BatchWorkerItemRequest>
{
    // (label, min delay ms, max delay ms)
    private static readonly (string Label, int MinMs, int MaxMs)[] Steps =
    [
        ("Preparing",  20,  60),
        ("Processing", 40, 120),
        ("Finalizing", 15,  45),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<BatchWorkerItemRequest> context,
        CancellationToken cancellationToken)
    {
        var req = context.Data;
        var workerKey = context.RequestId[..context.RequestId.LastIndexOf(":item-", StringComparison.Ordinal)];

        LogHandling(req.BatchId, req.ItemIndex, context.RequestId);

        using var activity = BatchGrainTelemetry.StartProcessItemActivity(context.RequestId, req.BatchId);

        int totalSteps = Steps.Length;

        for (int s = 0; s < totalSteps; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (label, minMs, maxMs) = Steps[s];
            await Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);

            // Record the processing step in the activity
            BatchGrainTelemetry.RecordProcessingStep(label);

            // Low-rate fault injection (~0.2%) at the final step only.
            if (s == totalSteps - 1)
                FaultInjector.MaybeThrowLowRate(s + 1, context.RequestId);

            int pct = (s + 1) * 100 / totalSteps;
            context.OnProgress?.Invoke(pct, label,
                new BatchProgressDelta(workerKey, s + 1, totalSteps));
        }

        var result = $"processed: {req.Data}";
        return new RequestResult(
            context.RequestId,
            Success: true,
            Output: result,
            TypedOutput: new BatchItemOutput(result));
    }

    [LoggerMessage(1, LogLevel.Debug, "Batch {BatchId}: handling item {ItemIndex} ({RequestId})")]
    private partial void LogHandling(string batchId, int itemIndex, string requestId);
}
