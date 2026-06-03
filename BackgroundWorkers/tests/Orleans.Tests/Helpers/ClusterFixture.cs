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

    public TestCluster Cluster { get; }

    public ClusterFixture()
    {
        var builder = new TestClusterBuilder();
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
                .AddSingleton<IRequestPoolMonitor>(ClusterFixture.Monitor);
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
