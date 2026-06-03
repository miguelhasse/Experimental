# Multithreaded Request Pool

A .NET 10 background service that processes requests concurrently through a priority-based channel queue and a dependency-injected dispatcher interface, with per-request completion callbacks and optional progress reporting. Hosted as a multi-service solution under **Aspire 13**.

Production-grade features layered on top of the core pipeline:

- Bounded channels with configurable **`FullMode`** + drop callback (back-pressure or shed-load)
- **Per-request `Timeout`** override, **`CorrelationId`** + log scopes, **`PartitionKey`** fairness within a priority
- **Retry + dead-letter** loop and optional **Polly `ResiliencePipeline`** integration
- **Priority aging** — long-waiting Low/Normal requests are promoted automatically
- Graceful **`DrainTimeout`** on shutdown
- Built-in **OpenTelemetry** metrics/traces and an **`IHealthCheck`** for the pool
- Allocation-reduced hot path (`ValueTask<RequestResult>` dispatch, pre-linked `CancellationTokenSource` per request, `FrozenDictionary` handler cache, typed `IWorkItemCallback`, `ConcurrentQueue` + CAS scheduler queue)

See `assets/diagrams/` for SVG architecture diagrams of the request pool, mediator dispatcher and queued task scheduler.

## Solution structure

```
BackgroundWorkers.slnx
│
├── src/
│   ├── Core/                ← Class library: IRequestPool, mediator dispatcher, priority channels, telemetry
│   ├── Demo.Orleans/        ← Orleans 10 silo: grains submit jobs through IRequestPool; Orleans dashboard
│   ├── Blazor.Dashboard/    ← Blazor Server UI: submit jobs, track status + live progress, pool stats
│   └── Demo.Worker/         ← Standalone worker demo with a simple SampleRequestDispatcher
│
├── aspire/
│   ├── Demo.AppHost/        ← Aspire 13 orchestrator: starts all services
│   └── Demo.ServiceDefaults/ ← Shared OTel (traces + metrics), health checks, service discovery
│
├── benchmarks/
│   └── RequestProcessor.Benchmarks/ ← BenchmarkDotNet project: throughput, scheduling, allocation
│
└── tests/
    ├── Core.Tests/          ← xUnit tests targeting the Core library
    └── Orleans.Tests/       ← xUnit tests targeting the Orleans grain layer
```

## Architecture

```
Caller ──► IRequestPool.EnqueueAsync(context, callback)
                │
     Three BoundedChannels (High / Normal / Low)
                │
     Workers drain using weighted round-robin (default weights: High=5, Normal=3, Low=1)
                │
                ▼
     IRequestDispatcher.DispatchAsync(context, ct)
                │
                ▼
     RequestCompletedCallback(result)       ← called on the worker thread
```

`BoundedCapacity` applies independently per priority channel. Workers use **weighted round-robin** to drain the channels — each priority gets a configurable token budget per cycle, so lower-priority requests are guaranteed to make progress even under sustained high-priority load.

## Running

```bash
# Full solution via Aspire (recommended)
dotnet run --project aspire/Demo.AppHost --launch-profile http
# Aspire dashboard: http://localhost:15004

# Orleans silo standalone (Orleans dashboard at http://localhost:8080)
dotnet run --project src/Demo.Orleans

# Blazor dashboard standalone (requires Orleans silo running first)
dotnet run --project src/Blazor.Dashboard

# Run all tests
dotnet test BackgroundWorkers.slnx

# Run benchmarks (see benchmarks/README.md for details)
dotnet run -c Release --project benchmarks/RequestProcessor.Benchmarks
```

## Quick start — simple dispatcher

```csharp
// Register a single dispatcher (all request types go through one class)
builder.Services.AddRequestPool<MyDispatcher>(options =>
{
    options.MaxConcurrency  = 4;    // parallel workers
    options.BoundedCapacity = 1000; // max queued requests per priority level
});
```

Implement `IRequestDispatcher`:

```csharp
public class MyDispatcher(HttpClient http) : IRequestDispatcher
{
    public async ValueTask<RequestResult> DispatchAsync(
        RequestContext context, CancellationToken ct)
    {
        // cast to the strongly-typed context when needed
        var typed = (RequestContext<MyRequest>)context;
        var response = await http.PostAsJsonAsync("/api/process", typed.Data, ct);
        return new RequestResult(context.RequestId, response.IsSuccessStatusCode,
            await response.Content.ReadAsStringAsync(ct));
    }
}
```

