using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace RequestProcessor.HealthChecks;

/// <summary>
/// Reports request pool health based on queued work per priority tier.
/// </summary>
public sealed class RequestPoolHealthCheck(
    IRequestPoolMonitor monitor,
    IOptions<RequestPoolHealthCheckOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = monitor.GetSnapshot();
        var values = options.Value;
        var status = HealthStatus.Healthy;

        var high = Evaluate(RequestPriority.High, snapshot.QueueDepthHigh, values);
        var normal = Evaluate(RequestPriority.Normal, snapshot.QueueDepthNormal, values);
        var low = Evaluate(RequestPriority.Low, snapshot.QueueDepthLow, values);

        status = Worst(status, high);
        status = Worst(status, normal);
        status = Worst(status, low);

        var data = new Dictionary<string, object>
        {
            ["high_depth"] = snapshot.QueueDepthHigh,
            ["normal_depth"] = snapshot.QueueDepthNormal,
            ["low_depth"] = snapshot.QueueDepthLow,
        };

        var description = status switch
        {
            HealthStatus.Unhealthy => "One or more request pool queues reached an unhealthy threshold.",
            HealthStatus.Degraded => "One or more request pool queues reached a degraded threshold.",
            _ => "Request pool queue depths are below configured thresholds.",
        };

        return Task.FromResult(new HealthCheckResult(status, description, data: data));
    }

    private static HealthStatus Evaluate(RequestPriority priority, int depth, RequestPoolHealthCheckOptions options)
    {
        var thresholds = options.For(priority);

        if (thresholds.UnhealthyThreshold is { } unhealthy && depth >= unhealthy)
            return HealthStatus.Unhealthy;

        if (thresholds.DegradedThreshold is { } degraded && depth >= degraded)
            return HealthStatus.Degraded;

        return HealthStatus.Healthy;
    }

    private static HealthStatus Worst(HealthStatus left, HealthStatus right)
    {
        if (left == HealthStatus.Unhealthy || right == HealthStatus.Unhealthy)
            return HealthStatus.Unhealthy;

        if (left == HealthStatus.Degraded || right == HealthStatus.Degraded)
            return HealthStatus.Degraded;

        return HealthStatus.Healthy;
    }
}
