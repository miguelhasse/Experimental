#pragma warning disable ORLEANSEXP005
using Orleans.Hosting;

namespace Orleans.Tests;

/// <summary>
/// The run timeout, on its own cluster with a short limit (the shared one would make every test that deliberately holds a job
/// for a few seconds time out). The fake pool never calls back for a held request, which is exactly a handler that hangs
/// and ignores cancellation: the pool's own timeout can do nothing about it, so the grain has to.
/// </summary>
[Collection("ClusterCollection")]   // shares the static fake pool and monitor with the other grain tests
public sealed class DurableJobTimeoutTests
{
    private static readonly TimeSpan MaxRun = TimeSpan.FromSeconds(2);

    public sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder silo)
        {
            silo.Services
                .AddSingleton<IJobTracker>(ClusterFixture.Tracker)
                .AddSingleton<IRequestPool>(ClusterFixture.Pool)
                .AddSingleton<IRequestPoolMonitor>(ClusterFixture.Monitor)
                .AddSingleton<IJobOwnerLiveness>(ClusterFixture.Liveness)
                .Configure<JobRecoveryOptions>(o =>
                {
                    o.CheckPeriod = TimeSpan.FromSeconds(1);
                    o.OrphanGracePeriod = TimeSpan.Zero;
                    o.MaxRunDuration = MaxRun;
                })
                .Configure<DurableJobsOptions>(o =>
                {
                    o.ShardDuration = TimeSpan.FromSeconds(1);
                    o.ShardActivationBufferPeriod = TimeSpan.FromSeconds(5);
                    o.JobStatusPollInterval = TimeSpan.FromMilliseconds(100);
                });
            silo.AddDurableJobJournaling();
        }
    }

    private static async Task<TestCluster> DeployAsync()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        var cluster = builder.Build();
        await cluster.DeployAsync();
        return cluster;
    }

    private static async Task<DurableJobRecord> WaitForTerminalAsync(IDurableJobGrain grain, string jobId, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await grain.GetJobAsync(jobId) is { IsTerminal: true } job)
                return job;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Job '{jobId}' did not reach a terminal state.");
    }

    private static HeldRequest HeldFor(PoolCapture capture, string key, int index = 0) =>
        capture.Requests.Where(r => r.Context.PartitionKey == key).ElementAt(index);

    [Fact]
    public async Task ARunThatNeverReportsBack_IsFailedByTheGrain_AndTheGrainIsFreedAgain()
    {
        var cluster = await DeployAsync();
        try
        {
            var key = $"timeout-{Guid.NewGuid():N}";
            var grain = cluster.GrainFactory.GetGrain<IDurableJobGrain>(key);
            var management = cluster.Client.GetGrain<IManagementGrain>(0);
            var capture = ClusterFixture.Pool.Capture();

            var started = DateTime.UtcNow;
            await grain.SubmitAsync("j1", new JobRequest("p"));
            var worker = HeldFor(capture, key);
            Assert.Equal(MaxRun, worker.Context.Timeout);   // the pool's own, cooperative timeout is set too

            var job = await WaitForTerminalAsync(grain, "j1");
            var took = DateTime.UtcNow - started;

            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("Timed out", job.Error);
            Assert.InRange(took, MaxRun, MaxRun + TimeSpan.FromSeconds(6));   // not before the limit, and soon after it
            Assert.Equal(1, await grain.GetFinishedCountAsync());
            Assert.Contains(worker.Context.RequestId, ClusterFixture.Monitor.CancelRequests);

            // Whatever the hung worker sends once it wakes up is ignored: the run is finished.
            worker.Report(100, "late", new PartialResultDelta(1, 1, "late partial", Redeliver: false));
            await worker.CompleteAsync("too late");
            await Task.Delay(500, TestContext.Current.CancellationToken);
            var after = (await grain.GetJobAsync("j1"))!;
            Assert.Equal((JobStatus.Failed, (string?)null, 0), (after.Status, after.Output, after.ResultsReceived));
            Assert.Equal(1, await grain.GetFinishedCountAsync());

            // It is not resubmitted by recovery either, and nothing pins the grain any more: collection works again.
            Assert.Equal(1, capture.Requests.Count(r => r.Context.PartitionKey == key));
            var id = grain.GetGrainId();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var collected = false;
            while (!collected && DateTime.UtcNow < deadline)
            {
                await management.ForceActivationCollection(TimeSpan.Zero);
                collected = !(await management.GetDetailedGrainStatistics()).Any(s => s.GrainId == id);
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.True(collected, "the grain is still pinned after its only run timed out");
        }
        finally
        {
            await cluster.StopAllSilosAsync(TestContext.Current.CancellationToken);
            await cluster.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARunThatFinishesInTime_IsNotFailedWhenItsDeadlinePasses()
    {
        var cluster = await DeployAsync();
        try
        {
            var key = $"timeout-{Guid.NewGuid():N}";
            var grain = cluster.GrainFactory.GetGrain<IDurableJobGrain>(key);
            var capture = ClusterFixture.Pool.Capture();
            await grain.SubmitAsync("j1", new JobRequest("p"));
            var worker = HeldFor(capture, key);

            await Task.Delay(300, TestContext.Current.CancellationToken);
            await worker.CompleteAsync("in time");
            Assert.Equal(JobStatus.Completed, (await WaitForTerminalAsync(grain, "j1")).Status);

            await Task.Delay(MaxRun + TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);   // well past the deadline the run had

            var job = (await grain.GetJobAsync("j1"))!;
            Assert.Equal((JobStatus.Completed, "in time", 1), (job.Status, job.Output, job.ResultsReceived));
            Assert.Equal(1, await grain.GetFinishedCountAsync());   // not counted a second time by the watchdog
        }
        finally
        {
            await cluster.StopAllSilosAsync(TestContext.Current.CancellationToken);
            await cluster.DisposeAsync();
        }
    }

    [Fact]
    public async Task AfterATimeout_ASubmitWithTheSameIdempotencyKey_RunsTheJobAgain()
    {
        var cluster = await DeployAsync();
        try
        {
            var key = $"timeout-{Guid.NewGuid():N}";
            var grain = cluster.GrainFactory.GetGrain<IDurableJobGrain>(key);
            var capture = ClusterFixture.Pool.Capture();

            await grain.SubmitAsync("j1", new JobRequest("p", IdempotencyKey: "request-1"));
            Assert.Equal(JobStatus.Failed, (await WaitForTerminalAsync(grain, "j1")).Status);   // timed out

            // The client retries the same request: it failed, so it must not be swallowed as a duplicate.
            await grain.SubmitAsync("j1", new JobRequest("p", IdempotencyKey: "request-1"));
            Assert.Equal(2, capture.Requests.Count(r => r.Context.PartitionKey == key));
            var second = HeldFor(capture, key, index: 1);
            Assert.NotEqual(HeldFor(capture, key, index: 0).Context.RequestId, second.Context.RequestId);

            await second.CompleteAsync("second try");
            Assert.Equal("second try", (await WaitForTerminalAsync(grain, "j1")).Output);
        }
        finally
        {
            await cluster.StopAllSilosAsync(TestContext.Current.CancellationToken);
            await cluster.DisposeAsync();
        }
    }
}
