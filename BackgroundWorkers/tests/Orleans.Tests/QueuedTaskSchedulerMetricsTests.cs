using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Tasks.Schedulers;

namespace Orleans.Tests;

public sealed class QueuedTaskSchedulerMetricsTests
{
    [Fact]
    public async Task Metrics_AreEmittedForQueuedAndDispatchedTasks()
    {
        var measurements = new ConcurrentBag<MetricMeasurement>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == QueuedTaskSchedulerMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new MetricMeasurement(instrument.Name, value, ToTags(tags))));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            measurements.Add(new MetricMeasurement(instrument.Name, value, ToTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new MetricMeasurement(instrument.Name, value, ToTags(tags))));
        listener.Start();

        await using var provider = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();

        using var scheduler = new QueuedTaskScheduler(
            threadCount: 1,
            meterFactory: provider.GetRequiredService<IMeterFactory>());
        var queue = scheduler.ActivateNewQueue(priority: 0);

        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();

        var first = Task.Factory.StartNew(
            () =>
            {
                firstStarted.Set();
                releaseFirst.Wait(TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.None,
            queue);

        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var second = Task.Factory.StartNew(
            () => { },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.None,
            queue);

        listener.RecordObservableInstruments();

        releaseFirst.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Contains(measurements, m => m.Name == "tasks_queued_total" && m.Value >= 1 && HasPriority(m.Tags, 0));
        Assert.Contains(measurements, m => m.Name == "tasks_dispatched_total" && m.Value >= 1 && HasPriority(m.Tags, 0));
        Assert.Contains(measurements, m => m.Name == "queue_depth" && m.Value >= 1 && HasPriority(m.Tags, 0));
        Assert.Contains(measurements, m => m.Name == "groups_active" && m.Value >= 1);
        Assert.Contains(measurements, m => m.Name == "lock_wait_ms" && m.Value >= 0);
    }

    private static Dictionary<string, object?> ToTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>();
        foreach (var tag in tags)
            result[tag.Key] = tag.Value;
        return result;
    }

    private static bool HasPriority(IReadOnlyDictionary<string, object?> tags, int priority) =>
        tags.TryGetValue("priority", out var value) && value is int actual && actual == priority;

    private sealed record MetricMeasurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);
}
