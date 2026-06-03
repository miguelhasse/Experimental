using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace System.Threading.Tasks.Schedulers;

/// <summary>
/// Strongly-typed metric instruments for <see cref="QueuedTaskScheduler"/>.
/// One instance is created per scheduler via DI's <see cref="IMeterFactory"/>,
/// ensuring correct lifecycle management.
/// </summary>
internal sealed class QueuedTaskSchedulerMetrics : IDisposable
{
    internal const string MeterName = "OrleansDemo.QueuedTaskScheduler";

    private readonly Meter _meter;

    /// <summary>Total tasks accepted by the scheduler queues (tag: <c>priority</c>).</summary>
    internal readonly Counter<long> TasksQueued;

    /// <summary>Total tasks dequeued for dispatch (tag: <c>priority</c>).</summary>
    internal readonly Counter<long> TasksDispatched;

    /// <summary>Time spent waiting to acquire the scheduler queue-groups lock.</summary>
    internal readonly Histogram<double> LockWait;

    internal QueuedTaskSchedulerMetrics(
        IMeterFactory meterFactory,
        Func<IEnumerable<Measurement<int>>> queueDepth,
        Func<int> activeGroups)
    {
        _meter = meterFactory.Create(MeterName);

        TasksQueued = _meter.CreateCounter<long>(
            "tasks_queued_total",
            unit: "tasks",
            description: "Total tasks accepted by the scheduler queues, tagged by priority.");

        TasksDispatched = _meter.CreateCounter<long>(
            "tasks_dispatched_total",
            unit: "tasks",
            description: "Total tasks dequeued for dispatch, tagged by priority.");

        LockWait = _meter.CreateHistogram<double>(
            "lock_wait_ms",
            unit: "ms",
            description: "Time spent waiting to acquire the scheduler queue-groups lock.");

        _meter.CreateObservableGauge(
            "queue_depth",
            observeValues: queueDepth,
            unit: "tasks",
            description: "Approximate number of tasks waiting in scheduler queues, tagged by priority.");

        _meter.CreateObservableGauge(
            "groups_active",
            observeValue: activeGroups,
            unit: "groups",
            description: "Number of scheduler priority groups with queued work.");
    }

    internal void RecordLockWait(long startTimestamp) =>
        LockWait.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

    public void Dispose() => _meter.Dispose();
}
