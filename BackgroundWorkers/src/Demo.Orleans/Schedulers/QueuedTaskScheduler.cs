//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE.md file in the project root for full license information.
//

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace System.Threading.Tasks.Schedulers
{
    /// <summary>
    /// Provides a TaskScheduler that provides control over priorities, fairness, and the underlying threads utilized.
    /// </summary>
    [DebuggerTypeProxy(typeof(QueuedTaskSchedulerDebugView))]
    [DebuggerDisplay("Id = {Id}, Queues = {DebugQueueCount}, ScheduledTasks = {DebugTaskCount}")]
    public sealed class QueuedTaskScheduler : TaskScheduler, IDisposable
    {
        // Lock ordering invariant: always acquire the _queueGroups lock before group.SyncRoot.
        /// <summary>Debug view for the QueuedTaskScheduler.</summary>
        private class QueuedTaskSchedulerDebugView
        {
            /// <summary>The scheduler.</summary>
            private readonly QueuedTaskScheduler _scheduler;

            /// <summary>Initializes the debug view.</summary>
            /// <param name="scheduler">The scheduler.</param>
            public QueuedTaskSchedulerDebugView(QueuedTaskScheduler scheduler) =>
                _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

            /// <summary>Gets all of the Tasks queued to the scheduler directly.</summary>
            public IEnumerable<Task> ScheduledTasks
            {
                get
                {
                    IEnumerable<Task?> tasks = (_scheduler._targetScheduler != null)
                        ? (IEnumerable<Task?>)_scheduler._threadsafeTaskQueue!
                        : (IEnumerable<Task?>)_scheduler._blockingTaskQueue!;
                    return tasks.OfType<Task>();
                }
            }

            /// <summary>Gets the prioritized and fair queues.</summary>
            public IEnumerable<TaskScheduler> Queues
            {
                get
                {
                    List<TaskScheduler> queues = new List<TaskScheduler>();
                    var snapshot = _scheduler.RentQueueGroupSnapshot(out int snapshotCount);
                    try
                    {
                        for (int i = 0; i < snapshotCount; i++)
                        {
                            var group = snapshot[i];
                            lock (group.SyncRoot)
                            {
                                if (!group.Removed)
                                    queues.AddRange(group.Cast<TaskScheduler>());
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<QueueGroup>.Shared.Return(snapshot, clearArray: true);
                    }
                    return queues;
                }
            }
        }

        /// <summary>
        /// A sorted list of round-robin queue lists.  Tasks with the smallest priority value
        /// are preferred.  Priority groups are round-robin'd through in order of priority.
        /// </summary>
        private readonly SortedList<int, QueueGroup> _queueGroups = [];
        /// <summary>Frozen snapshot of queue groups captured by <see cref="Freeze"/>. Null until frozen.</summary>
        private volatile QueueGroup[]? _frozenGroups;
        /// <summary>Metric instruments emitted by this scheduler. Null when metrics are disabled.</summary>
        private readonly QueuedTaskSchedulerMetrics? _metrics;
        /// <summary>Cancellation token used for disposal.</summary>
        private readonly CancellationTokenSource _disposeCancellation = new();
        /// <summary>
        /// The maximum allowed concurrency level of this scheduler.  If custom threads are
        /// used, this represents the number of created threads.
        /// </summary>
        private readonly int _concurrencyLevel;
        /// <summary>Whether we're processing tasks on the current thread.</summary>
        private static readonly ThreadLocal<bool> s_taskProcessingThread = new();

        // ***
        // *** For when using a target scheduler
        // ***

        /// <summary>The scheduler onto which actual work is scheduled. Null when using dedicated threads.</summary>
        private readonly TaskScheduler? _targetScheduler;
        /// <summary>The queue of tasks to process when using an underlying target scheduler. Null when using dedicated threads.</summary>
        private readonly ConcurrentQueue<Task?>? _threadsafeTaskQueue;
        /// <summary>The number of Tasks that have been queued or that are running while using an underlying scheduler.</summary>
        private int _delegatesQueuedOrRunning = 0;

        // ***
        // *** For when using our own threads
        // ***

        /// <summary>The threads used by the scheduler to process work.</summary>
        private readonly Thread[]? _threads;
        /// <summary>The collection of tasks to be executed on our custom threads. Null when using a target scheduler.</summary>
        private readonly BlockingCollection<Task?>? _blockingTaskQueue;

        // ***

        /// <summary>Initializes the scheduler.</summary>
        public QueuedTaskScheduler() : this(Default, 0) { }

        /// <summary>Initializes the scheduler.</summary>
        /// <param name="meterFactory">The meter factory used to create scheduler metrics.</param>
        public QueuedTaskScheduler(IMeterFactory? meterFactory) : this(Default, 0, meterFactory) { }

        /// <summary>Initializes the scheduler.</summary>
        /// <param name="targetScheduler">The target underlying scheduler onto which this sceduler's work is queued.</param>
        public QueuedTaskScheduler(TaskScheduler targetScheduler) : this(targetScheduler, 0) { }

        /// <summary>Initializes the scheduler.</summary>
        /// <param name="targetScheduler">The target underlying scheduler onto which this sceduler's work is queued.</param>
        /// <param name="maxConcurrencyLevel">The maximum degree of concurrency allowed for this scheduler's work.</param>
        public QueuedTaskScheduler(
            TaskScheduler targetScheduler,
            int maxConcurrencyLevel)
            : this(targetScheduler, maxConcurrencyLevel, null)
        {
        }

        /// <summary>Initializes the scheduler.</summary>
        /// <param name="targetScheduler">The target underlying scheduler onto which this sceduler's work is queued.</param>
        /// <param name="maxConcurrencyLevel">The maximum degree of concurrency allowed for this scheduler's work.</param>
        /// <param name="meterFactory">The meter factory used to create scheduler metrics.</param>
        public QueuedTaskScheduler(
            TaskScheduler targetScheduler,
            int maxConcurrencyLevel,
            IMeterFactory? meterFactory = null)
        {
            if (maxConcurrencyLevel < 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrencyLevel));

            // Initialize only those fields relevant to use an underlying scheduler.  We don't
            // initialize the fields relevant to using our own custom threads.
            _targetScheduler = targetScheduler ?? throw new ArgumentNullException("underlyingScheduler");
            _threadsafeTaskQueue = new ConcurrentQueue<Task?>();
            _metrics = meterFactory is not null
                ? new QueuedTaskSchedulerMetrics(meterFactory, ObserveQueueDepth, GetActiveGroupCount)
                : null;

            // If 0, use the number of logical processors.  But make sure whatever value we pick
            // is not greater than the degree of parallelism allowed by the underlying scheduler.
            _concurrencyLevel = maxConcurrencyLevel != 0 ? maxConcurrencyLevel : Environment.ProcessorCount;
            if (targetScheduler.MaximumConcurrencyLevel > 0 &&
                targetScheduler.MaximumConcurrencyLevel < _concurrencyLevel)
            {
                _concurrencyLevel = targetScheduler.MaximumConcurrencyLevel;
            }
        }

        /// <summary>Initializes the scheduler.</summary>
        /// <param name="threadCount">The number of threads to create and use for processing work items.</param>
        public QueuedTaskScheduler(int threadCount) : this(threadCount, string.Empty, false, ThreadPriority.Normal, ApartmentState.MTA, 0, null, null) { }

        /// <summary>Initializes the scheduler.</summary>
        /// <param name="threadCount">The number of threads to create and use for processing work items.</param>
        /// <param name="meterFactory">The meter factory used to create scheduler metrics.</param>
        public QueuedTaskScheduler(int threadCount, IMeterFactory? meterFactory) : this(threadCount, string.Empty, false, ThreadPriority.Normal, ApartmentState.MTA, 0, null, null, meterFactory) { }

        /// <summary>Initializes the scheduler.</summary>
        /// <param name="threadCount">The number of threads to create and use for processing work items.</param>
        /// <param name="threadName">The name to use for each of the created threads.</param>
        /// <param name="useForegroundThreads">A Boolean value that indicates whether to use foreground threads instead of background.</param>
        /// <param name="threadPriority">The priority to assign to each thread.</param>
        /// <param name="threadApartmentState">The apartment state to use for each thread.</param>
        /// <param name="threadMaxStackSize">The stack size to use for each thread.</param>
        /// <param name="threadInit">An initialization routine to run on each thread.</param>
        /// <param name="threadFinally">A finalization routine to run on each thread.</param>
        public QueuedTaskScheduler(
            int threadCount,
            string threadName = "",
            bool useForegroundThreads = false,
            ThreadPriority threadPriority = ThreadPriority.Normal,
            ApartmentState threadApartmentState = ApartmentState.MTA,
            int threadMaxStackSize = 0,
            Action? threadInit = null,
            Action? threadFinally = null)
            : this(threadCount, threadName, useForegroundThreads, threadPriority, threadApartmentState, threadMaxStackSize, threadInit, threadFinally, null)
        {
        }

        /// <summary>Initializes the scheduler.</summary>
        /// <param name="threadCount">The number of threads to create and use for processing work items.</param>
        /// <param name="threadName">The name to use for each of the created threads.</param>
        /// <param name="useForegroundThreads">A Boolean value that indicates whether to use foreground threads instead of background.</param>
        /// <param name="threadPriority">The priority to assign to each thread.</param>
        /// <param name="threadApartmentState">The apartment state to use for each thread.</param>
        /// <param name="threadMaxStackSize">The stack size to use for each thread.</param>
        /// <param name="threadInit">An initialization routine to run on each thread.</param>
        /// <param name="threadFinally">A finalization routine to run on each thread.</param>
        /// <param name="meterFactory">The meter factory used to create scheduler metrics.</param>
        public QueuedTaskScheduler(
            int threadCount,
            string threadName = "",
            bool useForegroundThreads = false,
            ThreadPriority threadPriority = ThreadPriority.Normal,
            ApartmentState threadApartmentState = ApartmentState.MTA,
            int threadMaxStackSize = 0,
            Action? threadInit = null,
            Action? threadFinally = null,
            IMeterFactory? meterFactory = null)
        {
            // Validates arguments (some validation is left up to the Thread type itself).
            // If the thread count is 0, default to the number of logical processors.
            if (threadCount < 0) throw new ArgumentOutOfRangeException(nameof(threadCount));
            else if (threadCount == 0) _concurrencyLevel = Environment.ProcessorCount;
            else _concurrencyLevel = threadCount;

            // Initialize the queue used for storing tasks
            _blockingTaskQueue = new BlockingCollection<Task?>();
            _metrics = meterFactory is not null
                ? new QueuedTaskSchedulerMetrics(meterFactory, ObserveQueueDepth, GetActiveGroupCount)
                : null;

            // Create all of the threads
            _threads = new Thread[_concurrencyLevel];
            for (int i = 0; i < _concurrencyLevel; i++)
            {
                _threads[i] = new Thread(() => ThreadBasedDispatchLoop(threadInit, threadFinally), threadMaxStackSize)
                {
                    Priority = threadPriority,
                    IsBackground = !useForegroundThreads,
                };
                if (threadName != null) _threads[i].Name = threadName + " (" + i + ")";
                if (OperatingSystem.IsWindows()) _threads[i].SetApartmentState(threadApartmentState);
            }

            // Start all of the threads
            foreach (var thread in _threads) thread.Start();
        }

        /// <summary>The dispatch loop run by all threads in this scheduler.</summary>
        /// <param name="threadInit">An initialization routine to run when the thread begins.</param>
        /// <param name="threadFinally">A finalization routine to run before the thread ends.</param>
        private void ThreadBasedDispatchLoop(Action? threadInit, Action? threadFinally)
        {
            s_taskProcessingThread.Value = true;
            threadInit?.Invoke();
            try
            {
                // If the scheduler is disposed, the cancellation token will be set and
                // we'll receive an OperationCanceledException.  That OCE should not crash the process.
                try
                {
                    // For each task queued to the scheduler, try to execute it.
                    foreach (var task in _blockingTaskQueue!.GetConsumingEnumerable(_disposeCancellation.Token))
                    {
                        // If the task is not null, that means it was queued to this scheduler directly.
                        // Run it.
                        if (task != null)
                        {
                            RecordDirectTaskDispatched();
                            TryExecuteTask(task);
                        }
                        // If the task is null, that means it's just a placeholder for a task
                        // queued to one of the subschedulers.  Find the next task based on
                        // priority and fairness and run it.
                        else
                        {
                            // Find the next task based on our ordering rules...
                            Task? targetTask;
                            QueuedTaskSchedulerQueue? queueForTargetTask;
                            FindNextTask(out targetTask, out queueForTargetTask);

                            // ... and if we found one, run it
                            if (targetTask != null) queueForTargetTask!.ExecuteTask(targetTask);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }
            finally
            {
                // Run a cleanup routine if there was one
                threadFinally?.Invoke();
                s_taskProcessingThread.Value = false;
            }
        }

        /// <summary>Gets the number of queues currently activated.</summary>
        private int DebugQueueCount
        {
            get
            {
                int count = 0;
                var snapshot = RentQueueGroupSnapshot(out int snapshotCount);
                try
                {
                    for (int i = 0; i < snapshotCount; i++)
                    {
                        var group = snapshot[i];
                        lock (group.SyncRoot)
                        {
                            if (!group.Removed)
                                count += group.Count;
                        }
                    }
                }
                finally
                {
                    ArrayPool<QueueGroup>.Shared.Return(snapshot, clearArray: true);
                }
                return count;
            }
        }

        /// <summary>Gets the number of tasks currently scheduled.</summary>
        private int DebugTaskCount => (_targetScheduler != null
                    ? (IEnumerable<Task?>)_threadsafeTaskQueue! : (IEnumerable<Task?>)_blockingTaskQueue!)
                    .OfType<Task>().Count();

        private IEnumerable<Measurement<int>> ObserveQueueDepth()
        {
            var measurements = new List<Measurement<int>>();
            var snapshot = RentQueueGroupSnapshot(out int snapshotCount);
            try
            {
                for (int i = 0; i < snapshotCount; i++)
                {
                    var group = snapshot[i];
                    int count = 0;
                    int priority = 0;
                    bool removed;
                    lock (group.SyncRoot)
                    {
                        removed = group.Removed;
                        if (!removed)
                        {
                            priority = group.Priority;
                            foreach (var queue in group)
                                count += queue.WaitingTasks;
                        }
                    }

                    if (!removed)
                        measurements.Add(new Measurement<int>(count, new KeyValuePair<string, object?>("priority", priority)));
                }
            }
            finally
            {
                ArrayPool<QueueGroup>.Shared.Return(snapshot, clearArray: true);
            }

            return measurements;
        }

        private int GetActiveGroupCount()
        {
            int count = 0;
            var snapshot = RentQueueGroupSnapshot(out int snapshotCount);
            try
            {
                for (int i = 0; i < snapshotCount; i++)
                {
                    var group = snapshot[i];
                    lock (group.SyncRoot)
                    {
                        if (group.Removed)
                            continue;

                        foreach (var queue in group)
                        {
                            if (queue.WaitingTasks > 0)
                            {
                                count++;
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<QueueGroup>.Shared.Return(snapshot, clearArray: true);
            }
            return count;
        }

        private void RecordTaskQueued(int priority) =>
            _metrics?.TasksQueued.Add(1, new KeyValuePair<string, object?>("priority", priority));

        private void RecordTaskDispatched(int priority) =>
            _metrics?.TasksDispatched.Add(1, new KeyValuePair<string, object?>("priority", priority));

        private void RecordDirectTaskQueued() =>
            _metrics?.TasksQueued.Add(1, new KeyValuePair<string, object?>("priority", "direct"));

        private void RecordDirectTaskDispatched() =>
            _metrics?.TasksDispatched.Add(1, new KeyValuePair<string, object?>("priority", "direct"));

        /// <summary>Find the next task that should be executed, based on priorities and fairness and the like.</summary>
        /// <param name="targetTask">The found task, or null if none was found.</param>
        /// <param name="queueForTargetTask">
        /// The scheduler associated with the found task.  Due to security checks inside of TPL,  
        /// this scheduler needs to be used to execute that task.
        /// </param>
        private void FindNextTask(out Task? targetTask, out QueuedTaskSchedulerQueue? queueForTargetTask)
        {
            // Fast path: if frozen, use the lock-free frozen snapshot
            var frozen = _frozenGroups;
            if (frozen is not null)
            {
                FindNextTaskFrozen(frozen, out targetTask, out queueForTargetTask);
                return;
            }

            // Slow path: dynamic group membership, needs _queueGroups lock
            targetTask = null;
            queueForTargetTask = null;

            var snapshot = RentQueueGroupSnapshot(out int snapshotCount);
            try
            {
                // First pass: respect per-group starvation quotas.
                // Groups whose ConsecutiveServed has reached their Quantum are skipped once,
                // allowing lower-priority groups to run.  This prevents permanent starvation
                // of lower-priority queues under sustained high-priority load.
                for (int groupIndex = 0; groupIndex < snapshotCount; groupIndex++)
                {
                    var queues = snapshot[groupIndex];
                    QueueGroupRemoval removal = default;

                    lock (queues.SyncRoot)
                    {
                        if (queues.Removed || queues.Count == 0)
                            continue;

                        if (queues.ConsecutiveServed >= queues.Quantum)
                        {
                            // Budget exhausted: reset and yield to lower-priority groups this round.
                            queues.ConsecutiveServed = 0;
                            continue;
                        }

                        if (TryDequeueFromGroup(queues, incrementConsecutiveServed: true, out targetTask, out queueForTargetTask, out removal))
                        {
                            RecordTaskDispatched(queueForTargetTask!._priority);
                        }
                        else
                        {
                            // No items found in this group; reset its counter so it starts fresh
                            // next time it has work (no penalty for being empty).
                            queues.ConsecutiveServed = 0;
                        }
                    }

                    RemoveGroupIfNeeded(removal);
                    if (targetTask != null)
                        return;
                }

                // Second pass (strict-priority fallback): reached when all groups with items
                // simultaneously hit their quota.  Ensures we always make progress.
                // We deliberately do NOT increment ConsecutiveServed here: the second pass is
                // a one-time escape hatch that must not charge against any group's quota budget.
                // If we incremented, the group that was "rescued" here would start the next
                // round already partway through its quantum, defeating the fairness guarantee.
                for (int groupIndex = 0; groupIndex < snapshotCount; groupIndex++)
                {
                    var queues = snapshot[groupIndex];
                    QueueGroupRemoval removal = default;

                    lock (queues.SyncRoot)
                    {
                        if (queues.Removed || queues.Count == 0)
                            continue;

                        if (TryDequeueFromGroup(queues, incrementConsecutiveServed: false, out targetTask, out queueForTargetTask, out removal))
                        {
                            RecordTaskDispatched(queueForTargetTask!._priority);
                        }
                    }

                    RemoveGroupIfNeeded(removal);
                    if (targetTask != null)
                        return;
                }
            }
            finally
            {
                ArrayPool<QueueGroup>.Shared.Return(snapshot, clearArray: true);
            }
        }

        /// <summary>
        /// Lock-free variant of <see cref="FindNextTask"/> for use after <see cref="Freeze"/>.
        /// Iterates the frozen group snapshot directly — no outer lock, no ArrayPool allocation.
        /// Per-group <c>group.SyncRoot</c> locks are still acquired for dequeue safety.
        /// </summary>
        private void FindNextTaskFrozen(QueueGroup[] frozen, out Task? targetTask, out QueuedTaskSchedulerQueue? queueForTargetTask)
        {
            targetTask = null;
            queueForTargetTask = null;

            // First pass: respect per-group quantum (starvation prevention)
            for (int groupIndex = 0; groupIndex < frozen.Length; groupIndex++)
            {
                var queues = frozen[groupIndex];
                QueueGroupRemoval removal = default;

                lock (queues.SyncRoot)
                {
                    if (queues.Removed || queues.Count == 0)
                        continue;

                    if (queues.ConsecutiveServed >= queues.Quantum)
                    {
                        queues.ConsecutiveServed = 0;
                        continue;
                    }

                    if (TryDequeueFromGroup(queues, incrementConsecutiveServed: true, out targetTask, out queueForTargetTask, out removal))
                    {
                        RecordTaskDispatched(queueForTargetTask!._priority);
                    }
                    else
                    {
                        queues.ConsecutiveServed = 0;
                    }
                }

                RemoveGroupIfNeeded(removal);
                if (targetTask != null)
                    return;
            }

            // Second pass: strict-priority fallback (does not charge ConsecutiveServed)
            for (int groupIndex = 0; groupIndex < frozen.Length; groupIndex++)
            {
                var queues = frozen[groupIndex];
                QueueGroupRemoval removal = default;

                lock (queues.SyncRoot)
                {
                    if (queues.Removed || queues.Count == 0)
                        continue;

                    if (TryDequeueFromGroup(queues, incrementConsecutiveServed: false, out targetTask, out queueForTargetTask, out removal))
                    {
                        RecordTaskDispatched(queueForTargetTask!._priority);
                    }
                }

                RemoveGroupIfNeeded(removal);
                if (targetTask != null)
                    return;
            }
        }

        private QueueGroup[] RentQueueGroupSnapshot(out int count)
        {
            if (_metrics is { } metrics)
            {
                var start = Stopwatch.GetTimestamp();
                lock (_queueGroups)
                {
                    metrics.RecordLockWait(start);
                    return CopyQueueGroupSnapshot(out count);
                }
            }

            lock (_queueGroups)
            {
                return CopyQueueGroupSnapshot(out count);
            }
        }

        private QueueGroup[] CopyQueueGroupSnapshot(out int count)
        {
            count = _queueGroups.Count;
            var snapshot = ArrayPool<QueueGroup>.Shared.Rent(Math.Max(count, 1));
            for (int i = 0; i < count; i++)
                snapshot[i] = _queueGroups.Values[i];
            return snapshot;
        }

        private readonly record struct QueueGroupRemoval(int Priority, QueueGroup? Group);

        private bool TryDequeueFromGroup(
            QueueGroup queues,
            bool incrementConsecutiveServed,
            out Task? targetTask,
            out QueuedTaskSchedulerQueue? queueForTargetTask,
            out QueueGroupRemoval removal)
        {
            targetTask = null;
            queueForTargetTask = null;
            removal = default;

            foreach (int i in queues.CreateSearchOrder())
            {
                queueForTargetTask = queues[i];
                var items = queueForTargetTask._workItems;
                if (!items.IsEmpty && items.TryDequeue(out targetTask) && targetTask != null)
                {
                    // Advance the round-robin index BEFORE potentially removing the queue.
                    // RemoveQueueFromGroup_NeedsLock decrements NextQueueIndex when the removed
                    // slot is at or before it, so performing the mod on the pre-removal Count is safe.
                    queues.NextQueueIndex = (queues.NextQueueIndex + 1) % queues.Count;
                    if (queueForTargetTask._disposed && items.IsEmpty)
                    {
                        removal = RemoveQueueFromGroup_NeedsLock(queues, queueForTargetTask);
                    }
                    if (incrementConsecutiveServed)
                        queues.ConsecutiveServed++;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Queues a task to the scheduler.</summary>
        /// <param name="task">The task to be queued.</param>
        protected override void QueueTask(Task task)
        {
            // If we've been disposed, no one should be queueing
            if (_disposeCancellation.IsCancellationRequested) throw new ObjectDisposedException(GetType().Name);

            // If the target scheduler is null (meaning we're using our own threads),
            // add the task to the blocking queue
            if (_targetScheduler == null)
            {
                _blockingTaskQueue!.Add(task);
                if (task != null) RecordDirectTaskQueued();
            }
            // Otherwise, add the task to the non-blocking queue,
            // and if there isn't already an executing processing task,
            // start one up
            else
            {
                _threadsafeTaskQueue!.Enqueue(task);
                if (task != null) RecordDirectTaskQueued();

                // If necessary, start processing asynchronously
                if (TryReserveProcessingSlot())
                {
                    Task.Factory.StartNew(ProcessPrioritizedAndBatchedTasks,
                        CancellationToken.None, TaskCreationOptions.None, _targetScheduler);
                }
            }
        }

        private bool TryReserveProcessingSlot()
        {
            while (true)
            {
                int current = Volatile.Read(ref _delegatesQueuedOrRunning);
                if (current >= _concurrencyLevel)
                    return false;

                if (Interlocked.CompareExchange(ref _delegatesQueuedOrRunning, current + 1, current) == current)
                    return true;
            }
        }

        /// <summary>
        /// Process tasks one at a time in the best order.  
        /// This should be run in a Task generated by QueueTask.
        /// It's been separated out into its own method to show up better in Parallel Tasks.
        /// </summary>
        private void ProcessPrioritizedAndBatchedTasks()
        {
            while (!_disposeCancellation.IsCancellationRequested)
            {
                try
                {
                    // Note that we're processing tasks on this thread
                    s_taskProcessingThread.Value = true;

                    // Until there are no more tasks to process
                    while (!_disposeCancellation.IsCancellationRequested && _threadsafeTaskQueue!.TryDequeue(out Task? targetTask))
                    {
                        // If the task is null, it's a placeholder for a task in the round-robin queues.
                        // Find the next one that should be processed.
                        QueuedTaskSchedulerQueue? queueForTargetTask = null;
                        if (targetTask == null)
                        {
                            FindNextTask(out targetTask, out queueForTargetTask);
                        }
                        else
                        {
                            RecordDirectTaskDispatched();
                        }

                        // Now if we finally have a task, run it.  If the task
                        // was associated with one of the round-robin schedulers, we need to use it
                        // as a thunk to execute its task.
                        if (targetTask != null)
                        {
                            if (queueForTargetTask != null) queueForTargetTask.ExecuteTask(targetTask);
                            else TryExecuteTask(targetTask);
                        }
                    }
                }
                finally
                {
                    s_taskProcessingThread.Value = false;
                    Interlocked.Decrement(ref _delegatesQueuedOrRunning);
                }

                if (_disposeCancellation.IsCancellationRequested || _threadsafeTaskQueue!.IsEmpty || !TryReserveProcessingSlot())
                    break;
            }
        }

        /// <summary>Notifies the pool that there's a new item to be executed in one of the round-robin queues.</summary>
        private void NotifyNewWorkItem() => QueueTask(null!);

        /// <summary>Tries to execute a task synchronously on the current thread.</summary>
        /// <param name="task">The task to execute.</param>
        /// <param name="taskWasPreviouslyQueued">Whether the task was previously queued.</param>
        /// <returns>true if the task was executed; otherwise, false.</returns>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
            // If we're already running tasks on this threads, enable inlining
            s_taskProcessingThread.Value && TryExecuteTask(task);

        /// <summary>Gets the tasks scheduled to this scheduler.</summary>
        /// <returns>An enumerable of all tasks queued to this scheduler.</returns>
        /// <remarks>This does not include the tasks on sub-schedulers.  Those will be retrieved by the debugger separately.</remarks>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            // If we're running on our own threads, get the tasks from the blocking queue...
            if (_targetScheduler == null)
            {
                // Get all of the tasks, filtering out nulls, which are just placeholders
                // for tasks in other sub-schedulers
                return _blockingTaskQueue!.OfType<Task>().ToList();
            }
            // otherwise get them from the non-blocking queue...
            else
            {
                return _threadsafeTaskQueue!.OfType<Task>().ToList();
            }
        }

        /// <summary>Gets the maximum concurrency level to use when processing tasks.</summary>
        public override int MaximumConcurrencyLevel => _concurrencyLevel;

        /// <summary>Initiates shutdown of the scheduler.</summary>
        public void Dispose()
        {
            _disposeCancellation.Cancel();

            // Join all worker threads so they exit cleanly before the process tears down.
            // A 5-second timeout per thread prevents an indefinite hang if a thread is stuck.
            if (_threads != null)
            {
                foreach (var thread in _threads)
                    thread.Join(TimeSpan.FromSeconds(5));
            }

            _disposeCancellation.Dispose();
            _blockingTaskQueue?.Dispose();
            _metrics?.Dispose();
        }

        /// <summary>Creates and activates a new scheduling queue for this scheduler.</summary>
        /// <returns>The newly created and activated queue at priority 0.</returns>
        public TaskScheduler ActivateNewQueue() => ActivateNewQueue(0);

        /// <summary>
        /// Captures an immutable snapshot of the current queue groups for lock-free dispatch.
        /// Call this once after all <see cref="ActivateNewQueue"/> calls are complete.
        /// After this call, <see cref="ActivateNewQueue"/> will throw <see cref="InvalidOperationException"/>.
        /// </summary>
        public void Freeze()
        {
            lock (_queueGroups)
            {
                if (_frozenGroups is not null)
                    return;

                var frozen = new QueueGroup[_queueGroups.Count];
                _queueGroups.Values.CopyTo(frozen, 0);
                _frozenGroups = frozen;
            }
        }

        /// <summary>Creates and activates a new scheduling queue for this scheduler.</summary>
        /// <param name="priority">The priority level for the new queue.</param>
        /// <param name="quantum">
        /// Maximum number of tasks to serve from this priority group consecutively before
        /// yielding to lower-priority groups.  Defaults to <see cref="int.MaxValue"/> (no cap).
        /// Only applied when creating the group; ignored if a group at this priority already exists.
        /// </param>
        /// <returns>The newly created and activated queue at the specified priority.</returns>
        public TaskScheduler ActivateNewQueue(int priority, int quantum = int.MaxValue)
        {
            if (_frozenGroups is not null)
                throw new InvalidOperationException("Cannot activate new queues after Freeze() has been called.");

            // Create the queue
            var createdQueue = new QueuedTaskSchedulerQueue(priority, this);

            AddQueueToGroup(priority, quantum, createdQueue);

            // Hand the new queue back
            return createdQueue;
        }

        private void AddQueueToGroup(int priority, int quantum, QueuedTaskSchedulerQueue queue)
        {
            while (true)
            {
                QueueGroup queueGroup;
                if (_metrics is { } metrics)
                {
                    var start = Stopwatch.GetTimestamp();
                    lock (_queueGroups)
                    {
                        metrics.RecordLockWait(start);
                        if (!_queueGroups.TryGetValue(priority, out queueGroup!))
                        {
                            queueGroup = new QueueGroup { Priority = priority, Quantum = quantum };
                            queueGroup.Add(queue);
                            _queueGroups.Add(priority, queueGroup);
                            return;
                        }
                    }
                }
                else
                {
                    lock (_queueGroups)
                    {
                        if (!_queueGroups.TryGetValue(priority, out queueGroup!))
                        {
                            queueGroup = new QueueGroup { Priority = priority, Quantum = quantum };
                            queueGroup.Add(queue);
                            _queueGroups.Add(priority, queueGroup);
                            return;
                        }
                    }
                }

                lock (queueGroup.SyncRoot)
                {
                    if (queueGroup.Removed)
                        continue;

                    queueGroup.Add(queue);
                    return;
                }
            }
        }

        private QueueGroupRemoval RemoveQueueFromGroup_NeedsLock(QueueGroup queueGroup, QueuedTaskSchedulerQueue queue)
        {
            // Guard against double-removal: Dispose() and FindNextTask can race to remove
            // a just-drained disposed queue. The first caller wins; the second call is a no-op.
            int index = queueGroup.IndexOf(queue);
            if (index < 0) return default;

            // We're about to remove the queue, so adjust the index of the next
            // round-robin starting location if it'll be affected by the removal.
            if (queueGroup.NextQueueIndex >= index) queueGroup.NextQueueIndex--;

            queueGroup.RemoveAt(index);

            if (queueGroup.Count != 0)
                return default;

            // Prevent ActivateNewQueue from adding to this group while its priority slot is
            // being removed from _queueGroups outside the per-group lock.
            queueGroup.Removed = true;
            return new QueueGroupRemoval(queue._priority, queueGroup);
        }

        private void RemoveQueue_NeedsLock(QueuedTaskSchedulerQueue queue) => RemoveQueue(queue);

        private void RemoveQueue(QueuedTaskSchedulerQueue queue)
        {
            QueueGroup? queueGroup;
            if (_metrics is { } metrics)
            {
                var start = Stopwatch.GetTimestamp();
                lock (_queueGroups)
                {
                    metrics.RecordLockWait(start);
                    if (!_queueGroups.TryGetValue(queue._priority, out queueGroup))
                        return;
                }
            }
            else
            {
                lock (_queueGroups)
                {
                    if (!_queueGroups.TryGetValue(queue._priority, out queueGroup))
                        return;
                }
            }

            QueueGroupRemoval removal;
            lock (queueGroup.SyncRoot)
            {
                if (queueGroup.Removed)
                    return;

                removal = RemoveQueueFromGroup_NeedsLock(queueGroup, queue);
            }

            RemoveGroupIfNeeded(removal);
        }

        private void RemoveGroupIfNeeded(QueueGroupRemoval removal)
        {
            // After Freeze(), _queueGroups is no longer the live lookup structure; skip removal.
            if (removal.Group is null || _frozenGroups is not null)
                return;

            if (_metrics is { } metrics)
            {
                var start = Stopwatch.GetTimestamp();
                lock (_queueGroups)
                {
                    metrics.RecordLockWait(start);
                    if (_queueGroups.TryGetValue(removal.Priority, out var current) && ReferenceEquals(current, removal.Group))
                        _queueGroups.Remove(removal.Priority);
                }
            }
            else
            {
                lock (_queueGroups)
                {
                    if (_queueGroups.TryGetValue(removal.Priority, out var current) && ReferenceEquals(current, removal.Group))
                        _queueGroups.Remove(removal.Priority);
                }
            }
        }

        /// <summary>A group of queues a the same priority level.</summary>
        private class QueueGroup : List<QueuedTaskSchedulerQueue>
        {
            public object SyncRoot { get; } = new();

            /// <summary>The priority shared by queues in this group.</summary>
            public int Priority;

            /// <summary>Whether this group has been removed from the scheduler.</summary>
            public bool Removed;

            /// <summary>The starting index for the next round-robin traversal.</summary>
            public int NextQueueIndex = 0;

            /// <summary>
            /// Maximum number of tasks to serve from this group consecutively before yielding to
            /// lower-priority groups.  <see cref="int.MaxValue"/> (the default) disables the cap.
            /// </summary>
            public int Quantum = int.MaxValue;

            /// <summary>
            /// How many tasks have been served from this group in the current epoch.
            /// Reset to 0 when the group is skipped due to quota or found to be empty.
            /// </summary>
            public int ConsecutiveServed = 0;

            /// <summary>Creates a search order through this group.</summary>
            /// <returns>A struct enumerator of indices for this group (zero-allocation foreach).</returns>
            public SearchOrderEnumerator CreateSearchOrder() => new(Count, NextQueueIndex);

            /// <summary>
            /// Allocation-free enumerator yielding indices starting at <paramref name="start"/>
            /// and wrapping around to cover all <paramref name="count"/> slots exactly once.
            /// Exposed so <c>foreach</c> binds to this struct via pattern-based dispatch
            /// instead of allocating an <see cref="IEnumerator{Int32}"/>.
            /// </summary>
            public struct SearchOrderEnumerator
            {
                private readonly int _count;
                private readonly int _start;
                private int _index; // -1 before MoveNext; otherwise count of items already yielded

                public SearchOrderEnumerator(int count, int start)
                {
                    _count = count;
                    _start = start;
                    _index = -1;
                    Current = 0;
                }

                public int Current { get; private set; }

                public bool MoveNext()
                {
                    _index++;
                    if (_index >= _count) return false;
                    int i = _start + _index;
                    if (i >= _count) i -= _count;
                    Current = i;
                    return true;
                }

                public SearchOrderEnumerator GetEnumerator() => this;
            }
        }

        /// <summary>Provides a scheduling queue associatd with a QueuedTaskScheduler.</summary>
        [DebuggerDisplay("QueuePriority = {_priority}, WaitingTasks = {WaitingTasks}")]
        [DebuggerTypeProxy(typeof(QueuedTaskSchedulerQueueDebugView))]
        private sealed class QueuedTaskSchedulerQueue : TaskScheduler, IDisposable
        {
            /// <summary>A debug view for the queue.</summary>
            private sealed class QueuedTaskSchedulerQueueDebugView
            {
                /// <summary>The queue.</summary>
                private readonly QueuedTaskSchedulerQueue _queue;

                /// <summary>Initializes the debug view.</summary>
                /// <param name="queue">The queue to be debugged.</param>
                public QueuedTaskSchedulerQueueDebugView(QueuedTaskSchedulerQueue queue) =>
                    _queue = queue ?? throw new ArgumentNullException(nameof(queue));

                /// <summary>Gets the priority of this queue in its associated scheduler.</summary>
                public int Priority => _queue._priority;
                /// <summary>Gets the ID of this scheduler.</summary>
                public int Id => _queue.Id;
                /// <summary>Gets all of the tasks scheduled to this queue.</summary>
                public IEnumerable<Task> ScheduledTasks => _queue.GetScheduledTasks();
                /// <summary>Gets the QueuedTaskScheduler with which this queue is associated.</summary>
                public QueuedTaskScheduler AssociatedScheduler => _queue._pool;
            }

            /// <summary>The scheduler with which this pool is associated.</summary>
            private readonly QueuedTaskScheduler _pool;
            /// <summary>The work items stored in this queue.</summary>
            internal readonly ConcurrentQueue<Task> _workItems;
            /// <summary>
            /// Whether this queue has been disposed.  Written under <see cref="_queueLock"/>;
            /// read either under <see cref="_queueLock"/> or by workers draining the queue.
            /// Must be volatile so worker threads observe writes made under the per-queue lock.
            /// </summary>
            internal volatile bool _disposed;
            /// <summary>Gets the priority for this queue.</summary>
            internal int _priority;
            /// <summary>
            /// Per-queue lock that guards <see cref="_disposed"/> and the enqueue path.
            /// Using a per-queue lock means concurrent enqueues to different priority queues
            /// do not contend with each other or with worker threads calling <see cref="FindNextTask"/>.
            /// </summary>
            private readonly object _queueLock = new();

            /// <summary>Initializes the queue.</summary>
            /// <param name="priority">The priority associated with this queue.</param>
            /// <param name="pool">The scheduler with which this queue is associated.</param>
            internal QueuedTaskSchedulerQueue(int priority, QueuedTaskScheduler pool)
            {
                _priority = priority;
                _pool = pool;
                _workItems = new ConcurrentQueue<Task>();
            }

            /// <summary>Gets the number of tasks waiting in this scheduler.</summary>
            internal int WaitingTasks => _workItems.Count;

            /// <summary>Gets the tasks scheduled to this scheduler.</summary>
            /// <returns>An enumerable of all tasks queued to this scheduler.</returns>
            protected override IEnumerable<Task> GetScheduledTasks() => _workItems.ToArray();

            /// <summary>Queues a task to the scheduler.</summary>
            /// <param name="task">The task to be queued.</param>
            protected override void QueueTask(Task task)
            {
                // Use the per-queue lock (not the global _queueGroups lock) so that
                // concurrent enqueues to different priority queues do not contend with
                // each other or with worker threads running FindNextTask.
                lock (_queueLock)
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    _workItems.Enqueue(task);
                    _pool.RecordTaskQueued(_priority);
                }

                // NotifyNewWorkItem stays outside the lock – it only posts a sentinel and
                // does not touch _queueGroups.
                _pool.NotifyNewWorkItem();
            }

            /// <summary>Tries to execute a task synchronously on the current thread.</summary>
            /// <param name="task">The task to execute.</param>
            /// <param name="taskWasPreviouslyQueued">Whether the task was previously queued.</param>
            /// <returns>true if the task was executed; otherwise, false.</returns>
            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
                // If we're using our own threads and if this is being called from one of them,
                // or if we're currently processing another task on this thread, try running it inline.
                s_taskProcessingThread.Value && TryExecuteTask(task);

            /// <summary>Runs the specified ask.</summary>
            /// <param name="task">The task to execute.</param>
            internal void ExecuteTask(Task task) => TryExecuteTask(task);

            /// <summary>Gets the maximum concurrency level to use when processing tasks.</summary>
            public override int MaximumConcurrencyLevel => _pool.MaximumConcurrencyLevel;

            /// <summary>Signals that the queue should be removed from the scheduler as soon as the queue is empty.</summary>
            public void Dispose()
            {
                if (_disposed) return;

                lock (_queueLock)
                {
                    // Double-dispose guard: two concurrent callers may both pass the volatile
                    // read above; the inner check serialises them.
                    if (_disposed) return;
                    _disposed = true;

                    // If the queue is already empty, remove it from _queueGroups now.
                    // If it still has items, FindNextTask will call
                    // RemoveQueue_NeedsLock after draining the last item (it checks
                    // _disposed after every successful TryDequeue).
                    if (_workItems.IsEmpty)
                    {
                        _pool.RemoveQueue_NeedsLock(this);
                    }
                }
            }
        }
    }
}
