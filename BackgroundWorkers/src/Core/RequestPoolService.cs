using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using RequestProcessor.Diagnostics;
using RequestProcessor.Internal;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace RequestProcessor;

/// <summary>
/// Background service that owns three bounded priority channels (High / Normal / Low)
/// and a fixed pool of worker tasks.
/// Workers drain the channels using weighted round-robin, governed by
/// <see cref="RequestPoolOptions.PriorityWeights"/>, so lower-priority requests
/// are guaranteed to make progress even under sustained high-priority load.
/// All three channels share the same <see cref="RequestPoolOptions.BoundedCapacity"/>
/// limit, applied independently per channel.
/// </summary>
/// <remarks>
/// Registered as both <see cref="IRequestPool"/>, <see cref="IRequestPoolMonitor"/> (singleton),
/// and <see cref="IHostedService"/> so the host starts and stops workers automatically.
/// </remarks>
public sealed partial class RequestPoolService : BackgroundService, IRequestPool, IRequestPoolMonitor
{
    private readonly Channel<WorkItem> _highChannel;
    private readonly Channel<WorkItem> _normalChannel;
    private readonly Channel<WorkItem> _lowChannel;

    // Per-priority token budgets for weighted round-robin scheduling.
    // Index = (int)RequestPriority: 0=Low, 1=Normal, 2=High.
    // Initialised to PriorityWeights in the constructor; reset when all budgets are exhausted.
    private readonly int[] _priorityWeights;
    private readonly int[] _priorityBudgets;

    // Channel array indexed by (int)RequestPriority for O(1) lookup in the WRR loop.
    private readonly Channel<WorkItem>[] _priorityChannels; // [0]=Low, [1]=Normal, [2]=High

