namespace OrleansSample;

/// <summary>
/// Handles <see cref="GenerateReportRequest"/> — simulates generating report content
/// across four phases: collecting data, building structure, applying formatting, and finalising.
/// </summary>
public sealed partial class GenerateReportHandler(ILogger<GenerateReportHandler> logger)
    : IRequestHandler<GenerateReportRequest>
{
    private static readonly (string Phase, int Steps)[] Phases =
    [
        ("Collecting data",     3),
        ("Building structure",  3),
        ("Applying formatting", 2),
        ("Finalising",          2),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<GenerateReportRequest> context, CancellationToken cancellationToken)
    {
        var reportId = context.Data.ReportId;
        var totalSteps = Phases.Sum(p => p.Steps);
        var step = 0;

        foreach (var (phase, count) in Phases)
        {
            for (int i = 1; i <= count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(30, 80)), cancellationToken);

                FaultInjector.MaybeThrow(step, reportId);

                step++;
                var pct = (int)Math.Round(step * 100.0 / totalSteps);
                context.OnProgress?.Invoke(pct, $"{phase} ({i}/{count})");
                LogStep(reportId, phase, i, count, pct);
            }
        }

        return new RequestResult(context.RequestId, Success: true,
            Output: $"Report {reportId} generated",
            TypedOutput: new TextJobOutput($"Report {reportId} generated"));
    }

    [LoggerMessage(1, LogLevel.Debug, "GenerateReport {ReportId}: {Phase} step {Step}/{Total} — {Pct}%")]
    private partial void LogStep(string reportId, string phase, int step, int total, int pct);
}

/// <summary>
/// Handles <see cref="ReviewReportRequest"/> — simulates reviewing a report
/// across three phases: validation, compliance check, and approval.
/// </summary>
public sealed partial class ReviewReportHandler(ILogger<ReviewReportHandler> logger)
    : IRequestHandler<ReviewReportRequest>
{
    private static readonly (string Phase, int Steps)[] Phases =
    [
        ("Validating content",  3),
        ("Compliance check",    3),
        ("Approval workflow",   2),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<ReviewReportRequest> context, CancellationToken cancellationToken)
    {
        var reportId = context.Data.ReportId;
        var totalSteps = Phases.Sum(p => p.Steps);
        var step = 0;

        foreach (var (phase, count) in Phases)
        {
            for (int i = 1; i <= count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(40, 100)), cancellationToken);

                FaultInjector.MaybeThrow(step, reportId);

                step++;
                var pct = (int)Math.Round(step * 100.0 / totalSteps);
                context.OnProgress?.Invoke(pct, $"{phase} ({i}/{count})");
                LogStep(reportId, phase, i, count, pct);
            }
        }

        return new RequestResult(context.RequestId, Success: true,
            Output: $"Report {reportId} reviewed",
            TypedOutput: new TextJobOutput($"Report {reportId} reviewed"));
    }

    [LoggerMessage(1, LogLevel.Debug, "ReviewReport {ReportId}: {Phase} step {Step}/{Total} — {Pct}%")]
    private partial void LogStep(string reportId, string phase, int step, int total, int pct);
}

/// <summary>
/// Handles <see cref="PublishReportRequest"/> — simulates publishing a report
/// across three phases: packaging, uploading, and notifying recipients.
/// </summary>
public sealed partial class PublishReportHandler(ILogger<PublishReportHandler> logger)
    : IRequestHandler<PublishReportRequest>
{
    private static readonly (string Phase, int Steps)[] Phases =
    [
        ("Packaging report",     2),
        ("Uploading to target",  3),
        ("Notifying recipients", 2),
    ];

    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<PublishReportRequest> context, CancellationToken cancellationToken)
    {
        var reportId = context.Data.ReportId;
        var totalSteps = Phases.Sum(p => p.Steps);
        var step = 0;

        foreach (var (phase, count) in Phases)
        {
            for (int i = 1; i <= count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(25, 70)), cancellationToken);

                FaultInjector.MaybeThrow(step, reportId);

                step++;
                var pct = (int)Math.Round(step * 100.0 / totalSteps);
                context.OnProgress?.Invoke(pct, $"{phase} ({i}/{count})");
                LogStep(reportId, phase, i, count, pct);
            }
        }

        return new RequestResult(context.RequestId, Success: true,
            Output: $"Report {reportId} published",
            TypedOutput: new TextJobOutput($"Report {reportId} published"));
    }

    [LoggerMessage(1, LogLevel.Debug, "PublishReport {ReportId}: {Phase} step {Step}/{Total} — {Pct}%")]
    private partial void LogStep(string reportId, string phase, int step, int total, int pct);
}