Inject `IRequestPool` to submit work:

```csharp
public class MyService(IRequestPool pool)
{
    public async Task HandleAsync(string id, MyRequest request)
    {
        var context = new RequestContext<MyRequest>(id, request,
            Priority: RequestPriority.High,
            OnProgress: (pct, msg, _) => Console.WriteLine($"{id}: {pct}% — {msg}"));

        await pool.EnqueueAsync(context, result =>
        {
            if (result.Success) Console.WriteLine($"{result.RequestId}: {result.Output}");
            else Console.WriteLine($"{result.RequestId} failed: {result.Error?.Message}");
            return Task.CompletedTask;
        });
    }
}
```

## Quick start — mediator dispatcher

Register one typed handler per request type and let `MediatorRequestDispatcher` route by the static generic type argument of `RequestContext<TData>`:

```csharp
builder.Services
    .AddMediatorRequestPool(options => { options.MaxConcurrency = 4; })
    .AddRequestHandler<OrderRequest, OrderRequestHandler>()
    .AddRequestHandler<ReportRequest, ReportRequestHandler>();
```

Implement `IRequestHandler<TRequest>`:

```csharp
public class OrderRequestHandler : IRequestHandler<OrderRequest>
{
    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<OrderRequest> context, CancellationToken ct)
    {
        // context.Data is strongly typed as OrderRequest
        var order = context.Data;

        context.OnProgress?.Invoke(50, "Half-way through order processing");
        await ProcessOrderAsync(order, ct);

        return new RequestResult(context.RequestId, Success: true, Output: order.Id);
    }
}
```

> **Important**: Always create `RequestContext<ConcreteType>` — never `RequestContext<IMyInterface>`.  
> The mediator resolves handlers by the static generic type argument, so the interface has no registered handler.

## Key types

| Type | Project | Description |
|---|---|---|
| `IRequestPool` | Core | Submit requests; inject into callers |
| `IRequestDispatcher` | Core | Single-class dispatch; implement for simple scenarios |
| `IRequestHandler<TRequest>` | Core | Per-type handler; used with the mediator |
| `MediatorRequestDispatcher` | Core | Routes `RequestContext<T>` to `IRequestHandler<T>` by type |
| `RequestContext` | Core | Abstract base: `RequestId`, `Priority`, `OnProgress` |
| `RequestContext<TData>` | Core | Typed subclass carrying `Data: TData` |
| `RequestResult` | Core | Output: `RequestId`, `Success`, `Output`, `Error` |
| `RequestProgressReporter` | Core | `delegate void(int pct, string? msg, object? delta)` |
| `RequestPriority` | Core | `High`, `Normal`, `Low` |
| `IRequestPoolMonitor` | Core | Stats snapshot + per-request / bulk cancellation |
| `RequestPoolOptions` | Core | `MaxConcurrency`, `BoundedCapacity`, `PriorityWeights`, `TaskSchedulerFactory`, `MaxConcurrentPerPriority` |
| `RequestPoolService` | Core | The `IHostedService` implementation; auto-registered |

## Configuration

Options can be set in code or bound from `appsettings.json`:

```json
{
  "RequestPool": {
    "MaxConcurrency": 8,
    "BoundedCapacity": 5000,
    "PriorityWeights": [1, 3, 5]
  }
}
```

```csharp
services.Configure<RequestPoolOptions>(
    configuration.GetSection(RequestPoolOptions.SectionName));
```

