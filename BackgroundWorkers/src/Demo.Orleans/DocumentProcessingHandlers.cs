namespace OrleansSample;

/// <summary>
/// Handles <see cref="ExtractContentRequest"/> (step 1 of the document-processing pipeline).
///
/// Simulates parsing a raw document through four phases:
/// reading, tokenising, cleaning, and summarising.
/// Produces <see cref="ExtractedContentOutput"/> containing a word count and a
/// synthetic raw-text token passed to step 2.
/// </summary>
internal sealed partial class ExtractContentHandler(ILogger<ExtractContentHandler> logger)
    : IRequestHandler<ExtractContentRequest>
{
    private static readonly (string Phase, int Steps, int MinMs, int MaxMs)[] Phases =
    [
        ("Reading source",   2, 30,  80),
        ("Tokenising",       3, 25,  70),
        ("Cleaning tokens",  3, 20,  60),
        ("Summarising",      2, 30,  80),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<ExtractContentRequest> context, CancellationToken cancellationToken)
    {
        var pipelineId = context.Data.PipelineId;
        var totalSteps = Phases.Sum(p => p.Steps); // 10
        var step = 0;

        foreach (var (phase, count, minMs, maxMs) in Phases)
        {
            for (int i = 1; i <= count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);
                FaultInjector.MaybeThrow(step, pipelineId);
                step++;
                var pct = step * 100 / totalSteps;
                var msg = count > 1 ? $"{phase} ({i}/{count})" : phase;
                context.OnProgress?.Invoke(pct, msg, new JobStepProgressDelta(step, totalSteps));
            }
        }

        var wordCount = Random.Shared.Next(200, 2000);
        var rawText = $"[extracted:{pipelineId}] {wordCount} words";
        var output = new ExtractedContentOutput(rawText, wordCount);

        LogExtracted(pipelineId, wordCount);
        return new RequestResult(context.RequestId, Success: true,
            Output: $"Extracted {wordCount} words from pipeline {pipelineId}",
            TypedOutput: output);
    }

    [LoggerMessage(1, LogLevel.Information, "Extract [{PipelineId}]: {WordCount} words extracted")]
    private partial void LogExtracted(string pipelineId, int wordCount);
}

/// <summary>
/// Handles <see cref="TransformContentRequest"/> (step 2 of the document-processing pipeline).
///
/// Receives the raw text and word count from step 1, extracts keywords,
/// and computes a sentiment score.  Produces <see cref="TransformedContentOutput"/>.
/// </summary>
internal sealed partial class TransformContentHandler(ILogger<TransformContentHandler> logger)
    : IRequestHandler<TransformContentRequest>
{
    private static readonly (string Phase, int Steps, int MinMs, int MaxMs)[] Phases =
    [
        ("Parsing raw text",    2, 30,  80),
        ("Extracting keywords", 4, 35,  90),
        ("Scoring sentiment",   2, 25,  70),
        ("Normalising",         2, 20,  60),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<TransformContentRequest> context, CancellationToken cancellationToken)
    {
        var pipelineId = context.Data.PipelineId;
        var wordCount = context.Data.WordCount;
        var totalSteps = Phases.Sum(p => p.Steps); // 10
        var step = 0;

        foreach (var (phase, count, minMs, maxMs) in Phases)
        {
            for (int i = 1; i <= count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);
                FaultInjector.MaybeThrow(step, pipelineId);
                step++;
                var pct = step * 100 / totalSteps;
                var msg = count > 1 ? $"{phase} ({i}/{count})" : phase;
                context.OnProgress?.Invoke(pct, msg, new JobStepProgressDelta(step, totalSteps));
            }
        }

        var keywordCount = Math.Max(3, wordCount / 50);
        var keywords = Enumerable.Range(1, keywordCount)
            .Select(k => $"kw-{k}")
            .ToArray();
        var sentiment = Math.Round(Random.Shared.NextDouble() * 2 - 1, 3); // -1..1
        var output = new TransformedContentOutput(keywords, sentiment);

        LogTransformed(pipelineId, keywordCount, sentiment);
        return new RequestResult(context.RequestId, Success: true,
            Output: $"Transformed pipeline {pipelineId}: {keywordCount} keywords, sentiment={sentiment:+0.000;-0.000}",
            TypedOutput: output);
    }

    [LoggerMessage(1, LogLevel.Information, "Transform [{PipelineId}]: {KeywordCount} keywords, sentiment={Sentiment:F3}")]
    private partial void LogTransformed(string pipelineId, int keywordCount, double sentiment);
}

/// <summary>
/// Handles <see cref="IndexContentRequest"/> (step 3 of the document-processing pipeline).
///
/// Receives the keywords and sentiment score from step 2, writes them to a simulated
/// index, and returns an <see cref="IndexedContentOutput"/> with the assigned index ID
/// and tag count.
/// </summary>
internal sealed partial class IndexContentHandler(ILogger<IndexContentHandler> logger)
    : IRequestHandler<IndexContentRequest>
{
    private static readonly (string Phase, int Steps, int MinMs, int MaxMs)[] Phases =
    [
        ("Preparing index entry", 2, 25,  70),
        ("Writing tags",          3, 30,  80),
        ("Committing index",      2, 35,  90),
        ("Verifying entry",       1, 20,  50),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<IndexContentRequest> context, CancellationToken cancellationToken)
    {
        var pipelineId = context.Data.PipelineId;
        var keywords = context.Data.Keywords;
        var totalSteps = Phases.Sum(p => p.Steps); // 8
        var step = 0;

        foreach (var (phase, count, minMs, maxMs) in Phases)
        {
            for (int i = 1; i <= count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);
                FaultInjector.MaybeThrow(step, pipelineId);
                step++;
                var pct = step * 100 / totalSteps;
                var msg = count > 1 ? $"{phase} ({i}/{count})" : phase;
                context.OnProgress?.Invoke(pct, msg, new JobStepProgressDelta(step, totalSteps));
            }
        }

        var indexId = $"idx-{pipelineId[..Math.Min(8, pipelineId.Length)]}-{Random.Shared.Next(1000, 9999)}";
        var output = new IndexedContentOutput(indexId, keywords.Count);

        LogIndexed(pipelineId, indexId, keywords.Count);
        return new RequestResult(context.RequestId, Success: true,
            Output: $"Indexed pipeline {pipelineId}: indexId={indexId}, {keywords.Count} tags",
            TypedOutput: output);
    }

    [LoggerMessage(1, LogLevel.Information, "Index [{PipelineId}]: assigned indexId={IndexId}, {TagCount} tags")]
    private partial void LogIndexed(string pipelineId, string indexId, int tagCount);
}
