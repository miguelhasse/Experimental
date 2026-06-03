namespace Orleans.Tests;

/// <summary>
/// Polling helpers for grain integration tests — needed because
/// <c>[OneWay]</c> observer calls are fire-and-forget and Orleans delivers
/// them asynchronously on the grain scheduler.
/// </summary>
internal static class GrainTestExtensions
{
    /// <summary>
    /// Polls <see cref="IJobGrain.GetStatusAsync"/> until it returns
    /// <paramref name="expected"/> or the <paramref name="timeout"/> elapses.
    /// Asserts equality on exit so test failures report the actual status.
    /// </summary>
    public static async Task WaitForStatusAsync(
        IJobGrain grain,
        JobStatus expected,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (await grain.GetStatusAsync() == expected) return;
            await Task.Delay(20);
        }
        Assert.Equal(expected, await grain.GetStatusAsync());
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns <c>true</c> or the
    /// <paramref name="timeout"/> elapses.  Asserts on exit.
    /// </summary>
    public static async Task WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }
        Assert.True(await condition(), "Condition was not satisfied within the timeout.");
    }
}
