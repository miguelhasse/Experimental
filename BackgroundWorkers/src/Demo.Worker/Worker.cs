using RequestProcessor;

namespace Demo;

/// <summary>
/// Continuously enqueues synthetic requests into the pool so the service
/// produces live telemetry while hosted under Aspire.
/// Throughput is intentionally throttled via <see cref="_interval"/>.
/// </summary>
public sealed partial class Worker(
    IRequestPool pool,
    ILogger<Worker> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long counter = 0;

        LogWorkerStarted(_interval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var id = $"req-{Interlocked.Increment(ref counter):D6}";
            var ctx = new RequestContext<string>(id, $"worker-item #{counter}");

            await pool.EnqueueAsync(ctx, result =>
            {
                if (result.Success)
                    LogRequestSucceeded(result.RequestId, result.Output);
                else
                    LogRequestFailed(result.RequestId, result.Error);
                return Task.CompletedTask;
            }, stoppingToken);

            await Task.Delay(_interval, stoppingToken);
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Demo worker started — submitting a request every {IntervalMs} ms")]
    private partial void LogWorkerStarted(double intervalMs);

    [LoggerMessage(2, LogLevel.Information, "✓ {RequestId}  {Output}")]
    private partial void LogRequestSucceeded(string requestId, string? output);

    [LoggerMessage(3, LogLevel.Error, "✗ {RequestId} failed")]
    private partial void LogRequestFailed(string requestId, Exception? ex);
}