| Property | Default | Description |
|---|---|---|
| `MaxConcurrency` | `Environment.ProcessorCount` | Parallel worker count |
| `BoundedCapacity` | `1 000` | Max queued items per priority channel |
| `FullMode` | `Wait` | `BoundedChannelFullMode` — switch to `DropOldest`/`DropNewest`/`DropWrite` to shed load |
| `OnItemDropped` | `null` | Callback invoked for the dropped `RequestContext` when `FullMode` causes a drop |
| `SingleWriter` | `false` | Enable only when exactly one producer calls `EnqueueAsync` |
| `DrainTimeout` | `Timeout.InfiniteTimeSpan` | Max time to drain on `StopAsync` before cancelling in-flight work |
| `PriorityWeights` | `[1, 3, 5]` | WRR token budget per cycle — `[Low, Normal, High]`. All values must be > 0. High ≈ 56%, Normal ≈ 33%, Low ≈ 11% with defaults. |
| `PartitionFairnessEnabled` | `false` | Enable per-`PartitionKey` round-robin within each priority |
| `PartitionCapacity` | `null` | Per-partition bounded capacity (defaults to `BoundedCapacity`) |
| `PartitionIdleEvictionThreshold` | `5m` | Idle time before an empty partition channel is evicted |
| `PriorityAgingThreshold` | `null` | Time a Low/Normal request may wait before being promoted one tier (null = disabled) |
| `PriorityAgingScanInterval` | `5s` | How often the aging scanner runs |
| `TimeProvider` | `TimeProvider.System` | Time source for aging — override in tests |
| `DispatchTimeoutMs` | `-1` | Per-dispatch timeout in ms. `-1` = none. Overridden by `RequestContext.Timeout` |
| `MaxDispatchAttempts` | `1` | Total dispatcher attempts per request (1 = no retry) |
| `ShouldRetry` | `null` | `(exception, attempt) => bool` predicate; null = retry on any exception |
| `RetryBackoff` | `TimeSpan.Zero` | Delay between failed attempts |
| `OnDeadLetter` | `null` | Invoked when a request exhausts `MaxDispatchAttempts` |
| `ResiliencePipelineFactory` | `null` | Factory for a Polly `ResiliencePipeline` wrapping each dispatch attempt |
| `TaskSchedulerFactory` | `null` | Per-priority `TaskScheduler` factory — called once per priority and cached |
| `MaxConcurrentPerPriority` | `null` | `[Low, Normal, High]` concurrency caps. `0` = uncapped for that tier |

## Cancellation

Requests that are still **queued** can be cancelled before a worker picks them up:

```csharp
// Inject IRequestPoolMonitor
bool removed = monitor.TryCancelRequest(requestId);   // true = was queued and cancelled
int  count   = monitor.CancelAllRequests(RequestPriority.Low); // cancel all low-priority
```

Once a worker has picked up a request, only the `CancellationToken` inside `DispatchAsync` can interrupt it — check `ct.IsCancellationRequested` or call `ct.ThrowIfCancellationRequested()` in your handler.

## Telemetry

`Demo.ServiceDefaults` wires the pool's telemetry into OpenTelemetry automatically via `AddServiceDefaults()`. The Aspire dashboard displays all traces and metrics out of the box.

To integrate with your own OTel setup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(RequestPoolDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(RequestPoolDiagnostics.MeterName));
```

| Metric | Kind | Description |
|---|---|---|
| `requestpool.requests.enqueued` | Counter | Incremented on each `EnqueueAsync` |
| `requestpool.requests.completed` | Counter | Tagged `outcome: success / failure` |
| `requestpool.processing.duration` | Histogram (ms) | Time from dequeue to callback |
| `requestpool.workers.active` | UpDownCounter | Currently dispatching workers |
| `requestpool.queue.depth` | ObservableGauge | Combined queued item count |
| `requestpool.requests.cancelled` | Counter | Requests cancelled before dispatch |
| `requestpool.requests.retried` | Counter | Dispatch attempts that triggered a retry |
| `requestpool.requests.dead_lettered` | Counter | Requests routed to `OnDeadLetter` after exhausting attempts |
| `requestpool.requests.priority_promoted` | Counter | Requests promoted by the aging scanner, tagged `from`/`to` priority |
| `requestpool.partitions.active` | ObservableGauge | Active per-priority partitions (when `PartitionFairnessEnabled`) |
| `requestpool.partitions.evicted` | Counter | Partition channels evicted after idle threshold |

Activities: `requestpool.enqueue` (Producer) → `requestpool.process` (Consumer) — linked as a distributed trace producer/consumer pair.

The Orleans silo also exposes scheduler metrics via the `OrleansBackgroundWorkers.QueuedTaskScheduler` meter: `tasks_queued_total`, `tasks_dispatched_total`, `queue_depth`, `lock_wait_ms`, `groups_active`.

## Health check

```csharp
builder.Services
    .AddRequestPool<MyDispatcher>()
    .AddHealthChecks()
    .AddRequestPoolHealthCheck("request-pool", tags: ["live", "ready"]);
