namespace OrleansSample;

/// <summary>
/// Submits a batch of demo jobs once the application (and Orleans silo) is fully
/// started, then logs the final statuses.  Runs as a background service so the
/// web host — and the Orleans dashboard — continue to serve requests after the
/// batch completes.
/// </summary>
internal sealed partial class JobBootstrapService(
    IClusterClient client,
    IJobTracker tracker,
    IHostApplicationLifetime lifetime,
    ILogger<JobBootstrapService> logger) : BackgroundService
{
    private const int JobCount = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until the host is fully started so the Orleans silo and cluster
        // client are guaranteed to be ready before we submit any grain calls.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = lifetime.ApplicationStarted.Register(() => ready.TrySetResult());
        await ready.Task.WaitAsync(stoppingToken);

        LogSubmittingJobs(JobCount);

        var submitTasks = Enumerable.Range(1, JobCount).Select(async i =>
        {
            var grain = client.GetGrain<IJobGrain>($"job-{i:D3}");

            IJobRequest request = (i % 3) switch
            {
                0 => new BatchJobRequest(
                    Items: Enumerable.Range(1, i).Select(n => $"item-{n}").ToList(),
                    Category: "batch"),
                1 => new ScheduledJobRequest(
                    Payload: $"Payload for job {i}",
                    ScheduledAt: DateTimeOffset.UtcNow.AddSeconds(-i)),
                _ => new JobRequest($"Payload for job {i}", Category: i % 2 == 0 ? "even" : "odd"),
            };

            await grain.SubmitAsync(request);
        });

        await Task.WhenAll(submitTasks);
        LogJobsSubmitted(JobCount);

        // Poll until all jobs reach a terminal state (or the host is stopping).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline && !stoppingToken.IsCancellationRequested)
        {
            var snapshot = tracker.Snapshot();
            var completed = snapshot.Values.Count(s => s is JobStatus.Completed or JobStatus.Failed);
            if (completed >= JobCount) break;
            await Task.Delay(200, stoppingToken);
        }

        // Query final statuses through grain references (re-activates each grain briefly).
        LogFinalStatusHeader();
        for (int i = 1; i <= JobCount; i++)
        {
            var grain = client.GetGrain<IJobGrain>($"job-{i:D3}");
            var status = await grain.GetStatusAsync();
            LogFinalJobStatus(i, status);
        }

        LogBatchComplete();
    }

    [LoggerMessage(1, LogLevel.Information, "Submitting {Count} jobs via grains...")]
    private partial void LogSubmittingJobs(int count);

    [LoggerMessage(2, LogLevel.Information, "{Count} jobs submitted — grains deactivated, awaiting observer callbacks")]
    private partial void LogJobsSubmitted(int count);

    [LoggerMessage(3, LogLevel.Information, "── Final job statuses ───────────────────────────────────")]
    private partial void LogFinalStatusHeader();

    [LoggerMessage(4, LogLevel.Information, "  job-{JobId:D3}  {Status}")]
    private partial void LogFinalJobStatus(int jobId, JobStatus status);

    [LoggerMessage(5, LogLevel.Information, "Batch complete — dashboard remains available at http://localhost:8080")]
    private partial void LogBatchComplete();
}
