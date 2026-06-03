using Polly;
using System.Threading.Channels;

namespace RequestProcessor;

/// <summary>
/// Tuning parameters for <see cref="RequestPoolService"/>.
/// Bind from configuration or set directly in <c>AddRequestPool</c>.
/// </summary>
public sealed class RequestPoolOptions
{
    public const string SectionName = "RequestPool";

    /// <summary>
    /// Number of concurrent worker threads draining the queue.
    /// Defaults to the logical processor count.
    /// </summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Maximum number of requests that may be queued before
    /// <see cref="IRequestPool.EnqueueAsync"/> starts back-pressuring callers.
    /// Applies independently to each priority channel (High, Normal, Low).
    /// </summary>
    public int BoundedCapacity { get; set; } = 1_000;

    /// <summary>
    /// Behavior used when a bounded priority channel is full. The default <see cref="BoundedChannelFullMode.Wait"/>
    /// preserves back-pressure by waiting for capacity. Drop modes follow the built-in channel semantics; when a queued
    /// item is dropped by the channel, <see cref="OnItemDropped"/> is invoked for the dropped request context.
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    /// <summary>
    /// Optional callback invoked when <see cref="FullMode"/> causes a bounded channel to drop an item.
    /// The callback is best-effort and should not throw.
    /// </summary>
    public Action<RequestContext>? OnItemDropped { get; set; }

    /// <summary>
    /// Set to <see langword="true"/> only when exactly one producer calls <see cref="IRequestPool.EnqueueAsync"/>.
    /// Enabling this with multiple concurrent producers is unsafe because the channels may optimize for single-writer access.
    /// </summary>
    public bool SingleWriter { get; set; }

    /// <summary>
    /// Maximum time to spend draining queued and in-flight work after <see cref="RequestPoolService.StopAsync"/> starts.
    /// The default <see cref="Timeout.InfiniteTimeSpan"/> preserves the previous behavior of waiting until the queue drains.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Per-priority dequeue weights for weighted round-robin scheduling.
    /// Index matches <see cref="RequestPriority"/> integer values:
    /// [0] = Low, [1] = Normal, [2] = High.
    /// A higher value means more dequeue tokens per cycle.
    /// Defaults to <c>{ 1, 3, 5 }</c> — High gets ~56% of cycles, Normal ~33%, Low ~11%.
    /// </summary>
    /// <remarks>
    /// Set all values equal (e.g. <c>{ 1, 1, 1 }</c>) for pure round-robin across priorities.
    /// Use a very large High weight (e.g. <c>{ 1, 3, 100 }</c>) to approximate — but not
    /// fully reproduce — the previous strict-cascade behaviour while still guaranteeing
    /// that Low-priority requests eventually run.
    /// </remarks>
    public int[] PriorityWeights { get; set; } = [1, 3, 5];

    /// <summary>
    /// Enables per-partition round-robin scheduling within each priority.
    /// Leave disabled to use the original single-channel-per-priority behavior.
    /// </summary>
    public bool PartitionFairnessEnabled { get; set; }

    /// <summary>
    /// Optional per-partition bounded channel capacity used when
    /// <see cref="PartitionFairnessEnabled"/> is enabled. When <see langword="null"/>,
    /// each partition uses <see cref="BoundedCapacity"/>. Capacity applies independently
    /// per active partition, so total queue memory can grow with partition count.
    /// </summary>
    public int? PartitionCapacity { get; set; }

    /// <summary>
    /// How long an empty partition channel may remain idle before it is evicted.
    /// </summary>
    public TimeSpan PartitionIdleEvictionThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Time a queued Low or Normal request may wait before it is promoted by one priority tier.
    /// Leave <see langword="null"/> to disable priority aging.
    /// </summary>
    public TimeSpan? PriorityAgingThreshold { get; set; }

    /// <summary>
    /// How often queued requests are scanned for priority aging promotion.
    /// </summary>
    public TimeSpan PriorityAgingScanInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Time source used for priority aging. Defaults to <see cref="System.TimeProvider.System"/>.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// Maximum time in milliseconds that a single dispatcher invocation may run before its
    /// <see cref="System.Threading.CancellationToken"/> is cancelled.
    /// Set to <see cref="System.Threading.Timeout.Infinite"/> (-1) to disable the timeout (default).
    /// </summary>
    public int DispatchTimeoutMs { get; set; } = Timeout.Infinite;

