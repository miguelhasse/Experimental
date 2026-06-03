namespace OrleansSample;

// ── Requests ─────────────────────────────────────────────────────────────────

/// <summary>
/// Step 1 of the document-processing pipeline — simulates parsing a raw document.
/// Produces <see cref="ExtractedContentOutput"/> consumed by step 2.
/// </summary>
[GenerateSerializer]
public record ExtractContentRequest(
    [property: Id(0)] string PipelineId) : IJobRequest
{
    public RequestPriority Priority => RequestPriority.High;
}

/// <summary>
/// Step 2 of the document-processing pipeline — enriches the extracted text.
/// Receives the output of step 1 and produces <see cref="TransformedContentOutput"/> for step 3.
/// </summary>
[GenerateSerializer]
public record TransformContentRequest(
    [property: Id(0)] string PipelineId,
    [property: Id(1)] string RawText,
    [property: Id(2)] int WordCount) : IJobRequest
{
    public RequestPriority Priority => RequestPriority.Normal;
}

/// <summary>
/// Step 3 of the document-processing pipeline — indexes the analysed content.
/// Receives the output of step 2 and produces the final <see cref="IndexedContentOutput"/>.
/// </summary>
[GenerateSerializer]
public record IndexContentRequest(
    [property: Id(0)] string PipelineId,
    [property: Id(1)] IReadOnlyList<string> Keywords,
    [property: Id(2)] double SentimentScore) : IJobRequest
{
    public RequestPriority Priority => RequestPriority.Normal;
}

// ── Typed outputs (JobOutput subtypes) ───────────────────────────────────────

/// <summary>Output of step 1 (extract). Carries raw text and word count.</summary>
[GenerateSerializer]
public sealed record ExtractedContentOutput(
    [property: Id(0)] string RawText,
    [property: Id(1)] int WordCount) : JobOutput;

/// <summary>Output of step 2 (transform). Carries keywords and sentiment score.</summary>
[GenerateSerializer]
public sealed record TransformedContentOutput(
    [property: Id(0)] IReadOnlyList<string> Keywords,
    [property: Id(1)] double SentimentScore) : JobOutput;

/// <summary>Output of step 3 (index). Carries the assigned index ID and tag count.</summary>
[GenerateSerializer]
public sealed record IndexedContentOutput(
    [property: Id(0)] string IndexId,
    [property: Id(1)] int TagCount) : JobOutput;

// ── Snapshot ──────────────────────────────────────────────────────────────────

/// <summary>
/// Point-in-time view of all three pipeline steps returned by
/// <see cref="IDocumentProcessingGrain.GetSummaryAsync"/>.
/// </summary>
[GenerateSerializer]
public record DocumentProcessingSnapshot(
    [property: Id(0)] string PipelineId,
    [property: Id(1)] JobStatus Step1Status,
    [property: Id(2)] JobStatus Step2Status,
    [property: Id(3)] JobStatus Step3Status,
    [property: Id(4)] JobProgressSnapshot? Step1Progress = null,
    [property: Id(5)] JobProgressSnapshot? Step2Progress = null,
    [property: Id(6)] JobProgressSnapshot? Step3Progress = null,
    [property: Id(7)] ExtractedContentOutput? Step1Output = null,
    [property: Id(8)] TransformedContentOutput? Step2Output = null,
    [property: Id(9)] IndexedContentOutput? Step3Output = null);