    private readonly ConcurrentDictionary<string, Lazy<PartitionQueue>>[] _priorityPartitions = [new(), new(), new()];
    private readonly int[] _partitionCursors = new int[3];
    private readonly Channel<byte> _partitionNotifications = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions
    {
        SingleWriter = false,
        SingleReader = false,
        AllowSynchronousContinuations = false,
    });
    private readonly BoundedChannelOptions _partitionChannelOptions;
    private readonly Action<WorkItem>? _itemDropped;
    private int _partitionWritersCompleted;
    private long _lastPartitionEvictionTimestamp;

    // Cached flat snapshots of each priority's partition collection.
    // Rebuilt under _snapshotUpdateLock on topology changes (partition add/remove).
    // Hot-path readers take a single volatile dereference — zero allocations, zero locks.
    private volatile KeyValuePair<string, Lazy<PartitionQueue>>[][] _partitionSnapshots = [[], [], []];
    private readonly Lock _snapshotUpdateLock = new();

    // Total items currently in partition channels (all priorities combined).
    // Maintained via Interlocked — replaces the O(3N) HasQueuedWork() scan.
    private int _partitionedItemCount;

    // Tracks in-flight drop callbacks so Dispose() can drain them before releasing resources.
    private int _inFlightDropCallbacks;

    private readonly IRequestDispatcher _dispatcher;
    private readonly ILogger<RequestPoolService> _logger;
    private readonly RequestPoolOptions _options;
    private readonly ResiliencePipeline? _resiliencePipeline;
    private readonly RequestPoolMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private ITimer? _priorityAgingTimer;
    private int _priorityAgingScanRunning;
    private int _disposed;

    private const int ClaimedPriorityState = 3;
    private const string DefaultPartitionKey = "__default";

    // Cached TaskScheduler instances, resolved once per priority at construction.
    // null when no TaskSchedulerFactory is configured.
    private readonly TaskScheduler?[]? _cachedSchedulers;

    // Optional per-priority concurrency gates, null per slot when uncapped.
    private readonly SemaphoreSlim?[] _concurrencyGates;

    // Completed by ExecuteAsync once it has started running on the ThreadPool.
    // BackgroundService.StartAsync uses Task.Run internally; callers that need to know
    // the service is actually running (e.g., tests) can await this before proceeding.
    private readonly TaskCompletionSource _executingTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _drainCts = new();

    /// <summary>
    /// Completes when <see cref="ExecuteAsync"/> has started and workers are running.
    /// Useful in tests to avoid the race where <see cref="Microsoft.Extensions.Hosting.BackgroundService.StopAsync"/>
    /// cancels the pending <c>Task.Run</c> before <see cref="ExecuteAsync"/> gets a thread.
    /// </summary>
    internal Task WhenStarted => _executingTcs.Task;

    // Associates a queued-but-not-yet-dispatched request with its priority,
    // enqueue timestamp and the CancellationTokenSource that drives per-request cancellation.
    private readonly record struct PendingEntry(
        RequestPriority Priority,
        CancellationTokenSource Cts,
        long EnqueuedTimestampTicks,
        WorkItem Item);

    // Control characters U+0000..U+001F. Stored as a ReadOnlySpan<char> over a string literal
    // so the runtime can use a vectorized IndexOfAny without any startup LINQ/array allocation.
    private static ReadOnlySpan<char> ControlChars =>
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F";

    // Requests that are queued but not yet dispatched; keyed by RequestId.
    // Workers remove the entry when they pick up an item; TryCancelRequest
    // and CancelAllRequests remove it and signal cancellation.
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);

    // In-memory counters mirrored alongside the OTel metrics so that
    // GetSnapshot() can return a consistent struct without querying the meter.
    private long _totalEnqueued;
    private long _totalCompleted;
    private long _totalFailed;
    private long _totalCancelled;
    private int _activeWorkerCount;

    // Carries the enqueue-time trace context into the worker thread so the
    // dispatch activity can be parented to the caller's span. Attempt is 1-based:
    // the public enqueue path creates attempt 1, and each retry increments it.
    private sealed class WorkItemAgingState(RequestPriority priority)
    {
        public int PriorityOrClaimed = (int)priority;
        public int CancellationTokenSourceReturned;
    }

    private readonly record struct WorkItem(
        RequestContext Context,
        IWorkItemCallback Callback,
        ActivityContext ParentActivityContext,
        CancellationToken RequestCancellationToken,
        CancellationTokenSource RequestCancellationTokenSource,
        int Attempt,
        RequestPriority QueuedPriority,
        WorkItemAgingState AgingState);

    private sealed class PartitionQueue(Channel<WorkItem> channel, long lastTouchedTimestamp)
    {
        private int _activeWriters;
        private int _evicted;

        public Channel<WorkItem> Channel { get; } = channel;
        public long LastTouchedTimestamp = lastTouchedTimestamp;

        public bool TryAcquireWriter()
        {
            while (Volatile.Read(ref _evicted) == 0)
            {
                var current = Volatile.Read(ref _activeWriters);
                if (Interlocked.CompareExchange(ref _activeWriters, current + 1, current) != current)
                    continue;

                if (Volatile.Read(ref _evicted) == 0)
                    return true;

                ReleaseWriter();
                return false;
            }

            return false;
        }

        public void ReleaseWriter() => Interlocked.Decrement(ref _activeWriters);

        public bool TryMarkEvicted()
        {
            if (Channel.Reader.Count != 0)
                return false;

            if (Interlocked.CompareExchange(ref _evicted, 1, 0) != 0)
                return false;

            if (Volatile.Read(ref _activeWriters) == 0 && Channel.Reader.Count == 0)
                return true;

            Interlocked.CompareExchange(ref _evicted, 0, 1);
            return false;
        }
    }

    public RequestPoolService(
        IRequestDispatcher dispatcher,
        IOptions<RequestPoolOptions> options,
        IMeterFactory meterFactory,
        ILogger<RequestPoolService> logger,
        IServiceProvider serviceProvider)
    {
        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger;
        _resiliencePipeline = _options.ResiliencePipelineFactory?.Invoke(serviceProvider);
        _timeProvider = _options.TimeProvider ?? TimeProvider.System;

        var channelOptions = new BoundedChannelOptions(_options.BoundedCapacity)
        {
            SingleWriter = _options.SingleWriter,
            SingleReader = false,
            FullMode = _options.FullMode,
            AllowSynchronousContinuations = false,
        };

        _partitionChannelOptions = new BoundedChannelOptions(_options.PartitionCapacity ?? _options.BoundedCapacity)
        {
            SingleWriter = _options.SingleWriter,
            SingleReader = false,
            FullMode = _options.FullMode,
            AllowSynchronousContinuations = false,
        };

        // Always register the drop callback when the channel might drop items, so we can fire
        // the user's completion callback (otherwise Task<RequestResult>-style awaiters hang)
        // and return the per-request CTS to the pool.
        var mayDrop = _options.FullMode is BoundedChannelFullMode.DropOldest
            or BoundedChannelFullMode.DropNewest
            or BoundedChannelFullMode.DropWrite;
        _itemDropped = (mayDrop || _options.OnItemDropped is not null) ? OnChannelItemDropped : null;
        _highChannel = Channel.CreateBounded(channelOptions, _itemDropped);
        _normalChannel = Channel.CreateBounded(channelOptions, _itemDropped);
        _lowChannel = Channel.CreateBounded(channelOptions, _itemDropped);

        _metrics = new RequestPoolMetrics(meterFactory, GetTotalQueueDepth, ObserveActivePartitions);

        _priorityWeights = (int[])_options.PriorityWeights.Clone(); // defensive copy
        _priorityBudgets = (int[])_priorityWeights.Clone();          // start with full budget
        _priorityChannels = [_lowChannel, _normalChannel, _highChannel]; // index = (int)priority

        // Resolve schedulers once per priority and cache them for the service lifetime.
        if (_options.TaskSchedulerFactory is { } factory)
        {
            _cachedSchedulers = new TaskScheduler?[3];
            _cachedSchedulers[(int)RequestPriority.Low] = factory(RequestPriority.Low);
            _cachedSchedulers[(int)RequestPriority.Normal] = factory(RequestPriority.Normal);
            _cachedSchedulers[(int)RequestPriority.High] = factory(RequestPriority.High);
        }

        // Initialise per-priority concurrency gates (null slot = uncapped).
        _concurrencyGates = new SemaphoreSlim?[3];
        if (_options.MaxConcurrentPerPriority is { } caps)
        {
            for (int i = 0; i < 3; i++)
            {
                if (caps.Length > i && caps[i] > 0)
                    _concurrencyGates[i] = new SemaphoreSlim(caps[i], caps[i]);
            }
        }
    }

    // -------------------------------------------------------------------------
    // IRequestPool
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public ValueTask EnqueueAsync(RequestContext context, RequestCompletedCallback onCompleted, CancellationToken cancellationToken = default) =>
        EnqueueCoreAsync(context, new WorkItemCallback(onCompleted), cancellationToken);

    /// <inheritdoc/>
    public ValueTask EnqueueAsync<TState>(RequestContext context, TState state, Func<TState, RequestResult, ValueTask> callback, CancellationToken cancellationToken = default) =>
        EnqueueCoreAsync(context, new WorkItemCallback<TState>(state, callback), cancellationToken);

    private async ValueTask EnqueueCoreAsync(RequestContext context, IWorkItemCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrEmpty(context.RequestId) || context.RequestId.Length > 256)
            throw new ArgumentException("RequestId must be between 1 and 256 characters.", nameof(context));

        if (context.RequestId.AsSpan().IndexOfAny(ControlChars) >= 0)
            throw new ArgumentException("RequestId must not contain control characters.", nameof(context));

        // Fail fast on duplicate RequestId— a queued request with the same ID would
        // have its CancellationTokenSource replaced, silently breaking cancellation.
        // Link to _drainCts so the per-request token fires on drain or TryCancelRequest.
        // CreateLinkedTokenSource is not poolable (TryReset returns false for linked CTS),
        // but the enqueue path is the caller's path — moving allocation here removes it from
        // the worker's hot dispatch path.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_drainCts.Token);

        using var activity = RequestPoolDiagnostics.ActivitySource.StartActivity("requestpool.enqueue", ActivityKind.Producer);
        activity?.SetTag("request.id", context.RequestId);
        activity?.SetTag("request.priority", context.Priority.ToString());
        activity?.SetTag("correlation.id", context.CorrelationId);

        LogEnqueuing(context.Priority, context.RequestId);

        // Capture activity context *after* starting the activity so the consumer
        // span can be parented to this span rather than the caller's span.
        var parentContext = activity?.Context ?? Activity.Current?.Context ?? default;
        var item = new WorkItem(
            context,
            callback,
            parentContext,
            cts.Token,
            cts,
            Attempt: 1,
            QueuedPriority: context.Priority,
            new WorkItemAgingState(context.Priority));

        if (!_pending.TryAdd(context.RequestId, new PendingEntry(context.Priority, cts, _timeProvider.GetTimestamp(), item)))
        {
            // Linked CTS must be disposed rather than pooled: TryReset() may succeed but the
            // instance remains linked to _drainCts, leaving a stale registration in the pool.
            cts.Dispose();
            throw new ArgumentException($"A request with RequestId '{context.RequestId}' is already enqueued.", nameof(context));
        }

        try
        {
            await WriteWorkItemAsync(item, cancellationToken);
            Interlocked.Increment(ref _totalEnqueued);
            _metrics.Enqueued.Add(1);
        }
        catch
        {
            // WriteAsync was cancelled or the channel was completed — clean up the CTS
            // so it doesn't linger in _pending and cause a false-positive TryCancelRequest.
            if (_pending.TryRemove(context.RequestId, out var orphaned))
                TryReturnRequestCancellationTokenSource(orphaned.Item);
            else
                TryReturnRequestCancellationTokenSource(item);
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // IRequestPoolMonitor
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public RequestPoolStats GetSnapshot()
    {
        int hi = GetQueueDepth(RequestPriority.High);
        int no = GetQueueDepth(RequestPriority.Normal);
        int lo = GetQueueDepth(RequestPriority.Low);
        return new(
            QueueDepthHigh: hi,
            QueueDepthNormal: no,
            QueueDepthLow: lo,
            TotalQueueDepth: hi + no + lo,
            ActiveWorkers: Volatile.Read(ref _activeWorkerCount),
            TotalEnqueued: Volatile.Read(ref _totalEnqueued),
            TotalCompleted: Volatile.Read(ref _totalCompleted),
            TotalFailed: Volatile.Read(ref _totalFailed),
            TotalCancelled: Volatile.Read(ref _totalCancelled),
            MaxConcurrency: _options.MaxConcurrency,
            BoundedCapacity: _options.BoundedCapacity);
    }

    /// <inheritdoc/>
    public bool TryCancelRequest(string requestId)
    {
        if (_pending.TryRemove(requestId, out var entry))
        {
            entry.Cts.Cancel();
            TryReturnRequestCancellationTokenSource(entry.Item);
            return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public int CancelAllRequests(RequestPriority? priority = null)
    {
        int count = 0;
        foreach (var id in _pending.Keys.ToArray())
        {
            // When a priority filter is given, peek at the entry's priority before
            // attempting removal.  A concurrent worker may remove the entry between
            // TryGetValue and TryRemove — TryRemove returning false means the item
            // was already dispatched, which is the correct outcome (don't cancel it).
            // This avoids the zombie-entry bug that occurred when the old code removed
            // a non-matching entry and then tried to re-add it (worker could pick it up
            // in the gap, leaving the re-added entry unreachable in _pending forever).
            if (priority.HasValue)
            {
                if (!_pending.TryGetValue(id, out var peek) || peek.Priority != priority.Value)
                    continue;
            }

            if (!_pending.TryRemove(id, out var entry))
                continue;

            entry.Cts.Cancel();
            TryReturnRequestCancellationTokenSource(entry.Item);
            count++;
        }
        return count;
    }

    // -------------------------------------------------------------------------
    // BackgroundService
    // -------------------------------------------------------------------------

    /// <summary>
    /// Spins up <see cref="RequestPoolOptions.MaxConcurrency"/> worker tasks that
    /// each drain all three priority channels independently.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogPoolStarting(_options.MaxConcurrency, _options.BoundedCapacity);
        StartPriorityAgingTimer();

        // Signal WhenStarted before launching workers so that callers awaiting it know
        // that this method is executing on the ThreadPool and StopAsync will no longer
        // cancel the underlying Task.Run before workers get a chance to run.
        _executingTcs.TrySetResult();

        var workers = new Task[_options.MaxConcurrency];
        for (int i = 0; i < workers.Length; i++)
            workers[i] = RunWorkerAsync(i, _drainCts.Token);

        await Task.WhenAll(workers);

        LogPoolStopped();
    }

    /// <summary>Stops accepting new items and lets the host shut down cleanly.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _priorityAgingTimer?.Dispose();
        _priorityAgingTimer = null;

        _highChannel.Writer.TryComplete();
        _normalChannel.Writer.TryComplete();
        _lowChannel.Writer.TryComplete();

        var drainCancellationRegistration = cancellationToken.Register(
            static cts => ((CancellationTokenSource)cts!).Cancel(),
            _drainCts);

        try
        {
            if (_options.PartitionFairnessEnabled)
            {
                Volatile.Write(ref _partitionWritersCompleted, 1);
                foreach (var partitions in _priorityPartitions)
                {
                    foreach (var partition in partitions.Values)
                    {
                        if (partition.IsValueCreated)
                            partition.Value.Channel.Writer.TryComplete();
                    }
                }

                _partitionNotifications.Writer.TryComplete();
            }

            if (_options.DrainTimeout != Timeout.InfiniteTimeSpan)
                _drainCts.CancelAfter(_options.DrainTimeout);

            await base.StopAsync(cancellationToken);
        }
        finally
        {
            drainCancellationRegistration.Dispose();
        }
    }

    public override void Dispose()
    {
        // Set the disposed flag BEFORE disposing the aging timer. This ensures that any
        // QueuePriorityAgingScan callback already queued to the thread pool (between the
        // timer firing and the timer.Dispose call) sees _disposed == 1 and bails out
        // before touching _drainCts / _metrics.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_cachedSchedulers is not null)
        {
            for (int i = 0; i < _cachedSchedulers.Length; i++)
            {
                var scheduler = _cachedSchedulers[i];
                if (scheduler is not IDisposable disposable)
                    continue;

                var seen = false;
                for (int j = 0; j < i; j++)
                {
                    if (ReferenceEquals(scheduler, _cachedSchedulers[j]))
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                    disposable.Dispose();
            }
        }

        _priorityAgingTimer?.Dispose();

        // Ensure any in-flight aging scan can exit promptly. StopAsync only cancels _drainCts
        // when DrainTimeout is finite, so a scan blocked on `WriteAsync(item, drainToken)`
        // would otherwise outlive the 5s spin-wait below and hit ObjectDisposedException once
        // we dispose _drainCts.
        try { _drainCts.Cancel(); }
        catch (ObjectDisposedException) { }

        // Wait briefly for any in-flight aging scan to finish so we don't dispose
        // _drainCts or _metrics while the scan is still touching them.
        var spinSw = Stopwatch.StartNew();
        while (Volatile.Read(ref _priorityAgingScanRunning) != 0 && spinSw.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(10);

        // Wait briefly for ExecuteAsync (and therefore all workers) to finish. Under the
        // normal IHostedService lifecycle StopAsync awaits ExecuteAsync, but if the host's
        // shutdown token fired first or Dispose was invoked without a prior StopAsync,
        // workers may still be running. Touching _metrics / _concurrencyGates / _drainCts
        // below would otherwise race with worker code paths (ProcessItemAsync,
        // DispatchWithSchedulerAsync) and surface as ObjectDisposedException.
        var executeTask = ExecuteTask;
        if (executeTask is not null && !executeTask.IsCompleted)
        {
            try { executeTask.Wait(TimeSpan.FromSeconds(5)); }
            catch { /* swallow worker exceptions during shutdown */ }
        }

        // Drain any in-flight drop callbacks before releasing _metrics and _drainCts.
        // Use a 5-second timeout (same as the aging-scan and ExecuteTask waits above) to
        // avoid an indefinite hang if a user callback or a ThreadPool saturation prevents
        // the Task.Run lambda from completing.
        var dropSpinSw = Stopwatch.StartNew();
        var spinWait = new SpinWait();
        while (Volatile.Read(ref _inFlightDropCallbacks) > 0 && dropSpinSw.Elapsed < TimeSpan.FromSeconds(5))
            spinWait.SpinOnce();

        // Return any cancellation token sources still held by undrained pending items
        // (channels that never delivered to a worker due to drain timeout or capacity).
        // Wrap each return so a single misbehaving CTS does not leak the rest.
        foreach (var entry in _pending.Values)
        {
            try { TryReturnRequestCancellationTokenSource(entry.Item); }
            catch { /* Dispose must be resilient to individual CTS return failures */ }
        }
        _pending.Clear();

        foreach (var gate in _concurrencyGates)
            gate?.Dispose();

        _drainCts.Dispose();
        _metrics.Dispose();
        base.Dispose();
    }

    // -------------------------------------------------------------------------
    // Worker loop
    // -------------------------------------------------------------------------

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        LogWorkerStarted(workerId);

        // Per-worker reusable wait buffer; avoids a Task<bool>[] allocation per idle wakeup.
        // Each worker owns its own buffer — WaitToReadAnyAsync is called concurrently by all workers,
        // so a single shared field would be unsafe.
        var waitBuffer = new Task<bool>[3];
        var waitTasks = new Task<bool>?[3];

        // Workers drain after Writer.Complete() until all channels empty or DrainTimeout cancels.
        while (!stoppingToken.IsCancellationRequested)
        {
            if (TryReadByPriority(out var item))
            {
                await ProcessItemAsync(workerId, item, stoppingToken);
                continue;
            }

            // All channels currently empty — wait until one signals data or is completed.
            bool morePossible = _options.PartitionFairnessEnabled
                ? await WaitToReadPartitionedAsync(stoppingToken)
                : await WaitToReadAnyAsync(waitBuffer, waitTasks, stoppingToken);
            if (!morePossible) break;
        }

        LogWorkerFinished(workerId);
    }

    /// <summary>
    /// Tries to read from a priority channel using weighted round-robin.
    /// Scans High→Normal→Low gated by a per-priority token budget.
    /// When all budgets are exhausted but work remains, resets budgets and retries once.
    /// Returns <c>false</c> only when all channels are currently empty.
    /// </summary>
    private bool TryReadByPriority(out WorkItem item)
    {
        if (TryReadWeighted(out item)) return true;

        // All budgets exhausted — reset and retry if any channel still has work.
        bool hasWork = HasQueuedWork();

        if (!hasWork)
        {
            item = default;
            return false;
        }

        ResetBudgets();
        return TryReadWeighted(out item);
    }

    // Scans High (index 2) → Normal (1) → Low (0), claiming one budget token per channel
    // via lock-free CAS.  Refunds the token if the channel turns out to be empty.
    private bool TryReadWeighted(out WorkItem item)
    {
        for (int p = 2; p >= 0; p--)
        {
            int b = Volatile.Read(ref _priorityBudgets[p]);
            if (b <= 0) continue;

            // Atomically claim one token.  If another worker won the CAS, re-try this level.
            if (Interlocked.CompareExchange(ref _priorityBudgets[p], b - 1, b) != b)
            {
                p++; // stay on the same priority index next iteration
                continue;
            }

            if (TryReadPriority(p, out item))
                return true;

            Interlocked.Increment(ref _priorityBudgets[p]); // refund: channel was empty
        }

        item = default;
        return false;
    }

    private void ResetBudgets()
    {
        Interlocked.Exchange(ref _priorityBudgets[0], _priorityWeights[0]); // Low
        Interlocked.Exchange(ref _priorityBudgets[1], _priorityWeights[1]); // Normal
        Interlocked.Exchange(ref _priorityBudgets[2], _priorityWeights[2]); // High
    }

    private bool TryReadPriority(int priorityIndex, out WorkItem item)
    {
        if (!_options.PartitionFairnessEnabled)
            return _priorityChannels[priorityIndex].Reader.TryRead(out item);

        // Single volatile read — zero allocations, zero locks.
        var partitions = _partitionSnapshots[priorityIndex];
        if (partitions.Length == 0)
        {
            item = default;
            return false;
        }

        var start = Interlocked.Increment(ref _partitionCursors[priorityIndex]) & int.MaxValue;
        for (int i = 0; i < partitions.Length; i++)
        {
            var partitionLazy = partitions[(start + i) % partitions.Length].Value;
            if (!partitionLazy.IsValueCreated)
                continue;

            var partition = partitionLazy.Value;
            if (partition.Channel.Reader.TryRead(out item))
            {
                Interlocked.Decrement(ref _partitionedItemCount);
                Volatile.Write(ref partition.LastTouchedTimestamp, _timeProvider.GetTimestamp());
                return true;
            }
        }

        item = default;
        return false;
    }

    private bool HasQueuedWork() => _options.PartitionFairnessEnabled
        ? Volatile.Read(ref _partitionedItemCount) > 0
        : GetTotalQueueDepth() > 0;

    private int GetTotalQueueDepth() => GetQueueDepth(RequestPriority.High) + GetQueueDepth(RequestPriority.Normal) + GetQueueDepth(RequestPriority.Low);

    private int GetQueueDepth(RequestPriority priority)
    {
        var priorityIndex = (int)priority;
        if (!_options.PartitionFairnessEnabled)
            return _priorityChannels[priorityIndex].Reader.Count;

        var total = 0;
        foreach (var partition in _priorityPartitions[priorityIndex].Values)
        {
            if (partition.IsValueCreated)
                total += partition.Value.Channel.Reader.Count;
        }

        return total;
    }

    private IEnumerable<Measurement<int>> ObserveActivePartitions()
    {
        yield return new Measurement<int>(_priorityPartitions[(int)RequestPriority.Low].Count, new KeyValuePair<string, object?>("priority", RequestPriority.Low.ToString()));
        yield return new Measurement<int>(_priorityPartitions[(int)RequestPriority.Normal].Count, new KeyValuePair<string, object?>("priority", RequestPriority.Normal.ToString()));
        yield return new Measurement<int>(_priorityPartitions[(int)RequestPriority.High].Count, new KeyValuePair<string, object?>("priority", RequestPriority.High.ToString()));
    }

    private async ValueTask<bool> WaitToReadPartitionedAsync(CancellationToken drainToken)
    {
        while (!drainToken.IsCancellationRequested)
        {
            if (HasQueuedWork())
                return true;

            if (Volatile.Read(ref _partitionWritersCompleted) != 0)
                return false;

            try
            {
                await _partitionNotifications.Reader.ReadAsync(drainToken);
            }
            catch (ChannelClosedException)
            {
                return false;
            }
            catch (OperationCanceledException) when (drainToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Waits asynchronously until at least one priority channel has an item to read
    /// or all channels have been completed and drained.
    /// </summary>
    /// <param name="buffer">
    /// A caller-owned 3-slot buffer reused across calls to avoid per-wakeup allocation.
    /// Must be exclusive to one worker (the method is called concurrently by multiple workers,
    /// each with its own buffer).
    /// </param>
    /// <returns>
    /// <c>true</c> if data is (likely) available; <c>false</c> if all channels are done.
    /// </returns>
    private async ValueTask<bool> WaitToReadAnyAsync(Task<bool>[] buffer, Task<bool>?[] waitTasks, CancellationToken drainToken)
    {
        while (!drainToken.IsCancellationRequested)
        {
            int taskCount = 0;

            for (int p = 0; p < _priorityChannels.Length; p++)
            {
                var reader = _priorityChannels[p].Reader;
                if (reader.Completion.IsCompleted)
                {
                    waitTasks[p] = null;
                    continue;
                }

                if (waitTasks[p] is not { IsCompleted: false })
                    waitTasks[p] = reader.WaitToReadAsync(drainToken).AsTask();

                buffer[taskCount++] = waitTasks[p]!;
            }

            if (taskCount == 0)
                return false; // all channels fully drained

            var first = taskCount switch
            {
                1 => buffer[0],
                2 => await Task.WhenAny(buffer[0], buffer[1]),
                _ => await Task.WhenAny(buffer),
            };

            bool hasData;
            try
            {
                hasData = await first;
            }
            catch (OperationCanceledException) when (drainToken.IsCancellationRequested)
            {
                return false;
            }

            if (hasData)
                return true;

            for (int p = 0; p < waitTasks.Length; p++)
            {
                if (ReferenceEquals(waitTasks[p], first))
                {
                    waitTasks[p] = null;
                    break;
                }
            }

            // One channel just completed+drained; loop to wait for the remaining active channels.
        }

        return false;
    }

    private async ValueTask WriteWorkItemAsync(WorkItem item, CancellationToken cancellationToken = default)
    {
        if (!_options.PartitionFairnessEnabled)
        {
            await _priorityChannels[(int)item.Context.Priority].Writer.WriteAsync(item, cancellationToken);
            return;
        }

        if (Volatile.Read(ref _partitionWritersCompleted) != 0)
            throw new ChannelClosedException();

        EvictIdlePartitionsIfDue();

        var priorityIndex = (int)item.Context.Priority;
        var partitionKey = item.Context.PartitionKey ?? DefaultPartitionKey;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _timeProvider.GetTimestamp();

            if (!_priorityPartitions[priorityIndex].TryGetValue(partitionKey, out var partitionLazy))
            {
                partitionLazy = _priorityPartitions[priorityIndex].GetOrAdd(
                    partitionKey,
                    static (_, state) => new Lazy<PartitionQueue>(
                        () => state.Service.CreatePartitionQueue(state.Now),
                        LazyThreadSafetyMode.ExecutionAndPublication),
                    (Service: this, Now: now));
                InvalidatePartitionSnapshot(priorityIndex);
            }

            var partition = partitionLazy.Value;

            if (!partition.TryAcquireWriter())
            {
                ((ICollection<KeyValuePair<string, Lazy<PartitionQueue>>>)_priorityPartitions[priorityIndex])
                    .Remove(new KeyValuePair<string, Lazy<PartitionQueue>>(partitionKey, partitionLazy));
                InvalidatePartitionSnapshot(priorityIndex);
                continue;
            }

            try
            {
                Volatile.Write(ref partition.LastTouchedTimestamp, now);
                // Increment BEFORE WriteAsync so the count is never transiently negative.
                // If WriteAsync throws (cancelled or channel full), decrement back.
                Interlocked.Increment(ref _partitionedItemCount);
                try
                {
                    await partition.Channel.Writer.WriteAsync(item, cancellationToken);
                }
                catch
                {
                    Interlocked.Decrement(ref _partitionedItemCount);
                    throw;
                }
                Volatile.Write(ref partition.LastTouchedTimestamp, _timeProvider.GetTimestamp());
                // Guard against calling TryWrite after TryComplete (called in StopAsync). A
                // completed writer silently returns false, which would leave the item unread.
                if (Volatile.Read(ref _partitionWritersCompleted) == 0)
                    _partitionNotifications.Writer.TryWrite(0);
                return;
            }
            finally
            {
                partition.ReleaseWriter();
            }
        }
    }

    private PartitionQueue CreatePartitionQueue(long now) =>
        new(Channel.CreateBounded(_partitionChannelOptions, _itemDropped), now);

    private void InvalidatePartitionSnapshot(int priorityIndex)
    {
        lock (_snapshotUpdateLock)
        {
            var updated = new KeyValuePair<string, Lazy<PartitionQueue>>[3][];
            var current = _partitionSnapshots;

            for (int i = 0; i < 3; i++)
            {
                updated[i] = i == priorityIndex
                    ? [.. _priorityPartitions[priorityIndex]]
                    : current[i];
            }

            _partitionSnapshots = updated;
        }
    }

    private void EvictIdlePartitionsIfDue()
    {
        var threshold = _options.PartitionIdleEvictionThreshold;
        if (threshold == TimeSpan.Zero)
            return;

        var now = _timeProvider.GetTimestamp();
        var last = Volatile.Read(ref _lastPartitionEvictionTimestamp);
        if (last != 0 && _timeProvider.GetElapsedTime(last, now) < TimeSpan.FromSeconds(10))
            return;

        if (Interlocked.CompareExchange(ref _lastPartitionEvictionTimestamp, now, last) != last)
            return;

        for (int p = 0; p < _priorityPartitions.Length; p++)
        {
            bool anyRemoved = false;
            foreach (var pair in _priorityPartitions[p].ToArray())
            {
                if (!pair.Value.IsValueCreated)
                    continue;

                var partition = pair.Value.Value;
                if (_timeProvider.GetElapsedTime(Volatile.Read(ref partition.LastTouchedTimestamp), now) <= threshold)
                    continue;

                if (!partition.TryMarkEvicted())
                    continue;

                if (!((ICollection<KeyValuePair<string, Lazy<PartitionQueue>>>)_priorityPartitions[p]).Remove(pair))
                    continue;

                partition.Channel.Writer.TryComplete();
                _metrics.PartitionEvicted.Add(1);
                anyRemoved = true;
            }
            if (anyRemoved)
                InvalidatePartitionSnapshot(p);
        }
    }

    // -------------------------------------------------------------------------
    // Item processing
    // -------------------------------------------------------------------------

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ProcessItemAsync(int workerId, WorkItem item, CancellationToken cancellationToken)
    {
        // Transition the first attempt from "queued" to "in-flight": remove from pending dict.
        // Retry items bypass _pending so they do not trip duplicate-ID detection or rent a new CTS.
        bool returnRequestCancellationTokenSource = item.Attempt != 1;
        try
        {
            bool wasCancelled = false;
            if (item.Attempt == 1)
            {
                if (Interlocked.CompareExchange(ref item.AgingState.PriorityOrClaimed, ClaimedPriorityState, (int)item.QueuedPriority) != (int)item.QueuedPriority)
                    return;

                returnRequestCancellationTokenSource = true;
                _pending.TryRemove(item.Context.RequestId, out _);
                wasCancelled = item.RequestCancellationToken.IsCancellationRequested;
            }

            if (wasCancelled)
            {
                Interlocked.Increment(ref _totalCancelled);
                _metrics.Cancelled.Add(1);
                LogRequestCancelledBeforeDispatch(item.Context.RequestId);
                await InvokeCallbackAsync(item, new RequestResult(
                    item.Context.RequestId,
                    Success: false,
                    Output: null,
                    Error: new OperationCanceledException("Request was cancelled before dispatch.")));
                return;
            }

            LogWorkerProcessing(workerId, item.Context.Priority, item.Context.RequestId);

            using var correlationScope = item.Context.CorrelationId is { } correlationId
                ? _logger.BeginScope("{CorrelationId}", correlationId)
                : null;

            // Consumer span is parented to the enqueue span, forming a
            // producer→consumer pair in distributed traces.
            using var activity = RequestPoolDiagnostics.ActivitySource.StartActivity("requestpool.process", ActivityKind.Consumer, parentContext: item.ParentActivityContext);
            activity?.SetTag("request.id", item.Context.RequestId);
            activity?.SetTag("request.priority", item.Context.Priority.ToString());
            activity?.SetTag("correlation.id", item.Context.CorrelationId);

            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag("worker.id", workerId);
                activity.SetTag("attempt", item.Attempt);
            }

            Interlocked.Increment(ref _activeWorkerCount);
            _metrics.ActiveWorkers.Add(1);
            var started = _timeProvider.GetTimestamp();

            RequestResult result;
            try
            {
                result = await DispatchFastOrFullAsync(item, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
                LogRequestCancelledDuringShutdown(item.Context.RequestId);
                return;
            }
            catch (Exception ex)
            {
                LogDispatcherThrewForRequest(item.Context.RequestId, ex);
                result = new RequestResult(item.Context.RequestId, Success: false, Output: null, Error: ex);
            }
            finally
            {
                var elapsedMs = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
                _metrics.ProcessingDuration.Record(elapsedMs, tag: new("priority", item.Context.Priority.ToString()));
                Interlocked.Decrement(ref _activeWorkerCount);
                _metrics.ActiveWorkers.Add(-1);
            }

            if (!result.Success)
            {
                var finalException = result.Error ?? new InvalidOperationException($"Request '{item.Context.RequestId}' failed without an exception.");
                if (await TryScheduleRetryAsync(item, finalException, cancellationToken))
                {
                    returnRequestCancellationTokenSource = false;
                    activity?.SetTag("outcome", "retry");
                    return;
                }

                InvokeDeadLetter(item.Context, finalException);
            }

            if (result.Success)
                Interlocked.Increment(ref _totalCompleted);
            else
                Interlocked.Increment(ref _totalFailed);

            var outcome = result.Success ? "success" : "failure";
            _metrics.Completed.Add(1, tag: new("outcome", outcome));
            activity?.SetTag("outcome", outcome);

            if (!result.Success)
                activity?.SetStatus(ActivityStatusCode.Error, result.Error?.Message);

            await InvokeCallbackAsync(item, result);
        }
        finally
        {
            if (returnRequestCancellationTokenSource)
                TryReturnRequestCancellationTokenSource(item);
        }
    }

    private async Task<bool> TryScheduleRetryAsync(WorkItem item, Exception exception, CancellationToken drainToken)
    {
        if (item.Attempt >= _options.MaxDispatchAttempts)
            return false;

        if (_options.ShouldRetry is { } shouldRetry)
        {
            bool retry;
            try
            {
                retry = shouldRetry(exception, item.Attempt);
            }
            catch (Exception ex)
            {
                LogRetryPredicateThrew(ex, item.Context.RequestId);
                return false;
            }

            if (!retry)
                return false;
        }

        try
        {
            if (_options.RetryBackoff > TimeSpan.Zero)
            {
                using var retryDelayCts = CancellationTokenSource.CreateLinkedTokenSource(drainToken, item.RequestCancellationToken);
                await Task.Delay(_options.RetryBackoff, retryDelayCts.Token);
            }

            var retryItem = item with { Attempt = item.Attempt + 1 };
            await WriteWorkItemAsync(retryItem, drainToken);
        }
        catch (OperationCanceledException) when (drainToken.IsCancellationRequested)
        {
            LogRequestCancelledDuringShutdown(item.Context.RequestId);
            // Returning false lets ProcessItemAsync fire the user callback with the
            // original failure so awaiters do not hang when shutdown interrupts retry.
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }

        _metrics.Retry.Add(1, tag: new("priority", item.Context.Priority.ToString()));
        LogRetryingRequest(item.Context.RequestId, item.Attempt + 1, _options.MaxDispatchAttempts);
        return true;
    }

    private void InvokeDeadLetter(RequestContext context, Exception exception)
    {
        _metrics.DeadLetter.Add(1, tag: new("priority", context.Priority.ToString()));

        try
        {
            _options.OnDeadLetter?.Invoke(context, exception);
        }
        catch (Exception ex)
        {
            LogDeadLetterCallbackThrew(ex, context.RequestId);
        }
    }

    /// <summary>
    /// Fast path: bypasses intermediate state machines when no gate, timeout, custom scheduler,
    /// or resilience pipeline is configured. Delegates directly to
    /// <see cref="IRequestDispatcher.DispatchAsync"/> for minimum overhead.
    /// Falls back to the full <see cref="DispatchWithSchedulerAsync"/> path otherwise.
    /// </summary>
    private ValueTask<RequestResult> DispatchFastOrFullAsync(WorkItem item, CancellationToken drainToken)
    {
        var scheduler = _cachedSchedulers?[(int)item.Context.Priority];
        if (_concurrencyGates[(int)item.Context.Priority] is null
            && (scheduler is null || scheduler == TaskScheduler.Default)
            && item.Context.Timeout is null
            && _options.DispatchTimeoutMs <= 0
            && _resiliencePipeline is null)
        {
            // item.RequestCancellationToken already incorporates drain+TryCancelRequest;
            // no additional linking is needed on this path.
            return _dispatcher.DispatchAsync(item.Context, item.RequestCancellationToken);
        }

        return DispatchWithSchedulerAsync(item, drainToken);
    }

    /// <summary>
    /// Invokes the dispatcher, optionally on a priority-specific <see cref="TaskScheduler"/>
    /// when <see cref="RequestPoolOptions.TaskSchedulerFactory"/> is configured, and optionally
    /// gated by a per-priority <see cref="SemaphoreSlim"/> from
    /// <see cref="RequestPoolOptions.MaxConcurrentPerPriority"/>.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RequestResult> DispatchWithSchedulerAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var gate = _concurrencyGates[(int)item.Context.Priority];
        var gateAcquired = false;

        if (gate is not null)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
        }
        try
        {
            CancellationTokenSource? timeoutCts = null;
            // item.RequestCancellationToken is pre-linked to _drainCts at enqueue time,
            // so it fires on drain, explicit TryCancelRequest, or CancelAllRequests.
            // No need to create a second linked CTS for the no-timeout path.
            CancellationToken dispatchToken = item.RequestCancellationToken;
            TimeSpan? effectiveTimeout = item.Context.Timeout ?? (_options.DispatchTimeoutMs > 0 ? TimeSpan.FromMilliseconds(_options.DispatchTimeoutMs) : null);

            if (effectiveTimeout is { } timeout)
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(item.RequestCancellationToken);
                timeoutCts.CancelAfter(timeout);
                dispatchToken = timeoutCts.Token;
            }

            try
            {
                var scheduler = _cachedSchedulers?[(int)item.Context.Priority];

                if (scheduler is null || scheduler == TaskScheduler.Default)
                    return await DispatchCoreAsync(item.Context, dispatchToken).ConfigureAwait(false);

                // Run the async dispatch on the caller-supplied scheduler so that the
                // synchronous entry point (and any CPU-bound work before the first await)
                // executes on the desired thread pool.
                return await Task.Factory.StartNew(
                    static state =>
                    {
                        var (service, ctx, tok) = ((RequestPoolService, RequestContext, CancellationToken))state!;
                        return service.DispatchCoreAsync(ctx, tok).AsTask();
                    },
                    (this, item.Context, dispatchToken),
                    dispatchToken,
                    TaskCreationOptions.DenyChildAttach,
                    scheduler).Unwrap().ConfigureAwait(false);
            }
            finally
            {
                timeoutCts?.Dispose();
            }
        }
        finally
        {
            if (gateAcquired)
                gate?.Release();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RequestResult> DispatchCoreAsync(RequestContext context, CancellationToken dispatchToken)
    {
        if (_resiliencePipeline is not null)
        {
            return await _resiliencePipeline.ExecuteAsync(
                static async (state, ct) => await state.Dispatcher.DispatchAsync(state.Context, ct).ConfigureAwait(false),
                (Dispatcher: _dispatcher, Context: context),
                dispatchToken).ConfigureAwait(false);
        }

        return await _dispatcher.DispatchAsync(context, dispatchToken).ConfigureAwait(false);
    }

    private async Task InvokeCallbackAsync(WorkItem item, RequestResult result)
    {
        try
        {
            await item.Callback.InvokeAsync(result);
        }
        catch (Exception ex)
        {
            // Swallow so a bad callback never kills a worker.
            LogCallbackThrewForRequest(item.Context.RequestId, ex);
        }
    }

    private static void TryReturnRequestCancellationTokenSource(WorkItem item)
    {
        if (Interlocked.Exchange(ref item.AgingState.CancellationTokenSourceReturned, 1) == 0)
            // Linked CTS cannot be safely pooled (TryReset succeeds but the instance stays linked
            // to _drainCts, leaving stale cancellation registrations). Dispose directly instead.
            item.RequestCancellationTokenSource.Dispose();
    }

    private void OnChannelItemDropped(WorkItem item)
    {
        // Decrement the partition item counter unconditionally (stale copies still occupied a slot).
        if (_options.PartitionFairnessEnabled)
            Interlocked.Decrement(ref _partitionedItemCount);

        // For first-attempt items, the AgingState.PriorityOrClaimed reference is shared with
        // any aging-promoted copy AND with the worker's CAS-claim sentinel. If PriorityOrClaimed
        // no longer matches this item's QueuedPriority, this is a stale copy — either:
        //   (a) aging promoted the item to a higher priority and the promoted WorkItem will fire
        //       the callback (when processed, dropped, or cancelled), or
        //   (b) a worker already CAS-claimed and is running it (claim sentinel value).
        // In both cases, firing the callback here would double-invoke the user's TCS / handler.
        var isStaleAgedOrClaimedCopy = item.Attempt == 1 && Volatile.Read(ref item.AgingState.PriorityOrClaimed) != (int)item.QueuedPriority;

        if (item.Attempt == 1 && !isStaleAgedOrClaimedCopy)
        {
            if (_pending.TryRemove(item.Context.RequestId, out var entry))
                TryReturnRequestCancellationTokenSource(entry.Item);
            else
                TryReturnRequestCancellationTokenSource(item);
        }
        else if (item.Attempt != 1)
        {
            TryReturnRequestCancellationTokenSource(item);
        }

        if (isStaleAgedOrClaimedCopy)
            return;

        _metrics.Dropped.Add(1, new KeyValuePair<string, object?>("priority", item.QueuedPriority.ToString()));

        try
        {
            _options.OnItemDropped?.Invoke(item.Context);
        }
        catch (Exception ex)
        {
            LogItemDroppedCallbackThrew(ex, item.Context.RequestId);
        }

        // Fire the user completion callback so awaiters (e.g. EnqueueAsync's Task<RequestResult>
        // overload) do not hang. The channel drop callback is synchronous, so dispatch async
        // invocation to a worker thread to avoid blocking the writer that triggered the drop.
        // Track in-flight tasks so Dispose() waits for them before releasing _metrics/_drainCts.
        var droppedResult = new RequestResult(
            item.Context.RequestId,
            Success: false,
            Output: null,
            Error: new ChannelClosedException("Request was dropped from the queue (channel full)."));

        Interlocked.Increment(ref _inFlightDropCallbacks);

        _ = Task.Run(async () =>
        {
            try
            {
                await InvokeCallbackAsync(item, droppedResult).ConfigureAwait(false);
            }
            catch
            {
                // InvokeCallbackAsync already swallows callback exceptions; this is a safety net.
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightDropCallbacks);
            }
        });
    }

    private void StartPriorityAgingTimer()
    {
        if (_options.PriorityAgingThreshold is null)
            return;

        _priorityAgingTimer = _timeProvider.CreateTimer(
            static state => ((RequestPoolService)state!).QueuePriorityAgingScan(),
            this,
            _options.PriorityAgingScanInterval,
            _options.PriorityAgingScanInterval);
    }

    private void QueuePriorityAgingScan()
    {
        // Dispose may have already started: if the timer callback was queued to the thread
        // pool before _priorityAgingTimer.Dispose() ran but executes after Dispose has passed
        // the spin-wait on _priorityAgingScanRunning, we must not enter the scan — it would
        // observe a disposed _drainCts / _metrics.
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.CompareExchange(ref _priorityAgingScanRunning, 1, 0) != 0)
            return;

        // Re-check after winning the CAS to close the window where Dispose set _disposed
        // between the first read and the CAS.
        if (Volatile.Read(ref _disposed) != 0)
        {
            Volatile.Write(ref _priorityAgingScanRunning, 0);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ScanPriorityAgingAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _priorityAgingScanRunning, 0);
            }
        });
    }

    private async Task ScanPriorityAgingAsync()
    {
        if (_options.PriorityAgingThreshold is not { } threshold)
            return;

        var drainToken = _drainCts.Token;
        if (drainToken.IsCancellationRequested)
            return;

        var now = _timeProvider.GetTimestamp();
        foreach (var (requestId, entry) in _pending)
        {
            if (drainToken.IsCancellationRequested)
                return;

            var fromPriority = entry.Priority;
            if (fromPriority == RequestPriority.High)
                continue;

            if (_timeProvider.GetElapsedTime(entry.EnqueuedTimestampTicks, now) < threshold)
                continue;

            var toPriority = (RequestPriority)((int)fromPriority + 1);
            if (Interlocked.CompareExchange(ref entry.Item.AgingState.PriorityOrClaimed, (int)toPriority, (int)fromPriority) != (int)fromPriority)
                continue;

            var promotedContext = entry.Item.Context with { Priority = toPriority };
            var promotedItem = entry.Item with
            {
                Context = promotedContext,
                QueuedPriority = toPriority
            };
            var promotedEntry = entry with { Priority = toPriority, Item = promotedItem };
            _pending.TryUpdate(requestId, promotedEntry, entry);

            try
            {
                await WriteWorkItemAsync(promotedItem, drainToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (drainToken.IsCancellationRequested)
            {
                // Shutdown in progress; drop the promotion silently — the original
                // entry is already removed from _pending by the worker draining the queue,
                // or will be cancelled by the per-request CTS.
                return;
            }
            catch (ChannelClosedException)
            {
                // Channel closed mid-promotion (race with StopAsync). Treat as shutdown drop.
                return;
            }
            catch (Exception ex)
            {
                if (_pending.TryRemove(requestId, out var removed))
                    TryReturnRequestCancellationTokenSource(removed.Item);
                else
                    TryReturnRequestCancellationTokenSource(promotedItem);

                // Count this terminal failure so the metrics conservation equation holds:
                // TotalEnqueued = Completed + Failed + Cancelled + Dropped + DeadLetter + InFlight + InQueue.
                Interlocked.Increment(ref _totalFailed);
                _metrics.Completed.Add(1, tag: new("outcome", "failure"));

                await InvokeCallbackAsync(promotedItem, new RequestResult(
                    requestId,
                    Success: false,
                    Output: null,
                    Error: ex)).ConfigureAwait(false);
                continue;
            }

            // Metrics recording happens outside the try so that a failure here cannot
            // re-fire the user callback for a request whose promoted copy is already in
            // the channel and will be processed (or correctly stale-detected) by a worker.
            _metrics.PriorityPromoted.Add(
                1,
                new KeyValuePair<string, object?>("from_priority", fromPriority.ToString()),
                new KeyValuePair<string, object?>("to_priority", toPriority.ToString()));
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    [LoggerMessage(1, LogLevel.Debug, "Enqueuing {Priority} request {RequestId}")]
    private partial void LogEnqueuing(RequestPriority priority, string requestId);

    [LoggerMessage(2, LogLevel.Information, "Request pool starting with {Concurrency} workers (per-priority capacity {Capacity})")]
    private partial void LogPoolStarting(int concurrency, int capacity);

    [LoggerMessage(3, LogLevel.Information, "Request pool stopped")]
    private partial void LogPoolStopped();

    [LoggerMessage(4, LogLevel.Debug, "Worker {WorkerId} started")]
    private partial void LogWorkerStarted(int workerId);

    [LoggerMessage(5, LogLevel.Debug, "Worker {WorkerId} finished")]
    private partial void LogWorkerFinished(int workerId);

    [LoggerMessage(6, LogLevel.Debug, "Request {RequestId} was cancelled before dispatch")]
    private partial void LogRequestCancelledBeforeDispatch(string requestId);

    [LoggerMessage(7, LogLevel.Debug, "Worker {WorkerId} processing [{Priority}] {RequestId}")]
    private partial void LogWorkerProcessing(int workerId, RequestPriority priority, string requestId);

    [LoggerMessage(8, LogLevel.Warning, "Request {RequestId} cancelled during shutdown")]
    private partial void LogRequestCancelledDuringShutdown(string requestId);

    [LoggerMessage(9, LogLevel.Error, "Dispatcher threw for request {RequestId}")]
    private partial void LogDispatcherThrewForRequest(string requestId, Exception ex);

    [LoggerMessage(10, LogLevel.Error, "Completion callback threw for request {RequestId}")]
    private partial void LogCallbackThrewForRequest(string requestId, Exception ex);

    [LoggerMessage(11, LogLevel.Error, "Item dropped callback threw for request {RequestId}")]
    private partial void LogItemDroppedCallbackThrew(Exception exception, string requestId);

    [LoggerMessage(12, LogLevel.Debug, "Retrying request {RequestId} as attempt {Attempt} of {MaxAttempts}")]
    private partial void LogRetryingRequest(string requestId, int attempt, int maxAttempts);

    [LoggerMessage(13, LogLevel.Error, "Retry predicate threw for request {RequestId}")]
    private partial void LogRetryPredicateThrew(Exception exception, string requestId);

    [LoggerMessage(14, LogLevel.Error, "Dead-letter callback threw for request {RequestId}")]
    private partial void LogDeadLetterCallbackThrew(Exception exception, string requestId);
}