```

Reports `Degraded` when queue depth exceeds the configured high-water mark and `Unhealthy` when the service has stopped accepting work.

## Resilience — retry, dead-letter, Polly

```csharp
services.AddRequestPool<MyDispatcher>(o =>
{
    o.MaxDispatchAttempts = 3;
    o.RetryBackoff        = TimeSpan.FromMilliseconds(200);
    o.ShouldRetry         = (ex, attempt) => ex is HttpRequestException && attempt < 3;
    o.OnDeadLetter        = (ctx, ex) => deadLetterStore.Enqueue(ctx, ex);

    // Optional: wrap each attempt in a Polly resilience pipeline
    o.ResiliencePipelineFactory = sp => new ResiliencePipelineBuilder()
        .AddTimeout(TimeSpan.FromSeconds(30))
        .AddCircuitBreaker(new() { FailureRatio = 0.5 })
        .Build();
});
```

## Per-request features

`RequestContext` carries optional, per-request overrides:

```csharp
var ctx = new RequestContext<OrderRequest>(id, request, Priority: RequestPriority.High)
{
    Timeout       = TimeSpan.FromSeconds(15), // overrides DispatchTimeoutMs
    CorrelationId = activity.Id,              // joined into log scopes + telemetry
    PartitionKey  = $"tenant:{tenantId}",     // fair share within priority (requires PartitionFairnessEnabled)
};
```

## Architecture diagrams

SVG diagrams live under [`assets/diagrams/`](assets/diagrams/):

- `request-pool-architecture.svg` — full pipeline: channels, WRR, partition fairness, aging, retry/DLQ, Polly, CTS pool
- `mediator-dispatcher.svg` — startup registry, `FrozenDictionary` hot path, before/after allocation comparison
- `queued-task-scheduler.svg` — two-pass quantum algorithm, per-group locks, `ConcurrentQueue`+CAS target-scheduler path, OTel

## Orleans sample (`src/Demo.Orleans`)

See [`src/Demo.Orleans/README.md`](src/Demo.Orleans/README.md) for full details.

Grains accept multiple job types via `IJobGrain.SubmitAsync(IJobRequest)`, enqueue them into `IRequestPool` using the mediator dispatcher, then **deactivate immediately**. When the pool worker finishes, it fires captured `IJobCompletionObserver` and `IJobProgressObserver` references that re-activate the grain for a single turn each. Live progress is stored in `IJobTracker` and polled by the Blazor dashboard.

```csharp
var grain = client.GetGrain<IJobGrain>("my-job-id");
await grain.SubmitAsync(new JobRequest("payload", Category: "demo", Priority: RequestPriority.High));

var status   = await grain.GetStatusAsync();   // Pending | Processing | Completed | Failed | Cancelled
var progress = await grain.GetProgressAsync(); // JobProgressSnapshot? { PercentComplete, Message }

bool cancelled = await grain.TryCancelJobAsync(); // cancel while still queued
```

## Blazor dashboard (`src/Blazor.Dashboard`)

Three-tab card at the top:

| Tab | Function |
|---|---|
| **Submit New Job** | Submit a single `JobRequest` with payload, category, priority |
| **Generate Batch** | Bulk-submit N jobs with random payloads and configurable priority |
| **Pool Stats** | Live cluster-wide `RequestPoolStatsSnapshot`: queue depths, active workers, cumulative counters |

All jobs table below shows live status badges, animated progress bars, and progress messages for in-flight jobs. Auto-refresh every 3 seconds.

## Shutdown behaviour

`RequestPoolService.StopAsync` closes the channel writers and starts a drain. Workers complete already-queued items up to `DrainTimeout` (default: infinite). When the timeout elapses, the per-request `CancellationToken` is signalled so handlers can abort cooperatively. `OperationCanceledException` from a dispatcher is treated as a cancellation (not a failure) and does not increment the failure counter.

## Requirements

- .NET 10
- `Microsoft.Extensions.Hosting` 10.x
- `Aspire.AppHost.Sdk` 13.x (AppHost only)
- `Microsoft.Orleans.Server` 10.x (`Demo.Orleans` only)
