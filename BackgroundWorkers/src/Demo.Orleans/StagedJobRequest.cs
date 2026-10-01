namespace OrleansSample;

/// <summary>
/// Work for the durable sample that produces <paramref name="Stages"/> partial results before its final result,
/// to simulate a job that pushes data back to its grain several times while it runs.
/// In-process only (not serialised): <see cref="DurableJobGrain"/> builds it from a <see cref="JobRequest"/>.
/// </summary>
internal sealed record StagedJobRequest(string Payload, int Stages, RequestPriority Priority);

/// <summary>
/// Progress delta that carries a partial <em>result</em>, not just a percentage. <see cref="DurableJobGrain"/> turns it
/// into a <see cref="ResultMessage"/> posted to its mailbox.
/// </summary>
/// <param name="Redeliver">Simulates at-least-once delivery: the same call is sent twice with the same sequence number.</param>
internal sealed record PartialResultDelta(int Stage, int TotalStages, string Output, bool Redeliver);

/// <summary>Handles <see cref="StagedJobRequest"/>: emits one partial result per stage, then a final result.</summary>
internal sealed class StagedJobRequestHandler : IRequestHandler<StagedJobRequest>
{
    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<StagedJobRequest> context,
        CancellationToken cancellationToken)
    {
        var stages = Math.Max(1, context.Data.Stages);

        for (var stage = 1; stage <= stages; stage++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(Random.Shared.Next(100, 250), cancellationToken);
            FaultInjector.MaybeThrow(stage, context.RequestId);

            var partial = $"stage {stage}/{stages} of '{context.Data.Payload}'";
            // Every second stage is delivered twice, like a retry after a timeout would.
            context.OnProgress?.Invoke(
                stage * 100 / stages,
                $"Stage {stage}/{stages}",
                new PartialResultDelta(stage, stages, partial, Redeliver: stage % 2 == 0));
        }

        var output = $"[staged] Job {context.RequestId}: {stages} partial results, done at {DateTimeOffset.UtcNow:O}";
        return new RequestResult(context.RequestId, Success: true, Output: output, TypedOutput: new TextJobOutput(output));
    }
}
