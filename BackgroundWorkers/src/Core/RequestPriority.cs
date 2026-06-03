namespace RequestProcessor;

/// <summary>
/// Processing priority for a request submitted to <see cref="IRequestPool"/>.
/// Higher-priority requests are dequeued before lower-priority ones within the same pool.
/// </summary>
public enum RequestPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
}
