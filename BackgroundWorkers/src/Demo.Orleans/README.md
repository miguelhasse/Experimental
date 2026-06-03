# Demo.Orleans

Demonstrates how to dispatch multiple types of background jobs from [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/) 10 grains using the `Core` request pool and a **mediator-pattern dispatcher**, with `IGrainObserver` as the asynchronous completion and progress-reporting mechanism.

Grains **deactivate immediately after enqueuing** a job. When the pool worker finishes (or reports progress), it fires a captured observer grain reference, which causes Orleans to re-activate the grain for exactly one turn to process the event. No activation is held open while work runs.

---

## Projects referenced

| Project | Role |
|---|---|
| `src/Core` | `IRequestPool`, `IRequestDispatcher`, mediator, priority channels, telemetry |
| `aspire/Demo.ServiceDefaults` | OpenTelemetry, health checks, service discovery |
| `Microsoft.Orleans.Server` 10.x | In-process silo + cluster client |

---

## Job types

Three concrete job request types are registered with the mediator for `JobGrain`:

| Type | Priority default | Handler | Description |
|---|---|---|---|
| `JobRequest` | Normal | `JobRequestHandler` | Simple payload job — 5 phases, 16 steps |
| `BatchJobRequest` | High | `BatchJobRequestHandler` | Multi-item batch — 3 sub-ops per item |
| `ScheduledJobRequest` | Low | `ScheduledJobRequestHandler` | Deferred job — 5 phases, 13 steps |

Three additional types are registered for `ReportGrain`:

| Type | Priority default | Handler | Description |
|---|---|---|---|
| `GenerateReportRequest` | High | `GenerateReportHandler` | Generate report content — 4 phases, 10 steps |
| `ReviewReportRequest` | Normal | `ReviewReportHandler` | Review / QA the report — 3 phases, 8 steps |
| `PublishReportRequest` | Low | `PublishReportHandler` | Publish to output target — 3 phases, 7 steps |

Three additional types are registered for `NotificationGrain`:

| Type | Priority default | Handler | Description |
|---|---|---|---|
| `EmailNotificationRequest` | Normal | `EmailNotificationHandler` | Email channel — 5 phases, 8 steps |
| `SmsNotificationRequest` | Normal | `SmsNotificationHandler` | SMS channel — 3 phases, 5 steps |
| `PushNotificationRequest` | Normal | `PushNotificationHandler` | Push channel — 3 phases, 4 steps |

Three additional types are registered for `DocumentProcessingGrain`:

| Type | Priority default | Handler | Description |
|---|---|---|---|
| `ExtractContentRequest` | High | `ExtractContentHandler` | Step 1 — parse raw document — 4 phases, 10 steps |
| `TransformContentRequest` | Normal | `TransformContentHandler` | Step 2 — keyword extraction + sentiment — 4 phases, 10 steps |
| `IndexContentRequest` | Normal | `IndexContentHandler` | Step 3 — write to index — 4 phases, 8 steps |

