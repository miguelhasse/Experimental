namespace System.Threading.Tasks.Schedulers;

/// <summary>
/// Configuration options for <see cref="QueuedTaskScheduler"/> and its priority sub-queues.
/// </summary>
public sealed class QueuedTaskSchedulerOptions
{
    /// <summary>
    /// Whether to enable the <see cref="QueuedTaskScheduler"/> for the request pool.
    /// When <see langword="false"/>, no dedicated background threads are created and the pool
    /// dispatches on the default <see cref="ThreadPool"/> — useful for benchmarking and for
    /// pure async I/O handlers where dedicated threads add overhead without benefit.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of dedicated background threads. Defaults to 4.</summary>
    public int ThreadCount { get; set; } = 4;

    /// <summary>Thread priority for scheduler threads. Defaults to <see cref="ThreadPriority.BelowNormal"/>.</summary>
    public ThreadPriority ThreadPriority { get; set; } = ThreadPriority.BelowNormal;

    /// <summary>Maximum consecutive tasks served from the high-priority queue before yielding. Defaults to 8.</summary>
    public int HighQuantum { get; set; } = 8;

    /// <summary>Maximum consecutive tasks served from the normal-priority queue before yielding. Defaults to 4.</summary>
    public int NormalQuantum { get; set; } = 4;

    /// <summary>Maximum consecutive tasks served from the low-priority queue before yielding. Defaults to <see cref="int.MaxValue"/> (unbounded), preserving the original behavior.</summary>
    public int LowQuantum { get; set; } = int.MaxValue;
}
