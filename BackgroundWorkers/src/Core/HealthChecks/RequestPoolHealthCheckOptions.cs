namespace RequestProcessor.HealthChecks;

/// <summary>
/// Queue depth thresholds used by <see cref="RequestPoolHealthCheck"/>.
/// </summary>
public sealed class RequestPoolHealthCheckOptions
{
    public RequestPoolHealthThresholds High { get; } = new();

    public RequestPoolHealthThresholds Normal { get; } = new();

    public RequestPoolHealthThresholds Low { get; } = new();

    internal RequestPoolHealthThresholds For(RequestPriority priority) => priority switch
    {
        RequestPriority.High => High,
        RequestPriority.Normal => Normal,
        RequestPriority.Low => Low,
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null),
    };
}

/// <summary>
/// Degraded and unhealthy queue depth thresholds for a single priority tier.
/// </summary>
public sealed class RequestPoolHealthThresholds
{
    public int? DegradedThreshold { get; set; }

    public int? UnhealthyThreshold { get; set; }
}
