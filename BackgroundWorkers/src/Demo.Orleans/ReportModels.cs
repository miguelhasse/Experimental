namespace OrleansSample;

/// <summary>
/// Request dispatched by <see cref="IReportGrain.GenerateAsync"/>.
/// Simulates generating report content (e.g. querying data sources, building the report body).
/// </summary>
[GenerateSerializer]
public record GenerateReportRequest(
    [property: Id(0)] string ReportId) : IJobRequest
{
    public RequestPriority Priority => RequestPriority.High;
}

/// <summary>
/// Request dispatched by <see cref="IReportGrain.ReviewAsync"/>.
/// Simulates reviewing / quality-checking the generated report.
/// </summary>
[GenerateSerializer]
public record ReviewReportRequest(
    [property: Id(0)] string ReportId) : IJobRequest
{
    public RequestPriority Priority => RequestPriority.Normal;
}

/// <summary>
/// Request dispatched by <see cref="IReportGrain.PublishAsync"/>.
/// Simulates publishing the reviewed report to an output target.
/// </summary>
[GenerateSerializer]
public record PublishReportRequest(
    [property: Id(0)] string ReportId) : IJobRequest
{
    public RequestPriority Priority => RequestPriority.Low;
}

/// <summary>
/// Snapshot of all three operation statuses and progress for a report, returned by
/// <see cref="IReportGrain.GetSummaryAsync"/>.
/// </summary>
[GenerateSerializer]
public record ReportSummary(
    [property: Id(0)] string ReportId,
    [property: Id(1)] JobStatus GenerateStatus,
    [property: Id(2)] JobStatus ReviewStatus,
    [property: Id(3)] JobStatus PublishStatus,
    [property: Id(4)] JobProgressSnapshot? GenerateProgress = null,
    [property: Id(5)] JobProgressSnapshot? ReviewProgress = null,
    [property: Id(6)] JobProgressSnapshot? PublishProgress = null);