    /// <summary>
    /// Maximum number of dispatcher attempts for a request. The default of <c>1</c>
    /// preserves no-retry behavior.
    /// </summary>
    public int MaxDispatchAttempts { get; set; } = 1;

    /// <summary>
    /// Optional retry predicate invoked after a failed attempt with the exception and
    /// the attempt number that just completed. Return <see langword="true"/> to retry.
    /// </summary>
    public Func<Exception, int, bool>? ShouldRetry { get; set; }

    /// <summary>
    /// Optional callback invoked when a request exhausts dispatch attempts or retry is denied.
    /// </summary>
    public Action<RequestContext, Exception>? OnDeadLetter { get; set; }

    /// <summary>
    /// Delay between failed attempts. The default <see cref="TimeSpan.Zero"/> retries immediately.
    /// </summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Optional factory for a Polly <see cref="ResiliencePipeline"/> used to wrap each dispatcher invocation.
    /// </summary>
    /// <remarks>
    /// The pipeline runs inside the built-in <see cref="MaxDispatchAttempts"/> retry loop, so a Polly retry strategy is
    /// applied once for each request-pool dispatch attempt. Do not combine Polly retries with
    /// <see cref="MaxDispatchAttempts"/> greater than <c>1</c> unless multiplicative retry behavior is intentional.
    /// </remarks>
    public Func<IServiceProvider, ResiliencePipeline>? ResiliencePipelineFactory { get; set; }

    /// <summary>
    /// Optional factory that returns a <see cref="TaskScheduler"/> for a given
    /// <see cref="RequestPriority"/>.  When set, each dispatcher invocation is
    /// started on the returned scheduler via
    /// <c>Task.Factory.StartNew(…, scheduler).Unwrap()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This lets you provide a dedicated thread pool (e.g. one with elevated or
    /// reduced thread priorities) for specific priority levels.  Leave <c>null</c>
    /// (the default) to run all dispatchers on <see cref="TaskScheduler.Default"/>.
    /// </para>
    /// <para>
    /// The factory is called <b>exactly once per <see cref="RequestPriority"/> value</b>
    /// (three calls total) during <see cref="RequestPoolService"/> construction.
    /// The returned scheduler instances are cached for the lifetime of the service.
    /// Return the same instance for multiple priorities if sharing a scheduler is intended.
    /// </para>
    /// <para>
    /// If a returned scheduler implements <see cref="IDisposable"/>, the service will
    /// dispose it automatically when <see cref="RequestPoolService.Dispose"/> is called.
    /// Do <b>not</b> dispose the scheduler externally after passing it through this factory.
    /// </para>
    /// </remarks>
    public Func<RequestPriority, TaskScheduler>? TaskSchedulerFactory { get; set; }

    /// <summary>
    /// Optional per-priority maximum number of concurrent dispatcher invocations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Index matches <see cref="RequestPriority"/> integer values:
    /// <c>[0]</c> = <see cref="RequestPriority.Low"/>,
    /// <c>[1]</c> = <see cref="RequestPriority.Normal"/>,
    /// <c>[2]</c> = <see cref="RequestPriority.High"/>.
    /// </para>
    /// <para>
    /// A positive value limits how many dispatcher invocations of that priority tier
    /// may run simultaneously, across all workers.  A value of <c>0</c> means no cap
    /// for that tier.  Leave the property <c>null</c> (the default) to apply no
    /// per-priority concurrency limits.
    /// </para>
    /// <para>
    /// This is complementary to <see cref="MaxConcurrency"/>, which caps the total
    /// number of concurrent dispatches across all priority tiers.
    /// <see cref="MaxConcurrentPerPriority"/> applies additional per-tier limits
    /// on top of that global cap.
    /// </para>
    /// </remarks>
    public int[]? MaxConcurrentPerPriority { get; set; }
}
