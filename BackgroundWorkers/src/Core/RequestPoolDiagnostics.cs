using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RequestProcessor.Diagnostics;

/// <summary>
/// Telemetry constants for the request pool. Reference <see cref="ActivitySourceName"/>
/// and <see cref="MeterName"/> when configuring OpenTelemetry exporters.
/// </summary>
public static class RequestPoolDiagnostics
{
    /// <summary>Name of the <see cref="System.Diagnostics.ActivitySource"/> emitted by the pool.</summary>
    public const string ActivitySourceName = "RequestProcessor";

    /// <summary>Name of the <see cref="Meter"/> emitted by the pool.</summary>
    public const string MeterName = "RequestProcessor";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, version: "1.0.0");
}

/// <summary>
/// Strongly-typed metric instruments for the request pool.
/// One instance is created per <see cref="RequestPoolService"/> via DI's
/// <see cref="IMeterFactory"/>, ensuring correct lifecycle management.
/// </summary>
internal sealed class RequestPoolMetrics : IDisposable
{
    private readonly Meter _meter;

    /// <summary>Total requests accepted into the queue.</summary>
    internal readonly Counter<long> Enqueued;

    /// <summary>Total requests that finished processing (tag: <c>outcome = success | failure</c>).</summary>
    internal readonly Counter<long> Completed;

    /// <summary>End-to-end dispatcher duration in milliseconds.</summary>
    internal readonly Histogram<double> ProcessingDuration;

    /// <summary>Workers currently inside a <see cref="IRequestDispatcher.DispatchAsync"/> call.</summary>
    internal readonly UpDownCounter<int> ActiveWorkers;

    /// <summary>Requests cancelled before dispatch began.</summary>
    internal readonly Counter<long> Cancelled;

    /// <summary>Dispatcher attempts scheduled after an initial failure.</summary>
    internal readonly Counter<long> Retry;

    /// <summary>Requests that exhausted attempts or were denied retry.</summary>
    internal readonly Counter<long> DeadLetter;

    /// <summary>Requests dropped by a bounded channel due to a drop <c>FullMode</c> policy.</summary>
    internal readonly Counter<long> Dropped;

    /// <summary>Requests promoted by priority aging (tags: <c>from_priority</c>, <c>to_priority</c>).</summary>
    internal readonly Counter<long> PriorityPromoted;

    /// <summary>Empty partition channels evicted after being idle.</summary>
    internal readonly Counter<long> PartitionEvicted;

    internal RequestPoolMetrics(
        IMeterFactory meterFactory,
        Func<int> queueDepth,
        Func<IEnumerable<Measurement<int>>> activePartitions)
    {
        _meter = meterFactory.Create(RequestPoolDiagnostics.MeterName);

        Enqueued = _meter.CreateCounter<long>(
            "requestpool.requests.enqueued",
            unit: "requests",
            description: "Total requests accepted into the queue.");

        Completed = _meter.CreateCounter<long>(
            "requestpool.requests.completed",
            unit: "requests",
            description: "Total requests that finished processing, tagged by outcome.");

        ProcessingDuration = _meter.CreateHistogram<double>(
            "requestpool.processing.duration",
            unit: "ms",
            description: "End-to-end time spent inside IRequestDispatcher.DispatchAsync.");

        ActiveWorkers = _meter.CreateUpDownCounter<int>(
            "requestpool.workers.active",
            unit: "workers",
            description: "Number of workers currently executing a dispatch.");

        Cancelled = _meter.CreateCounter<long>(
            "requestpool.requests.cancelled",
            unit: "requests",
            description: "Requests cancelled before dispatch began.");

        Retry = _meter.CreateCounter<long>(
            "requestpool.retry",
            unit: "requests",
            description: "Dispatcher attempts scheduled after an initial failure.");

        DeadLetter = _meter.CreateCounter<long>(
            "requestpool.dead_letter",
            unit: "requests",
            description: "Requests that exhausted attempts or were denied retry.");

        Dropped = _meter.CreateCounter<long>(
            "requestpool.dropped",
            unit: "requests",
            description: "Requests dropped by a bounded channel due to a drop FullMode policy.");

        PriorityPromoted = _meter.CreateCounter<long>(
            "requestpool.priority_promoted",
            unit: "requests",
            description: "Requests promoted by priority aging.");

        PartitionEvicted = _meter.CreateCounter<long>(
            "requestpool.partition_evicted",
            unit: "partitions",
            description: "Empty partition channels evicted after being idle.");

        _meter.CreateObservableGauge(
            "requestpool.partitions_active",
            observeValues: activePartitions,
            unit: "partitions",
            description: "Active partition channels by priority.");

        _meter.CreateObservableGauge(
            "requestpool.queue.depth",
            observeValue: queueDepth,
            unit: "requests",
            description: "Approximate number of requests waiting in the bounded channel.");
    }

    public void Dispose() => _meter.Dispose();
}
