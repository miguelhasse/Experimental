namespace Orleans.Tests;

/// <summary>
/// Shared test cluster used by all Orleans grain integration tests.
/// A single silo is spun up per test collection; unique grain keys must be used
/// across tests to avoid tracker-state contamination.
/// </summary>
public sealed class ClusterFixture : IDisposable
{
    // Static singletons injected into the test silo.
    // Tests interact with these directly to pre-seed state or verify side-effects.
    internal static readonly InMemoryJobTracker Tracker = new();
    internal static readonly FakeRequestPool Pool = new();
    internal static readonly FakeRequestPoolMonitor Monitor = new();
    internal static readonly FakeJobOwnerLiveness Liveness = new();

    public TestCluster Cluster { get; }

    public ClusterFixture()
    {
        // One silo: DurableJobGrain uses per-silo volatile journal storage, so a grain that
        // re-activates on a different silo would not see its previous state.
        var builder = new TestClusterBuilder(initialSilosCount: 1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public void Dispose() =>
        Cluster.StopAllSilosAsync().GetAwaiter().GetResult();

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.Services
                .AddSingleton<IJobTracker>(ClusterFixture.Tracker)
                .AddSingleton<IRequestPool>(ClusterFixture.Pool)
                .AddSingleton<IRequestPoolMonitor>(ClusterFixture.Monitor)
                .AddSingleton<IJobOwnerLiveness>(ClusterFixture.Liveness); // registered first so TryAdd keeps it

            siloBuilder.AddDurableJobJournaling();

            // Scheduled durable jobs drive recovery of orphaned jobs; shorten the delays so tests do not wait minutes.
            siloBuilder.Services
                .Configure<JobRecoveryOptions>(o =>
                {
                    o.CheckPeriod = TimeSpan.FromSeconds(1);
                    o.OrphanGracePeriod = TimeSpan.Zero;   // tests simulate owner loss and expect recovery at once (the grace rule has its own tests)
                    o.MaxRetainedFinishedJobs = 5;         // small, so the retention rule is cheap to test
                })
                .Configure<DurableJobsOptions>(o =>
                {
                    // A shard that starts further away than the activation buffer is only started by a periodic
                    // check, so a short check delay could wait a long time. A buffer larger than
                    // shard duration + check delay makes every shard activate as soon as it is created.
                    o.ShardDuration = TimeSpan.FromSeconds(1);
                    o.ShardActivationBufferPeriod = TimeSpan.FromSeconds(5);
                    o.JobStatusPollInterval = TimeSpan.FromMilliseconds(100);
                });
        }
    }
}

/// <summary>
/// xUnit collection that shares a single <see cref="ClusterFixture"/> across
/// all test classes that declare <c>[Collection("ClusterCollection")]</c>.
/// Tests in the collection run sequentially, which also serialises access to
/// the shared <see cref="FakeRequestPool"/> handler.
/// </summary>
[CollectionDefinition("ClusterCollection")]
public sealed class ClusterCollection : ICollectionFixture<ClusterFixture> { }
