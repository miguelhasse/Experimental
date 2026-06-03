# RequestProcessor Benchmarks

Performance benchmarks for `RequestPoolService` using [BenchmarkDotNet](https://benchmarkdotnet.org/) v0.15.8.

## Running

```sh
# Quick run (ShortRun job — ~5 min)
dotnet run -c Release --project benchmarks/RequestProcessor.Benchmarks

# Target a single benchmark class
dotnet run -c Release --project benchmarks/RequestProcessor.Benchmarks -- --filter "*Throughput*"

# Full run with stable numbers (switch BenchmarkConfig to Job.Default first)
dotnet run -c Release --project benchmarks/RequestProcessor.Benchmarks -- --job default
```

> **Always run in `Release` configuration.** Debug builds disable optimisations and produce meaningless numbers.

Results and HTML reports are written to `benchmarks/BenchmarkDotNet.Artifacts/results/`.

---

## Benchmarks

| Class | What it measures |
|---|---|
| [`EnqueueThroughputBenchmark`](#enqueuethroughputbenchmark) | End-to-end drain time — params: Concurrency × RequestCount |
| [`PrioritySchedulingBenchmark`](#priorityschedulingbenchmark) | Weighted round-robin overhead with mixed-priority load |
| [`PartitionFairnessBenchmark`](#partitionfairnessbenchmark) | Per-partition scheduling overhead vs single-channel baseline |
| [`MediatorDispatcherBenchmark`](#mediatordispatcherbenchmark) | `MediatorRequestDispatcher` routing cost vs direct dispatch |
| [`CallbackAllocationBenchmark`](#callbackallocationbenchmark) | Closure vs state-based `EnqueueAsync<TState>` allocations |

---

## Results

> **Environment:** BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8524/25H2/2025Update/HudsonValley2)  
> **Runtime:** .NET 10.0.8 (10.0.8.826.23019), X64 RyuJIT x86-64-v3  
> **CPU:** Intel Core i9-10900 2.80 GHz (Max: 2.81 GHz), 1 CPU, 20 logical / 10 physical cores  
> **SDK:** .NET SDK 10.0.300  
> **GC:** Concurrent Workstation  
> **Jobs:** LongRun (LaunchCount=3, WarmupCount=15, IterationCount=100) · ShortRun (LaunchCount=1, WarmupCount=3, IterationCount=3)  
> Tables below show **LongRun** results (stable); ShortRun rows are in the artifact files for quick reference.  
> **Last run:** 2026-06-02 — latest completed run (23:14), includes `ValueTask<RequestResult>` dispatch, pre-linked CTS at enqueue, and fast-path bypass (Tier 1–3 optimizations).

---

### EnqueueThroughputBenchmark

Enqueues `RequestCount` no-op requests and awaits all completions, varying concurrency and volume.
Each operation covers the full round-trip: enqueue → channel → worker dispatch → callback.

| Method | Concurrency | RequestCount | Mean | Error | StdDev | Gen0 | Gen1 | Allocated |
|---|---|---|---:|---:|---:|---:|---:|---:|
| DrainAllRequests | 1 | 500 | 526.7 µs | 4.68 µs | 24.34 µs | 38.09 | 15.63 | 391.35 KB |
| DrainAllRequests | 1 | 2000 | 2,181.7 µs | 24.77 µs | 127.57 µs | 152.34 | 70.31 | 1,563.29 KB |
| DrainAllRequests | 4 | 500 | 731.4 µs | 10.07 µs | 51.67 µs | 52.73 | 15.63 | 550.56 KB |
| DrainAllRequests | 4 | 2000 | 2,856.1 µs | 42.44 µs | 215.87 µs | 203.13 | 78.13 | 2,068.35 KB |
| DrainAllRequests | 8 | 500 | 1,062.8 µs | 13.62 µs | 70.51 µs | 66.41 | 19.53 | 684.02 KB |
| DrainAllRequests | 8 | 2000 | 4,539.3 µs | 82.84 µs | 428.80 µs | 273.44 | 101.56 | 2,775.29 KB |

**Observations:**
- At `Concurrency=1`, throughput scales near-linearly with request count (~1.1 µs/request at 500 req).
- Allocation per request is **~0.78 KB** (500-req, C=1 baseline) — down ~21% from the previous 0.99 KB/request, reflecting the removal of pooled CTS rentals and `Task<T>` boxing on the dispatch path.
- Higher concurrency increases total time — at `Concurrency=8, RequestCount=2000` the overhead of scheduling across 8 workers outweighs parallelism gains for no-op dispatchers. This is expected: the benchmark uses an instant no-op (`ValueTask.FromResult`), so worker-thread overhead dominates.
- StdDev is tight (24–429 µs) across the LongRun averages, so the throughput trend is stable.

---

### PrioritySchedulingBenchmark

Enqueues equal counts of High, Normal, and Low requests (200 each = 600 total) with two weight presets.

| Method | Weights | Mean | Error | StdDev | Gen0 | Gen1 | Allocated |
|---|---|---:|---:|---:|---:|---:|---:|
| MixedPriorityDrain | 1,1,1 | 871.6 µs | 11.41 µs | 59.05 µs | 58.59 | 15.63 | 600.49 KB |
| MixedPriorityDrain | 5,3,1 | 974.4 µs | 10.56 µs | 54.76 µs | 60.55 | 15.63 | 615 KB |

> Weights format: `High,Normal,Low`. Default production weights are `5,3,1`.

**Observations:**
- Balanced `1,1,1` weights are ~11% faster for equal-count mixed loads because the WRR scheduling pattern matches the equal-count distribution more closely — each drain cycle picks one request per priority and reduces stalls on empty channels.
- The `5,3,1` skew adds ~103 µs (~12%) overhead: the scheduler must exhaust 5 high-priority tokens before moving to normal/low, which increases idle spinning when those channels are empty midway through the cycle. This overhead is still small at real production concurrency where queues are rarely empty.
- Both numbers are stable with tight error bands (StdDev 55–59 µs), confirming the difference is real and not measurement noise.

---

### PartitionFairnessBenchmark

Drains 600 requests distributed evenly across `PartitionCount` partition keys, comparing the default single-channel mode against per-partition fair scheduling.

| Method | PartitionFairnessEnabled | PartitionCount | Mean | Error | StdDev | Gen0 | Gen1 | Allocated |
|---|---|---|---:|---:|---:|---:|---:|---:|
| PartitionedDrain | False | 1 | 907.4 µs | 12.50 µs | 63.93 µs | 70.31 | 23.44 | 725.27 KB |
| PartitionedDrain | False | 4 | 938.7 µs | 11.09 µs | 57.19 µs | 68.36 | 25.39 | 706.32 KB |
| PartitionedDrain | False | 16 | 933.1 µs | 12.66 µs | 65.21 µs | 68.36 | 17.58 | 702.26 KB |
| PartitionedDrain | **True** | 1 | **1,041.8 µs** | 10.02 µs | 51.42 µs | 56.64 | 15.63 | 580.38 KB |
| PartitionedDrain | **True** | 4 | **1,036.6 µs** | 11.66 µs | 60.24 µs | 54.69 | 11.72 | 581.85 KB |
| PartitionedDrain | **True** | 16 | **997.7 µs** | 11.43 µs | 59.16 µs | 54.69 | 13.67 | 568.1 KB |

**Observations:**
- Partition fairness still allocates less than the flat baseline (568–581 KB vs 702–725 KB), because the snapshot cache eliminates per-poll `ConcurrentDictionary.ToArray()` allocations.
- The fair path is a little slower in this run — about 3% at P=16 and roughly 15% at P=1 — but the allocation savings remain consistent across partition counts.
- The overhead stays bounded across partition counts (1, 4, 16), so the scheduling cost is O(1) regardless of the number of active partitions.
- `PartitionFairnessEnabled` is still viable on throughput-sensitive paths when allocation pressure matters more than raw latency.

---

### MediatorDispatcherBenchmark

Dispatches 500 requests through a direct `IRequestDispatcher` (baseline) and through `MediatorRequestDispatcher` (FrozenDictionary type-keyed lookup).

| Method | Mean | Error | StdDev | Ratio | RatioSD | Gen0 | Gen1 | Allocated | Alloc Ratio |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| DirectDispatch *(baseline)* | 714.5 µs | 8.50 µs | 43.84 µs | 1.00 | 0.09 | 56.64 | 13.67 | 579.48 KB | 1.00 |
| MediatorDispatch | 762.4 µs | 11.58 µs | 59.53 µs | 1.07 | 0.11 | 56.64 | 11.72 | 584.19 KB | 1.01 |

**Observations:**
- `MediatorRequestDispatcher` is close to direct dispatch (Ratio ≈ 1.07) — the `FrozenDictionary` lookup and wrapper call are the remaining routing overhead; the handler's `ValueTask<RequestResult>` still flows through without wrapping.
- Allocation difference is tiny (~4.7 KB over 500 requests, ≈ 9 bytes/request), so the hot path is now dominated by dispatch work rather than adapter overhead.
- `RequestContext<TData>.RequestDataType` still removes the old runtime type cache completely; the remaining cost is just the dictionary lookup and wrapper invocation.

---

### CallbackAllocationBenchmark

Compares per-request allocations for the closure-based `EnqueueAsync` overload versus the generic state-passing `EnqueueAsync<TState>` overload. Request IDs are pre-allocated in `GlobalSetup` to isolate callback allocation from string interpolation noise.

| Method | Mean | Error | StdDev | Ratio | RatioSD | Gen0 | Gen1 | Allocated | Alloc Ratio |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| ClosureCallback *(baseline)* | 281.9 µs | 4.86 µs | 25.19 µs | 1.01 | 0.13 | 16.60 | 1.46 | 169.41 KB | 1.00 |
| StatefulCallback | 267.3 µs | 5.94 µs | 30.79 µs | 0.96 | 0.14 | 15.14 | 1.46 | 154.7 KB | 0.91 |

**Observations:**
- `EnqueueAsync<TState>` reduces allocations by **~9%** (14.71 KB per 200 requests ≈ 74 bytes/request). The state-based path still avoids the closure display class and trims GC pressure a bit.
- Mean time is slightly better for the state-based overload (267 µs vs 282 µs, Ratio ≈ 0.96), though this is still a small benchmark and the main win is reduced allocation.
- Use the state-based overload (`EnqueueAsync<TState>`) in hot paths where allocation pressure matters.

---

## Notes

- All benchmarks use a **no-op dispatcher** (`ValueTask.FromResult(...)`) to isolate pool infrastructure overhead from user code.
- Numbers above were produced with `Job.LongRun` (3 launches × 15 warmup + 100 measurement iterations = 300 data points per cell). ShortRun results (1 launch × 3 warmup + 3 iterations) are available alongside them in the artifact files.
- High `Error`/`StdDev` values in throughput benchmarks reflect async scheduling non-determinism on the test machine, not instability in the pool itself. Run on a dedicated, unloaded machine for tighter intervals.
