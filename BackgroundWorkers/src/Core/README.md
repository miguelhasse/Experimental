# RequestProcessor

A .NET class library that provides a **priority-based background request pool** with a dependency-injected dispatcher interface, per-request completion callbacks, and optional progress reporting. Designed to be hosted via `IHostedService` and integrated into any `IServiceCollection`-based application.

---

## Public API

| Type | Kind | Description |
|---|---|---|
| `IRequestPool` | Interface | Submit requests; inject into callers. Includes `EnqueueAsync(...)` callback overload and `Task<RequestResult> EnqueueAsync(...)` await overload |
| `IRequestDispatcher` | Interface | Single-class dispatch; implement for simple scenarios |
| `IRequestHandler<TRequest>` | Interface | Per-type handler; used with the mediator dispatcher |
| `MediatorRequestDispatcher` | Class | Routes `RequestContext<T>` to `IRequestHandler<T>` by static type argument (`FrozenDictionary` lookup) |
| `RequestContext` | Abstract record | Base: `RequestId`, `Priority`, `OnProgress`, `Timeout`, `CorrelationId`, `PartitionKey` |
| `RequestContext<TData>` | Sealed record | Typed subclass carrying `Data: TData` |
| `RequestProgressReporter` | Delegate | `void(int pct, string? msg, object? delta)` |
| `RequestResult` | Record | `RequestId`, `Success`, `Output` (`string?`), `Error`, `TypedOutput` (`object?`) |
| `RequestCompletedCallback` | Delegate | `void(RequestResult)` — called on worker thread |
| `RequestPriority` | Enum | `High`, `Normal`, `Low` |
| `IRequestPoolMonitor` | Interface | Stats snapshot + per-request / bulk cancellation |
| `RequestPoolStats` | Record | Point-in-time pool metrics |
| `RequestPoolOptions` | Class | All tuning knobs — see [Configuration](#configuration) |
| `RequestPoolHealthCheck` | Class | `IHealthCheck` implementation; register via `AddRequestPoolHealthCheck` |
| `RequestPoolHealthCheckOptions` | Class | Degraded / unhealthy thresholds |
| `RequestPoolDiagnostics` | Static class | `ActivitySourceName`, `MeterName` constants |
| `CancellationTokenSourcePool` | Static class | Pooled `CancellationTokenSource` rental (`Rent` / `Return` / `CreateLinked`) |

---

## How it works

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

`RequestPoolService` is registered as three interfaces from a single instance:
- `IRequestPool` — submit work
- `IRequestPoolMonitor` — stats + cancellation
- `IHostedService` — starts/stops workers automatically

`BoundedCapacity` applies independently per priority channel. Workers use **weighted round-robin** to drain the channels — each priority receives a configurable token budget per cycle, so lower-priority requests are guaranteed progress even under sustained high-priority load.

---

## Simple dispatcher

Register one class that handles every request type:

```csharp
builder.Services.AddRequestPool<MyDispatcher>(options =>
{
    options.MaxConcurrency  = 4;     // parallel workers (default: Environment.ProcessorCount)
    options.BoundedCapacity = 1_000; // max queued per priority level (default: 1 000)
});
```

Implement `IRequestDispatcher`:

```csharp
public class MyDispatcher(HttpClient http) : IRequestDispatcher
{
    public async ValueTask<RequestResult> DispatchAsync(
        RequestContext context, CancellationToken ct)
    {
        var typed = (RequestContext<MyRequest>)context;
        var response = await http.PostAsJsonAsync("/api/process", typed.Data, ct);
        return new RequestResult(
            context.RequestId,
            Success: response.IsSuccessStatusCode,
            Output: await response.Content.ReadAsStringAsync(ct));
    }
}
```

Submit work from anywhere in the application:

```csharp
public class MyService(IRequestPool pool)
{
    public async Task HandleAsync(string id, MyRequest request)
    {
        var context = new RequestContext<MyRequest>(
            RequestId: id,
            Data:      request,
            Priority:  RequestPriority.High,
            OnProgress: (pct, msg, _) => Console.WriteLine($"{id}: {pct}% — {msg}"));

        await pool.EnqueueAsync(context, result =>
        {
            if (result.Success)
                Console.WriteLine($"{result.RequestId}: {result.Output}");
            else
                Console.WriteLine($"{result.RequestId} failed: {result.Error?.Message}");
            return Task.CompletedTask;
        });
    }
}
```

---

## Mediator dispatcher

Register one typed handler per request type; `MediatorRequestDispatcher` routes by the **static generic type argument** of `RequestContext<TData>`:

```csharp
builder.Services
    .AddMediatorRequestPool(options => { options.MaxConcurrency = 4; })
    .AddRequestHandler<OrderRequest,  OrderRequestHandler>()
    .AddRequestHandler<ReportRequest, ReportRequestHandler>();
```

Implement `IRequestHandler<TRequest>`:

```csharp
public class OrderRequestHandler : IRequestHandler<OrderRequest>
{
    public async ValueTask<RequestResult> HandleAsync(
        RequestContext<OrderRequest> context, CancellationToken ct)
    {
        var order = context.Data; // strongly typed — no cast required

        context.OnProgress?.Invoke(50, "Processing order", delta: null);
        await SaveOrderAsync(order, ct);

        return new RequestResult(context.RequestId, Success: true, Output: order.Id);
    }
}
```

> **Important**: always create `RequestContext<ConcreteType>` — never `RequestContext<IMyInterface>`.  
> The mediator resolves handlers by the static generic type argument. An interface has no registered handler and the dispatch will throw `InvalidOperationException`.

---

## `RequestContext<TData>`

```csharp
public sealed record RequestContext<TData>(
    string RequestId,
    TData Data,
    RequestPriority Priority = RequestPriority.Normal,
    RequestProgressReporter? OnProgress = null)
    : RequestContext(RequestId, Priority, OnProgress)
    where TData : notnull;
```

- `RequestId` — caller-supplied identifier; must be unique within the pending queue (duplicate `RequestId` throws `ArgumentException` on `EnqueueAsync`).
- `Data` — the request payload; any non-null type.
- `Priority` — determines which channel the request enters.
- `OnProgress` — optional callback; invoked by the handler to report incremental progress.

### Progress reporting

```csharp
public delegate void RequestProgressReporter(
    int percentComplete,   // 0–100
    string? message,       // optional human-readable status
    object? delta);        // optional handler-specific typed delta
```

Handlers invoke `context.OnProgress?.Invoke(pct, msg, delta)`. The `delta` object is defined by the handler and cast by the callback subscriber. The callback is fired synchronously on the worker thread — keep it fast or dispatch to another thread.

---

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
| `FullMode` | `Wait` | `BoundedChannelFullMode` (`Wait` / `DropOldest` / `DropNewest` / `DropWrite`) |
| `OnItemDropped` | `null` | Callback for dropped `RequestContext` when `FullMode` is a drop mode |
| `SingleWriter` | `false` | Enable only when one producer calls `EnqueueAsync` (channel optimization) |
| `DrainTimeout` | `Timeout.InfiniteTimeSpan` | Max time to drain on `StopAsync` before cancelling in-flight dispatchers |
| `PriorityWeights` | `[1, 3, 5]` | WRR token budget per cycle — `[Low, Normal, High]`. All values must be > 0. Set equal values (e.g. `[1, 1, 1]`) for pure round-robin. |
| `PartitionFairnessEnabled` | `false` | Enable per-`PartitionKey` round-robin within each priority |
| `PartitionCapacity` | `null` | Per-partition bounded capacity (defaults to `BoundedCapacity`) |
| `PartitionIdleEvictionThreshold` | `5m` | Idle time before an empty partition channel is evicted |
| `PriorityAgingThreshold` | `null` | Time a Low/Normal request may wait before being promoted one tier. `null` = disabled |
| `PriorityAgingScanInterval` | `5s` | How often the aging scanner runs |
| `TimeProvider` | `TimeProvider.System` | Time source — override (e.g. `FakeTimeProvider`) in tests |
| `DispatchTimeoutMs` | `-1` | Per-dispatch timeout in ms. `-1` = none. Must be `-1` or > 0. Overridden by `RequestContext.Timeout` |
| `MaxDispatchAttempts` | `1` | Total dispatcher attempts per request (1 = no retry) |
| `ShouldRetry` | `null` | `(exception, attempt) => bool` predicate; `null` = retry on any exception while attempts remain |
| `RetryBackoff` | `TimeSpan.Zero` | Delay between failed attempts |
| `OnDeadLetter` | `null` | Invoked when a request exhausts `MaxDispatchAttempts` or `ShouldRetry` returns `false` |
| `ResiliencePipelineFactory` | `null` | Factory for a Polly `ResiliencePipeline` wrapping each dispatcher invocation (runs **inside** the `MaxDispatchAttempts` loop) |
| `TaskSchedulerFactory` | `null` | Per-priority `TaskScheduler` factory. Called once per priority and cached. Disposable schedulers are owned by the service. |
| `MaxConcurrentPerPriority` | `null` | `[Low, Normal, High]` concurrency caps. `0` = uncapped for that tier. `null` = no per-priority caps. |

---

## Cancellation

Requests that are still **queued** (not yet picked up by a worker) can be cancelled via `IRequestPoolMonitor`:

```csharp
// Inject IRequestPoolMonitor
bool removed = monitor.TryCancelRequest(requestId);
// true  → was queued, removed, callback will NOT be called
// false → already dispatched, completed, or never submitted

int count = monitor.CancelAllRequests(RequestPriority.Low); // cancel all low-priority
int all   = monitor.CancelAllRequests();                    // cancel everything queued
```

Once a worker has picked up a request, only the `CancellationToken` passed to `DispatchAsync` / `HandleAsync` can interrupt it. Call `ct.ThrowIfCancellationRequested()` at safe points in your handler.

`OperationCanceledException` thrown by a dispatcher is treated as a **cancellation** (not a failure) and does not increment the failure counter.

---

## Telemetry

Reference `RequestPoolDiagnostics` constants when registering OpenTelemetry exporters:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(RequestPoolDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(RequestPoolDiagnostics.MeterName));
```

| Constant | Value |
|---|---|
| `ActivitySourceName` | `"RequestProcessor"` |
| `MeterName` | `"RequestProcessor"` |

### Metrics

| Instrument | Kind | Unit | Tags | Description |
|---|---|---|---|---|
| `requestpool.requests.enqueued` | Counter | requests | — | Incremented on each accepted `EnqueueAsync` |
| `requestpool.requests.completed` | Counter | requests | `outcome: success\|failure` | Requests that finished processing |
| `requestpool.processing.duration` | Histogram | ms | — | Time inside `IRequestDispatcher.DispatchAsync` |
| `requestpool.workers.active` | UpDownCounter | workers | — | Workers currently dispatching |
| `requestpool.queue.depth` | ObservableGauge | requests | — | Combined queued item count across all priorities |
| `requestpool.requests.cancelled` | Counter | requests | — | Requests cancelled before dispatch |
| `requestpool.requests.retried` | Counter | requests | `attempt` | Failed attempts that triggered a retry |
| `requestpool.requests.dead_lettered` | Counter | requests | — | Requests routed to `OnDeadLetter` |
| `requestpool.requests.priority_promoted` | Counter | requests | `from`, `to` | Requests promoted by the aging scanner |
| `requestpool.partitions.active` | ObservableGauge | partitions | `priority` | Active partitions when `PartitionFairnessEnabled` |
| `requestpool.partitions.evicted` | Counter | partitions | `priority` | Idle partition channels evicted |

### Traces

Two linked activities form a distributed trace producer/consumer pair:

| Activity | Kind | Description |
|---|---|---|
| `requestpool.enqueue` | Producer | Span created when a request is accepted |
| `requestpool.process` | Consumer | Child span created when a worker begins dispatch; linked to producer |

---

## Shutdown and backpressure

**Backpressure**: `EnqueueAsync` yields (awaits) when a channel is at `BoundedCapacity` until a worker drains an item. Pass a `CancellationToken` to abort the wait. When `FullMode` is one of the drop modes, the channel will instead evict an item according to the chosen policy and invoke `OnItemDropped` for the dropped context.

**Shutdown**: `RequestPoolService.StopAsync` closes the channel writers and starts a drain. Workers complete already-queued items for up to `DrainTimeout` (default: infinite). When the timeout elapses, the in-flight per-request `CancellationToken` is signalled so handlers can abort cooperatively. The `IHostedService` graceful stop token cancels the drain immediately if fired.

---

## Resilience — retry, dead-letter, Polly

```csharp
services.AddRequestPool<MyDispatcher>(o =>
{
    o.MaxDispatchAttempts = 3;
    o.RetryBackoff        = TimeSpan.FromMilliseconds(200);
    o.ShouldRetry         = (ex, attempt) => ex is HttpRequestException && attempt < 3;
    o.OnDeadLetter        = (ctx, ex) => deadLetterStore.Enqueue(ctx, ex);

    // Optional Polly pipeline runs INSIDE each MaxDispatchAttempts iteration
    o.ResiliencePipelineFactory = sp => new ResiliencePipelineBuilder()
        .AddTimeout(TimeSpan.FromSeconds(30))
        .AddCircuitBreaker(new() { FailureRatio = 0.5 })
        .Build();
});
```

`OperationCanceledException` is never retried — it is treated as cancellation and skips both retry and dead-letter paths. Failures from the final attempt (or those where `ShouldRetry` returns `false`) are reported via the completion callback **and** `OnDeadLetter` if configured.

---

## Per-request features

`RequestContext` carries optional overrides applied to a single request:

```csharp
var ctx = new RequestContext<OrderRequest>(id, request, Priority: RequestPriority.High)
{
    Timeout       = TimeSpan.FromSeconds(15), // overrides DispatchTimeoutMs for this request
    CorrelationId = activity.Id,              // joined into log scopes + processing telemetry
    PartitionKey  = $"tenant:{tenantId}",     // fair share within priority (requires PartitionFairnessEnabled)
};
```

`PartitionKey` is used only when `PartitionFairnessEnabled = true`. Each unique key gets its own bounded channel within the priority; workers round-robin between active partitions so a noisy key cannot starve others. Idle partitions are evicted after `PartitionIdleEvictionThreshold`.

---

## Priority aging

Long-waiting Low/Normal requests are promoted one tier (Low → Normal → High) after they have been queued for `PriorityAgingThreshold`. Promotion is exactly-once (CAS marker on the queue entry) and emits `requestpool.requests.priority_promoted`.

```csharp
services.AddRequestPool<MyDispatcher>(o =>
{
    o.PriorityAgingThreshold    = TimeSpan.FromSeconds(30);
    o.PriorityAgingScanInterval = TimeSpan.FromSeconds(5);
});
```

Override `o.TimeProvider` (for example with `Microsoft.Extensions.Time.Testing.FakeTimeProvider`) to drive aging deterministically in tests.

---

## Health check

```csharp
builder.Services
    .AddRequestPool<MyDispatcher>()
    .AddHealthChecks()
    .AddRequestPoolHealthCheck("request-pool", tags: ["live", "ready"]);
```

Returns:

- **Healthy** — queue depth below `DegradedQueueDepthThreshold`
- **Degraded** — queue depth at or above the degraded threshold
- **Unhealthy** — service has stopped accepting work

---

## `Task<RequestResult>` overload

For call sites that prefer to await the result instead of supplying a callback:

```csharp
RequestResult result = await pool.EnqueueAsync(
    new RequestContext<MyRequest>("id", request),
    cancellationToken);
```

Internally this still uses the callback path with a `TaskCompletionSource`, so the result is materialised exactly once on the worker thread.

---

## Performance & allocation notes

The hot path is designed to minimise per-request allocations:

- `IRequestDispatcher.DispatchAsync` returns `ValueTask<RequestResult>` — simple dispatchers (e.g. `ValueTask.FromResult(...)`) complete synchronously with zero heap allocation.
- Each request gets a pre-linked `CancellationTokenSource` (linked to the drain token) at enqueue time; linked CTS instances are `Dispose()`d (not pooled) to avoid stale registrations. Standalone CTS instances are rented from `CancellationTokenSourcePool` via `TryReset`.
- `MediatorRequestDispatcher` resolves handlers from a `FrozenDictionary` built at startup. Handler type resolution uses `RequestContext<TData>.RequestDataType` (a JIT-resolved `typeof(TData)` constant), eliminating runtime reflection and dictionary cache probes on the hot path.
- The worker callback uses a typed `IWorkItemCallback` rather than capturing closures over `RequestContext` and the completion delegate.
- Channel `WaitToReadAnyAsync` returns a cached `ValueTask<bool>` for the common immediate-data case.
- The fast dispatch path bypasses the concurrency gate, timeout CTS, and Polly state machines entirely when none of those features are configured, calling `DispatchAsync` directly without an intermediate state machine.
- Options validation is allocation-free (no LINQ).

---

## Test helpers

The `Core.Tests` project uses a `ServiceFactory` helper that builds a live `RequestPoolService` against a supplied lambda:

```csharp
await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
{
    await Task.Yield();
    return new RequestResult(ctx.RequestId, Success: true, Output: "ok");
});

var tcs = new TaskCompletionSource<RequestResult>(
    TaskCreationOptions.RunContinuationsAsynchronously);

await f.Service.EnqueueAsync(
    new RequestContext<string>("id-1", "data"),
    r => tcs.TrySetResult(r));

var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
```

Use `TaskCreationOptions.RunContinuationsAsynchronously` on `TaskCompletionSource` to avoid deadlocks when the callback runs inline on a pool worker.
