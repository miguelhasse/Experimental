using RequestProcessor;

namespace Demo;

/// <summary>
/// Stand-in dispatcher that simulates async work with a random delay.
/// Replace with your real implementation (HTTP, database, message bus, etc.).
/// </summary>
internal sealed partial class SampleRequestDispatcher(ILogger<SampleRequestDispatcher> logger) : IRequestDispatcher
{
    private static readonly Random _rng = Random.Shared;

    public async ValueTask<RequestResult> DispatchAsync(RequestContext context, CancellationToken cancellationToken)
    {
        LogDispatching(context.RequestId);

        // Simulate variable-latency I/O (50–300 ms).
        await Task.Delay(_rng.Next(50, 300), cancellationToken);

        var output = $"Processed request '{context.RequestId}' at {DateTimeOffset.UtcNow:O}";
        return new RequestResult(context.RequestId, Success: true, Output: output);
    }

    [LoggerMessage(1, LogLevel.Information, "Dispatching {RequestId}")]
    private partial void LogDispatching(string requestId);
}

