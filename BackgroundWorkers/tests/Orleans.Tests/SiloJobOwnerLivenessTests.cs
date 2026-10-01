using Orleans.Runtime;

namespace Orleans.Tests;

/// <summary>
/// Exercises the real <see cref="SiloJobOwnerLiveness"/> against the test silo
/// (the grain tests substitute <see cref="FakeJobOwnerLiveness"/>).
/// </summary>
[Collection("ClusterCollection")]
public sealed class SiloJobOwnerLivenessTests(ClusterFixture fixture)
{
    private SiloJobOwnerLiveness Create()
    {
        var sp = fixture.Cluster.GetSiloServiceProvider();
        return new SiloJobOwnerLiveness(
            sp.GetRequiredService<ILocalSiloDetails>(),
            sp.GetRequiredService<IClusterMembershipService>());
    }

    [Fact]
    public void IsAlive_ForCurrentSilo_ReturnsTrue()
    {
        var liveness = Create();
        Assert.True(liveness.IsAlive(liveness.CurrentOwnerId));
    }

    [Fact]
    public void IsAlive_ForActiveClusterMember_ReturnsTrue()
    {
        var member = fixture.Cluster.Silos[0].SiloAddress;
        Assert.True(Create().IsAlive(member.ToParsableString()));
    }

    [Fact]
    public void IsAlive_ForRestartedSilo_ReturnsFalse()
    {
        // Same endpoint, newer generation: what a restarted silo looks like.
        var current = fixture.Cluster.Silos[0].SiloAddress;
        var restarted = SiloAddress.New(current.Endpoint, current.Generation + 1);

        Assert.False(Create().IsAlive(restarted.ToParsableString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-silo-address")]
    public void IsAlive_ForMissingOrMalformedOwner_ReturnsFalse(string? owner)
    {
        Assert.False(Create().IsAlive(owner));
    }
}
