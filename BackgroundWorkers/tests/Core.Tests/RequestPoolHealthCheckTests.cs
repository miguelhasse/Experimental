using Microsoft.Extensions.Diagnostics.HealthChecks;
using RequestProcessor.HealthChecks;

namespace RequestProcessor.Tests;

public class RequestPoolHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_DepthsBelowThresholds_ReturnsHealthy()
    {
        var check = CreateCheck(high: 1, normal: 2, low: 3, options =>
        {
            options.High.DegradedThreshold = 2;
            options.Normal.DegradedThreshold = 3;
            options.Low.DegradedThreshold = 4;
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, result.Data["high_depth"]);
        Assert.Equal(2, result.Data["normal_depth"]);
        Assert.Equal(3, result.Data["low_depth"]);
    }

    [Fact]
    public async Task CheckHealthAsync_DepthAtDegradedThreshold_ReturnsDegraded()
    {
        var check = CreateCheck(high: 4, normal: 5, low: 0, options =>
        {
            options.High.DegradedThreshold = 10;
            options.Normal.DegradedThreshold = 5;
            options.Normal.UnhealthyThreshold = 9;
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_DepthAtUnhealthyThreshold_ReturnsUnhealthy()
    {
        var check = CreateCheck(high: 10, normal: 1, low: 1, options =>
        {
            options.High.DegradedThreshold = 5;
            options.High.UnhealthyThreshold = 10;
            options.Normal.DegradedThreshold = 2;
            options.Low.DegradedThreshold = 2;
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static RequestPoolHealthCheck CreateCheck(
        int high,
        int normal,
        int low,
        Action<RequestPoolHealthCheckOptions> configure)
    {
        var options = new RequestPoolHealthCheckOptions();
        configure(options);

        return new RequestPoolHealthCheck(
            new StubMonitor(high, normal, low),
            Options.Create(options));
    }

    private sealed class StubMonitor(int high, int normal, int low) : IRequestPoolMonitor
    {
        public RequestPoolStats GetSnapshot() => new(
            QueueDepthHigh: high,
            QueueDepthNormal: normal,
            QueueDepthLow: low,
            TotalQueueDepth: high + normal + low,
            ActiveWorkers: 0,
            TotalEnqueued: 0,
            TotalCompleted: 0,
            TotalFailed: 0,
            TotalCancelled: 0,
            MaxConcurrency: 1,
            BoundedCapacity: 100);

        public bool TryCancelRequest(string requestId) => false;

        public int CancelAllRequests(RequestPriority? priority = null) => 0;
    }
}