All handlers simulate multi-step work with randomised delays and ~4% per-step fault injection (see [Fault simulation](#fault-simulation)).

---

## `IReportGrain` — idempotent multi-operation grain

`IReportGrain` demonstrates a grain whose **three named operations each dispatch an independent queued request** while guaranteeing idempotency.

```csharp
var report = client.GetGrain<IReportGrain>("rpt-20240101");

var genStatus = await report.GenerateAsync();  // dispatches GenerateReportRequest
var revStatus = await report.ReviewAsync();    // dispatches ReviewReportRequest
var pubStatus = await report.PublishAsync();   // dispatches PublishReportRequest

// Calling again is safe — no double dispatch
genStatus = await report.GenerateAsync();      // returns Pending/Processing/Completed; no re-enqueue

var summary = await report.GetSummaryAsync();
// ReportSummary { GenerateStatus: Completed, ReviewStatus: Processing, PublishStatus: Pending }
```

### Idempotency contract

| Current status | Calling the method again |
|---|---|
| `Pending` or `Processing` | No-op — returns current status |
| `Completed` | No-op — returns `Completed` |
| `Unknown` or `Failed` | Dispatches a new request (first run or retry) |

### Per-operation tracker keys

Each operation is tracked independently in `IJobTracker` using composite keys:

- `"{reportId}:generate"`
- `"{reportId}:review"`
- `"{reportId}:publish"`

This reuses the existing `IJobTracker` with no changes.

### Observer pattern

`ReportGrain` implements `IJobCompletionObserver` only (no progress observer — progress is written inside the closure). The pool callback closure **captures the operation key** and updates `IJobTracker` directly for the specific operation, then fires the unified observer:

```csharp
await pool.EnqueueAsync(
    new RequestContext<GenerateReportRequest>(opKey, request, ...),
    result =>
    {
        if (result.Error is OperationCanceledException)
        {
            tracker.SetStatus(opKey, JobStatus.Cancelled);
            completionRef.OnCanceled();
        }
        else if (result.Error is not null)
        {
            tracker.SetStatus(opKey, JobStatus.Failed);
            completionRef.OnFaulted(result.Error);
        }
        else
        {
            tracker.SetStatus(opKey, JobStatus.Completed);
            completionRef.OnCompleted(result.TypedOutput as JobOutput ?? new TextJobOutput(result.Output ?? string.Empty));
        }
    });
```

The closure captures `opKey` precisely — no RequestId parsing needed. `OnCompleted`/`OnCanceled`/`OnFaulted` on the grain handle cross-cutting concerns (logging at the report level).

### Lifecycle diagram

```
Client               ReportGrain (active)          RequestPool (thread pool)
  │                       │                                  │
  │──GenerateAsync()──────▶│                                  │
  │                       │──EnqueueAsync()──────────────────▶│
  │◀──JobStatus.Pending───│  DeactivateOnIdle()               │──GenerateReportHandler──▶ (work)
  │                       ·                                  │
  │──ReviewAsync()────────▶ [re-activated]                    │
  │                       │──EnqueueAsync()──────────────────▶│
  │◀──JobStatus.Pending───│  DeactivateOnIdle()               │──ReviewReportHandler──▶ (work)
  │                       ·                                  │
  │                       ·  ◀──OnCompleted(output)──────────────│
  │               [re-activated, updates tracker, deactivates]│
  │                       ·                                  │
  │──GenerateAsync()──────▶ [re-activated]                    │
  │◀──JobStatus.Completed─│  (no-op: already done)            │
```

---

## `INotificationGrain` — 3 idempotent independent channel dispatches

`INotificationGrain` demonstrates a grain whose **three named methods each dispatch a different queued request type** to independent mediator-registered handlers, all running concurrently in the pool.

The **Blazor dashboard** provides a dedicated [Notifications tab](../Blazor.Dashboard/README.md#notifications-page-notifications) for creating notification grain instances and invoking each channel method interactively.

```csharp
var notification = client.GetGrain<INotificationGrain>("notif-42");

// All three channels are dispatched independently — no ordering requirement.
var emailStatus = await notification.SendEmailAsync("user@example.com", "Welcome!");
var smsStatus   = await notification.SendSmsAsync("+15550001234", "Your code is 9182");
var pushStatus  = await notification.SendPushAsync("token-abc123", "New message arrived");

// Calling again is safe — already-queued channels are a no-op.
emailStatus = await notification.SendEmailAsync("user@example.com", "Welcome!");
// returns JobStatus.Pending or Processing; no re-enqueue

var snapshot = await notification.GetAllStatusAsync();
// NotificationSnapshot { EmailStatus: Processing, SmsStatus: Completed, PushStatus: Pending }
```

### Idempotency contract

| Current channel status | Calling `Send*Async()` again |
|---|---|
| `Pending` or `Processing` | No-op — returns current status immediately |
| `Completed` | No-op — returns `Completed` (channel already delivered) |
| `Unknown` | Dispatches a new request (first send) |
| `Failed` or `Cancelled` | Re-dispatches — retries the channel |

### Per-channel tracker keys

Each channel is tracked independently in `IJobTracker` using composite keys:

- `"{notificationId}:email"`
- `"{notificationId}:sms"`
- `"{notificationId}:push"`

This reuses the existing `IJobTracker` with no changes.

### Observer pattern

`NotificationGrain` implements `IJobCompletionObserver` and `IJobProgressObserver`. The pool callback closure **captures the channel key** and updates `IJobTracker` directly, then fires the unified observer:

```csharp
await pool.EnqueueAsync(
    new RequestContext<EmailNotificationRequest>(channelJobId, request, ...),
    result =>
    {
        if (result.Error is OperationCanceledException)
        {
            tracker.SetStatus(channelJobId, JobStatus.Cancelled);
            completionRef.OnCanceled();
        }
        else if (result.Error is not null)
        {
            tracker.SetStatus(channelJobId, JobStatus.Failed);
            completionRef.OnFaulted(result.Error);
        }
        else
        {
            tracker.SetStatus(channelJobId, JobStatus.Completed);
            completionRef.OnCompleted(result.TypedOutput as JobOutput ?? new TextJobOutput(result.Output ?? string.Empty));
        }
    });
```

The closure captures `channelJobId` precisely — no parsing needed. `OnCompleted`/`OnCanceled`/`OnFaulted` on the grain handle cross-cutting logging at the notification level.

### Lifecycle diagram

```
Client                NotificationGrain (active)       RequestPool (thread pool)
  │                          │                                  │
  │──SendEmailAsync()────────▶│                                  │
  │                          │──EnqueueAsync("...:email")───────▶│
  │◀──JobStatus.Pending──────│  DeactivateOnIdle()               │──EmailNotificationHandler──▶
  │                          ·                                  │
  │──SendSmsAsync()──────────▶ [re-activated]                    │
  │                          │──EnqueueAsync("...:sms")─────────▶│──SmsNotificationHandler──▶
  │◀──JobStatus.Pending──────│  DeactivateOnIdle()               │
  │                          ·                                  │
  │                          ·  ◀──OnCompleted(output)──────────│
  │              [re-activated, logs at notification level]   │
  │                          ·                                  │
  │──SendEmailAsync()────────▶ [re-activated]                    │
  │◀──JobStatus.Completed────│  (no-op: already done)            │
```

---

## `IDocumentProcessingGrain` — chained sequential pipeline

`IDocumentProcessingGrain` demonstrates a grain that **chains three pool requests sequentially**, passing each step's typed `JobOutput` as input to the next request. This is the only grain pattern where the output of one handler directly feeds the input of the next.

```csharp
var pipeline = client.GetGrain<IDocumentProcessingGrain>("pipe-abc1");

var status = await pipeline.RunAsync();
// status: JobStatus.Processing — step 1 (Extract) is now queued

var snapshot = await pipeline.GetSummaryAsync();
// DocumentProcessingSnapshot {
//   Step1Status: Completed, Step1Output: ExtractedContentOutput { WordCount: 842, ... }
//   Step2Status: Processing, Step2Progress: { PercentComplete: 60, Message: "Extracting keywords (3/4)" }
//   Step3Status: Unknown
// }
```

### Pipeline steps

| Step | Request | Handler | Produces | Feeds |
|---|---|---|---|---|
| 1 | `ExtractContentRequest` | `ExtractContentHandler` | `ExtractedContentOutput` (raw text, word count) | Step 2 |
| 2 | `TransformContentRequest` | `TransformContentHandler` | `TransformedContentOutput` (keywords, sentiment score) | Step 3 |
| 3 | `IndexContentRequest` | `IndexContentHandler` | `IndexedContentOutput` (index ID, tag count) | — |

### Chaining mechanism

`RunAsync()` dispatches only step 1. Each step's dispatch closure calls `completionRef.OnCompleted(output)` on success. The grain's `OnCompleted(JobOutput output)` implementation **routes by concrete output type** to determine which step completed, stores the output, and enqueues the next step:

```csharp
public async Task OnCompleted(JobOutput output)
{
    var (step, stepKey) = output switch
    {
        ExtractedContentOutput   => (Step1, StepKey(pipelineId, Step1)),
        TransformedContentOutput => (Step2, StepKey(pipelineId, Step2)),
        IndexedContentOutput     => (Step3, StepKey(pipelineId, Step3)),
        _                        => throw new InvalidOperationException(...),
    };

    tracker.SetStatus(stepKey, JobStatus.Completed);
    tracker.SetOutput(stepKey, output);

    if (step == Step1)      await DispatchStep2Async(pipelineId);
    else if (step == Step2) await DispatchStep3Async(pipelineId);
    // Step 3 complete: pipeline done.
}
```

Cancel and fault tracker updates are performed inside each dispatch closure (which captures the exact `stepKey`), not in the observer methods — this prevents stale callbacks from a previous run from corrupting a newly-restarted pipeline under `[AlwaysInterleave]` concurrency.

If any step fails or is cancelled, the chain stops. Calling `RunAsync()` again restarts from step 1, clearing all prior outputs.

### Idempotency contract

| Any step status | Calling `RunAsync()` |
|---|---|
| Any step is `Processing` | No-op — returns current step 1 status |
| All steps non-`Processing` | Full restart from step 1 (all steps reset to `Unknown`) |

### Tracker keys

- `"{pipelineId}:step1"`, `"{pipelineId}:step2"`, `"{pipelineId}:step3"`

Typed outputs are stored under the same keys via `IJobTracker.SetOutput`.

---

## Key types

| Type | Description |
|---|---|
| `IJobGrain` | `SubmitAsync(IJobRequest)`, `GetStatusAsync()`, `GetProgressAsync()`, `TryCancelJobAsync()` |
| `IJobCompletionObserver` | `Task OnCompleted(JobOutput)`, `Task OnCanceled()`, `Task OnFaulted(Exception)` — all `[OneWay, AlwaysInterleave]` |
| `IJobProgressObserver` | `Task OnProgress(JobProgressUpdate)` — `[OneWay, AlwaysInterleave]` |
| `IJobDataObserver` | `Task OnDataReceived(JobDataPayload, CancellationToken)` — awaitable (not `[OneWay]`); for mid-handler streaming |
| `JobGrain` | Implements `IJobCompletionObserver + IJobProgressObserver`; submits work, deactivates, handles callbacks |
| `IReportGrain` | `GenerateAsync()`, `ReviewAsync()`, `PublishAsync()`, `GetSummaryAsync()` — all idempotent |
| `ReportGrain` | Implements `IJobCompletionObserver` only; per-operation tracker writes in closure; idempotency via composite keys |
| `INotificationGrain` | `SendEmailAsync()`, `SendSmsAsync()`, `SendPushAsync()`, `GetAllStatusAsync()` — all idempotent per channel |
| `NotificationGrain` | Implements `IJobCompletionObserver + IJobProgressObserver`; channel tracker writes in closure; composite keys |
| `IJobTracker` | Process-scoped singleton; thread-safe status + progress store |
| `IJobRequest` | Marker interface — `JobRequest`, `BatchJobRequest`, `ScheduledJobRequest`, `GenerateReportRequest`, `ReviewReportRequest`, `PublishReportRequest`, `EmailNotificationRequest`, `SmsNotificationRequest`, `PushNotificationRequest`, `ExtractContentRequest`, `TransformContentRequest`, `IndexContentRequest` |
| `JobOutput` | Abstract base for typed handler output — subtypes: `TextJobOutput`, `ExtractedContentOutput`, `TransformedContentOutput`, `IndexedContentOutput` |
| `JobDataPayload` | Abstract base for mid-handler streaming data — concrete: `RawDataPayload` |
| `JobProgressUpdate` | `JobId`, `PercentComplete`, `Message`, `Delta` (typed `JobProgressDelta`) |
| `JobProgressSnapshot` | Serialisable point-in-time progress — returned by `GetProgressAsync()` |
| `JobStatus` | `Unknown → Pending → Processing → Completed / Failed / Cancelled` |
| `ReportSummary` | `ReportId`, `GenerateStatus`, `ReviewStatus`, `PublishStatus` |
| `IPoolStatsGrain` | Aggregates `RequestPoolStatsSnapshot` across all active silos |
| `RequestPoolGrainService` | Per-silo grain service bridging `IRequestPoolMonitor` to grains |

---

## Architecture

### Mediator dispatcher

`MediatorRequestDispatcher` resolves the correct handler by the **static generic type argument** of `RequestContext<TData>`. Handlers are registered for the concrete types:

```csharp
builder.Services
    .AddMediatorRequestPool(options => { options.MaxConcurrency = 4; })
    .AddRequestHandler<JobRequest,              JobRequestHandler>()
    .AddRequestHandler<BatchJobRequest,         BatchJobRequestHandler>()
    .AddRequestHandler<ScheduledJobRequest,     ScheduledJobRequestHandler>()
    .AddRequestHandler<GenerateReportRequest,   GenerateReportHandler>()
    .AddRequestHandler<ReviewReportRequest,     ReviewReportHandler>()
    .AddRequestHandler<PublishReportRequest,    PublishReportHandler>()
    .AddRequestHandler<EmailNotificationRequest, EmailNotificationHandler>()
    .AddRequestHandler<SmsNotificationRequest,   SmsNotificationHandler>()
    .AddRequestHandler<PushNotificationRequest,  PushNotificationHandler>()
    .AddRequestHandler<ExtractContentRequest,    ExtractContentHandler>()
    .AddRequestHandler<TransformContentRequest,  TransformContentHandler>()
    .AddRequestHandler<IndexContentRequest,      IndexContentHandler>();
```

> **Critical**: `JobGrain.SubmitAsync` pattern-matches on the concrete `IJobRequest` type to create the correctly-typed `RequestContext<T>`. Never create `RequestContext<IJobRequest>` — the interface has no registered handler.

### Grain lifecycle

```
Client             JobGrain (active)             RequestPool (thread pool)
  │                     │                                 │
  │──SubmitAsync()──────▶│                                 │
  │                     │──EnqueueAsync()────────────────▶│
  │                     │◀──(accepted)────────────────────│
  │                     │  DeactivateOnIdle()              │
  │                     │  [activation released]           │──HandleAsync()──▶ (work)
  │                     ·                                 │
  │                 [deactivated]                         │  OnProgress fires periodically
  │                     ·   ◀─────────OnProgress()────────│
  │             [re-activated, one turn, deactivates]     │
  │                     ·                                 │
  │                     ·   ◀─────────OnCompleted()───────│
  │             [re-activated, one turn]                  │
  │                     │  tracker.SetStatus(Completed)   │
  │                     │  [deactivates again]            │
  │                     ·                                 │
  │──GetStatusAsync()───▶ [re-activated if needed]        │
  │◀──Completed──────────│                                │
```

### Progress reporting

Handlers call `context.OnProgress?.Invoke(pct, message, delta)` at each step. `JobGrain` receives `OnProgress(JobProgressUpdate)` as a one-way grain message and stores the latest snapshot in `IJobTracker`. The Blazor dashboard polls `GetProgressAsync()` every 3 seconds alongside status.

Typed delta types for each handler:

| Delta type | Handler | Fields |
|---|---|---|
| `JobStepProgressDelta` | `JobRequestHandler` | `Step`, `TotalSteps` |
| `BatchItemProgressDelta` | `BatchJobRequestHandler` | `ProcessedCount`, `TotalItems`, `CurrentItem` |
| `ScheduledJobProgressDelta` | `ScheduledJobRequestHandler` | `Phase` |

### Cancellation

```csharp
bool cancelled = await grain.TryCancelJobAsync();
```

- Returns `true` if the request was still queued and was removed.  
- Returns `false` if already dispatched (cancellation via `CancellationToken` inside the handler would be needed in that case).  
- Sets `JobStatus.Cancelled` in the tracker when `true`.

### Cluster-wide pool stats

`PoolStatsGrain` (key `"default"`) fans out `GetStatisticsAsync(siloAddress)` to every active silo via `IManagementGrain.GetHosts()`, then aggregates by summing all fields:

```csharp
var stats = await client.GetGrain<IPoolStatsGrain>("default").GetSnapshotAsync();
// stats.SiloCount, stats.ActiveWorkers, stats.TotalEnqueued, ...
```

### Grain service layer

`RequestPoolGrainService` (`IGrainService`) runs exactly once per silo and bridges `IRequestPoolMonitor` to grains. It exposes stats and bulk-cancellation but is **not** on the job submission path. Grains inject `IRequestPoolGrainServiceClient`; it routes every call to the service on the **same silo** as the calling grain.

---

## Fault simulation

All three handlers call `FaultInjector.MaybeThrow(step, jobId)` at each processing step (~4% probability). When triggered it throws one of:

- `InvalidOperationException` — unexpected state
- `TimeoutException` — simulated timeout
- `IOException` — simulated I/O error
- `ApplicationException` — downstream service error

`RequestPoolService` catches any thrown exception, wraps it in `RequestResult(Success: false, Error: ex)`, and fires `completionRef.OnFaulted(ex)` → `JobStatus.Failed`.

---

## Observer interfaces — `Task`, not `void`

Unlike the Orleans documentation which states observer methods must be `void`, this project uses `Task` with `[OneWay]` — Orleans fires these as one-way messages and the caller does not await them. All methods carry `[AlwaysInterleave]` so they are never blocked by another processing turn.

Three unified observer interfaces replace the old per-grain observers:

```csharp
[Alias("Grains.JobCompletionObserver")]
public interface IJobCompletionObserver : IGrainObserver
{
    [Alias("OnCompleted"), AlwaysInterleave, OneWay]
    Task OnCompleted(JobOutput result);

    [Alias("OnCanceled"), AlwaysInterleave, OneWay]
    Task OnCanceled();

    [Alias("OnFaulted"), AlwaysInterleave, OneWay]
    Task OnFaulted(Exception exception);
}

[Alias("Grains.JobProgressObserver")]
public interface IJobProgressObserver : IGrainObserver
{
    [Alias("OnProgress"), AlwaysInterleave, OneWay]
    Task OnProgress(JobProgressUpdate progress);
}

[Alias("Grains.JobDataObserver")]
public interface IJobDataObserver : IGrainObserver
{
    // NOT [OneWay] — awaitable; intended for mid-handler streaming
    [Alias("OnDataReceived"), AlwaysInterleave]
    Task OnDataReceived(JobDataPayload data, CancellationToken cancellationToken = default);
}
```

`OnFaulted(Exception exception)` passes raw `System.Exception` across the grain boundary. Orleans 10's `ExceptionCodec` handles serialisation for all `System.*`, `Microsoft.*`, and `Azure.*` namespace prefixes by default — no `Program.cs` changes needed for the BCL exceptions thrown by fault simulation.

Each grain captures observer references before calling `EnqueueAsync`:

```csharp
var completionRef = this.AsReference<IJobCompletionObserver>();
var progressRef   = this.AsReference<IJobProgressObserver>();

await pool.EnqueueAsync(context, result =>
{
    if      (result.Error is OperationCanceledException) completionRef.OnCanceled();
    else if (result.Error is not null)                   completionRef.OnFaulted(result.Error);
    else                                                 completionRef.OnCompleted(result.TypedOutput as JobOutput ?? ...);
    return Task.CompletedTask;
});
```

---

## Orleans serialisation conventions

Every type that crosses an Orleans grain boundary must be decorated with `[GenerateSerializer]` and each member needs a stable `[property: Id(N)]`:

```csharp
[GenerateSerializer]
public record JobProgressSnapshot(
    [property: Id(0)] int PercentComplete,
    [property: Id(1)] string? Message = null);
```

All grain interfaces carry `[Alias("...")]` and their methods carry `[Alias("...")]` for forward-compatible versioning. `GlobalUsings.cs` aliases `RequestContext` and `RequestResult` from `RequestProcessor` namespace to avoid clashes with Orleans internals.

---

## Running

```bash
# Full solution (recommended — Blazor dashboard waits for Orleans /alive)
dotnet run --project aspire/Demo.AppHost --launch-profile http
# Aspire dashboard: http://localhost:15004
# Orleans dashboard: http://localhost:8080

# Orleans silo standalone
dotnet run --project src/Demo.Orleans
# Orleans dashboard: http://localhost:8080
```

---

## Orleans Dashboard

`Demo.Orleans` ships with `Microsoft.Orleans.Dashboard` which provides a real-time web UI at **http://localhost:8080**.

| Tab | Content |
|---|---|
| Overview | Silo count, total activations, requests/sec |
| Grains | Per-grain-type activation count, call rate, exception rate |
| Silo | Per-silo CPU, memory, and network counters |
| Reminders | All scheduled reminders (in-memory service) |
| Logs | Live streaming grain log output |