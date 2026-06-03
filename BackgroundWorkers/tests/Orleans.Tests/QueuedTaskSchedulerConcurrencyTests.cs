using System.Collections.Concurrent;
using System.Threading.Tasks.Schedulers;

namespace Orleans.Tests;

public sealed class QueuedTaskSchedulerConcurrencyTests
{
    [Fact]
    public async Task ConcurrentPriorityStress_CompletesAndHonorsQuantumFallbackOrdering()
    {
        using var scheduler = new QueuedTaskScheduler(threadCount: 1);
        var queues = new[]
        {
            scheduler.ActivateNewQueue(priority: 0, quantum: 2),
            scheduler.ActivateNewQueue(priority: 1, quantum: 1),
            scheduler.ActivateNewQueue(priority: 2, quantum: 1),
        };

        using var blockerStarted = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();

        var blocker = Task.Factory.StartNew(
            () =>
            {
                blockerStarted.Set();
                releaseBlocker.Wait(TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.None,
            scheduler);

        Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        const int perPriority = 120;
        const int priorityCount = 3;
        var executionOrder = new ConcurrentQueue<int>();
        var scheduledTasks = new ConcurrentBag<Task>();

        var producers = Enumerable.Range(0, perPriority * priorityCount)
            .Select(i => Task.Run(() =>
            {
                int priority = i % priorityCount;
                scheduledTasks.Add(Task.Factory.StartNew(
                    () => executionOrder.Enqueue(priority),
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    queues[priority]));
            }, TestContext.Current.CancellationToken))
            .ToArray();

        await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(perPriority * priorityCount, scheduledTasks.Count);

        releaseBlocker.Set();
        await Task.WhenAll(scheduledTasks.Append(blocker)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var actual = executionOrder.ToArray();
        Assert.Equal(perPriority * priorityCount, actual.Length);
        Assert.Equal(BuildExpectedPriorityOrder(perPriority), actual);
    }

    private static int[] BuildExpectedPriorityOrder(int perPriority)
    {
        int[] remaining = [perPriority, perPriority, perPriority];
        int[] consecutiveServed = [0, 0, 0];
        int[] quantum = [2, 1, 1];
        var expected = new List<int>(perPriority * remaining.Length);

        while (remaining.Any(count => count > 0))
        {
            bool picked = false;
            for (int priority = 0; priority < remaining.Length; priority++)
            {
                if (consecutiveServed[priority] >= quantum[priority])
                {
                    consecutiveServed[priority] = 0;
                    continue;
                }

                if (remaining[priority] > 0)
                {
                    remaining[priority]--;
                    consecutiveServed[priority]++;
                    expected.Add(priority);
                    picked = true;
                    break;
                }

                consecutiveServed[priority] = 0;
            }

            if (picked)
                continue;

            for (int priority = 0; priority < remaining.Length; priority++)
            {
                if (remaining[priority] > 0)
                {
                    remaining[priority]--;
                    expected.Add(priority);
                    break;
                }
            }
        }

        return expected.ToArray();
    }
}
