using Orleans.Runtime;

namespace OrleansSample;

/// <summary>
/// Decides whether the silo that was running a job is still alive. A job that is still
/// <see cref="JobStatus.Processing"/> in the journal but whose owner is gone has lost its
/// in-memory pool work and must be resubmitted.
/// </summary>
public interface IJobOwnerLiveness
{
    /// <summary>Identifier stored on a job when this silo starts running it.</summary>
    string CurrentOwnerId { get; }

    /// <summary>
    /// False only if <paramref name="ownerId"/> is null, malformed, absent from the membership view or marked dead.
    /// A member that is joining, shutting down or stopping may still deliver results, so it counts as alive.
    /// </summary>
    bool IsAlive(string? ownerId);
}

/// <summary>
/// Uses the silo address (which includes the silo's start generation, so a restarted silo
/// is a different owner) and cluster membership.
/// </summary>
internal sealed class SiloJobOwnerLiveness(
    ILocalSiloDetails localSilo,
    IClusterMembershipService membership) : IJobOwnerLiveness
{
    public string CurrentOwnerId { get; } = localSilo.SiloAddress.ToParsableString();

    public bool IsAlive(string? ownerId)
    {
        if (ownerId is null)
            return false;

        if (ownerId == CurrentOwnerId)
            return true;

        SiloAddress address;
        try
        {
            address = SiloAddress.FromParsableString(ownerId);
        }
        catch (Exception)
        {
            return false; // Not a silo address we wrote: treat the owner as gone.
        }

        // Dead, or not in the view at all (restarted silos get a new address). Anything else may still be running work.
        return membership.CurrentSnapshot.Members.TryGetValue(address, out var member)
            && member.Status != SiloStatus.Dead;
    }
}
