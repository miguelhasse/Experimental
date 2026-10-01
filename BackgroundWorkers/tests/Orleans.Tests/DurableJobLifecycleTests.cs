#pragma warning disable ORLEANSEXP005
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace Orleans.Tests;

/// <summary>
/// What happens to jobs in flight when the activation that owns them is lost for real (a graceful silo restart), which is the one
/// case the keep-alive cannot prevent. Uses its own cluster; the journal is carried over to the restarted silo.
/// </summary>
[Collection("ClusterCollection")]   // shares the static fake pool and monitor with the other grain tests
public sealed class DurableJobLifecycleTests
{
    // The journal has to outlive the first silo, so the restarted one is handed the provider the first one created
    // (standing in for durable storage). The in-memory durable jobs store is process-wide already.
    private static VolatileJournalStorageProvider? s_journal;

    public sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder silo)
        {
            silo.Services
                .AddSingleton<IJobTracker>(ClusterFixture.Tracker)
                .AddSingleton<IRequestPool>(ClusterFixture.Pool)
                .AddSingleton<IRequestPoolMonitor>(ClusterFixture.Monitor)
                .Configure<JobRecoveryOptions>(o =>
                {
                    o.CheckPeriod = TimeSpan.FromSeconds(1);
                    o.OrphanGracePeriod = TimeSpan.Zero;
                })
                .Configure<DurableJobsOptions>(o =>
                {
                    o.ShardDuration = TimeSpan.FromSeconds(1);
                    o.ShardActivationBufferPeriod = TimeSpan.FromSeconds(5);
                    o.JobStatusPollInterval = TimeSpan.FromMilliseconds(100);
                });

            if (s_journal is null)
            {
                silo.AddDurableJobJournaling();
                return;
            }

            // The restarted silo: the same wiring as AddDurableJobJournaling, but on the first silo's journal.
            silo.ConfigureServices(services =>
            {
                services.AddSingleton(s_journal);
                services.AddSingleton<IJournalStorageProvider>(s_journal);
                services.TryAddSingleton<IJobOwnerLiveness, SiloJobOwnerLiveness>();
            });
            silo.UseInMemoryDurableJobs();
            silo.AddJournalStorage()
                .UseJsonJournalFormat(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        }
    }

    /// <summary>Polls, tolerating the transient errors of a client that is reconnecting after the restart.</summary>
    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                    return true;
            }
            catch (Exception)
            {
                // reconnecting
            }

            await Task.Delay(100);
        }

        return false;
    }

    [Fact]
    public async Task ActivationLostWithARunInFlight_CancelsAndReleasesIt_AndTheRestartedSiloRecoversIt()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        try
        {
            s_journal = cluster.GetSiloServiceProvider().GetRequiredService<VolatileJournalStorageProvider>();

            var key = $"lifecycle-{Guid.NewGuid():N}";
            var grain = cluster.GrainFactory.GetGrain<IDurableJobGrain>(key);
            var capture = ClusterFixture.Pool.Capture();
            await grain.SubmitAsync("j1", new JobRequest("p", PartialResults: 2));
            var oldWorker = capture.Requests.Single(r => r.Context.PartitionKey == key);
            oldWorker.Report(50, "stage 1", new PartialResultDelta(1, 2, "partial-before-the-restart", Redeliver: false));
            Assert.True(await EventuallyAsync(async () => (await grain.GetJobAsync("j1"))!.ResultsReceived == 1), "the first partial result was not applied");

            // The silo is restarted gracefully: the activation is deactivated while its run is still in flight.
            await cluster.RestartSiloAsync(cluster.Silos[0]);

            // Deactivation cancelled the run in the pool: its results could no longer be delivered anywhere.
            var oldRequestId = DurableJobGrain.RunRequestId(key, "j1", 1);
            Assert.Equal(oldRequestId, oldWorker.Context.RequestId);
            Assert.Contains(oldRequestId, ClusterFixture.Monitor.CancelRequests);

            // The reactivated grain finds the released job and restarts it as a new run, without anyone resubmitting it.
            Assert.True(
                await EventuallyAsync(async () =>
                {
                    await grain.GetJobAsync("j1");   // activates the grain on the restarted silo
                    return capture.Requests.Count(r => r.Context.PartitionKey == key) == 2;
                }),
                "the released job was not restarted");
            var newWorker = capture.Requests.Where(r => r.Context.PartitionKey == key).Last();
            Assert.NotEqual(oldRequestId, newWorker.Context.RequestId);

            // The old worker was still running and finishes after the restart: its mailbox is gone, so the data goes nowhere.
            await oldWorker.CompleteAsync("stale");
            await Task.Delay(300, TestContext.Current.CancellationToken);

            newWorker.Report(50, "stage 1", new PartialResultDelta(1, 2, "fresh-1", Redeliver: false));
            await newWorker.CompleteAsync("fresh");
            DurableJobRecord? job = null;
            Assert.True(await EventuallyAsync(async () => (job = await grain.GetJobAsync("j1"))!.IsTerminal), "the restarted job did not finish");

            Assert.Equal((JobStatus.Completed, "fresh", 1, 2L), (job!.Status, job.Output, job.Attempts, job.Epoch));
            Assert.Equal(2, job.ResultsReceived);   // the new run's partial and final result; nothing of the first run
            Assert.Equal(1, await grain.GetFinishedCountAsync());
        }
        finally
        {
            s_journal = null;
            await cluster.StopAllSilosAsync(TestContext.Current.CancellationToken);
            await cluster.DisposeAsync();
        }
    }
}
