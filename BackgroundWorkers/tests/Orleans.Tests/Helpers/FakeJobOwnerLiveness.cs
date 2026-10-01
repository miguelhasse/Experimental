namespace Orleans.Tests;

/// <summary>
/// Test <see cref="IJobOwnerLiveness"/>. <see cref="SimulateOwnerLoss"/> mimics a silo crash and restart:
/// the current owner becomes dead and subsequent work is owned by a new id.
/// </summary>
internal sealed class FakeJobOwnerLiveness : IJobOwnerLiveness
{
    private readonly HashSet<string> _dead = [];
    private readonly object _lock = new();
    private int _generation = 1;

    public string CurrentOwnerId
    {
        get { lock (_lock) return $"test-silo-{_generation}"; }
    }

    public bool IsAlive(string? ownerId)
    {
        lock (_lock)
            return ownerId is not null && !_dead.Contains(ownerId);
    }

    /// <summary>Marks the current owner dead and starts a new one. Returns the owner that was lost.</summary>
    public string SimulateOwnerLoss()
    {
        lock (_lock)
        {
            var lost = $"test-silo-{_generation}";
            _dead.Add(lost);
            _generation++;
            return lost;
        }
    }
}
