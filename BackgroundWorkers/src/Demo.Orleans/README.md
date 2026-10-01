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

![ReportGrain lifecycle](../../assets/diagrams/report-grain-lifecycle.svg)

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

![NotificationGrain lifecycle](../../assets/diagrams/notification-grain-lifecycle.svg)

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

![JobGrain lifecycle](../../assets/diagrams/job-grain-lifecycle.svg)

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

## Durable state with `DurableGrain` (Orleans Journaling)

`Durable/DurableJobGrain` persists job state through `Microsoft.Orleans.Journaling` (experimental; `ORLEANSEXP005` is suppressed in the csproj). It shows how data produced by the request pool, which runs outside the grain scheduler, reaches durable state without exposing any grain method that could receive it.

```
client ─► DurableJobGrain.SubmitAsync ─► IRequestPool.EnqueueAsync
                                              │  (pool worker thread)
                                              ▼
              progress / partial results / final result
                                              │  TryWrite (in-process, ordered)
                                              ▼
   in-process mailbox (Channel) ─► pump on the grain's scheduler ─► DurableJobStore ─► journal
```

- **The mailbox.** Workers only write messages (`ProgressMessage`, `ResultMessage`) into a `Channel` owned by the grain activation. A pump, started in `OnActivateAsync`, drains it: its continuations resume on the grain's own scheduler, so state is touched only from grain turns, with no polling delay. In a spike, 40,000 messages written from 8 threads were all handled on the grain scheduler with no overlap while 1,200 concurrent calls competed for the grain. **There is no grain method that receives results, so nothing outside the silo process can forge one.** (The alternative, a public `IGrainExtension` that workers called through a grain reference, was forgeable by any client, and making its interface `internal` did not help: Orleans identifies interfaces by `[Alias]`, so a separate process with a lookalike interface completed a running job.)
- `DurableJobStore` is the only code that mutates and persists state from worker data. State is injected with `[FromKeyedServices("name")]` (the concrete durable types are internal). Reads and writes go through one gate, so a read never returns state whose journal write is still in flight and a deactivation cannot lose it.
- `AddDurableJobJournaling()` registers the in-memory `VolatileJournalStorageProvider` and the JSON journal format. State survives grain deactivation on the same silo, but **not** a silo restart, and it is **per silo** (the test cluster therefore runs one silo). Use a shared, durable provider (for example Azure Blob) beyond a demo.

### Jobs that outlive the grain's idle timeout (keep-alive)

Results can only be delivered to the activation that started the run, because they travel through its in-process mailbox. A job can run for much longer than Orleans lets a grain stay idle (15 minutes by default), so while any run is in flight the grain **delays its own deactivation**:

- `UpdateKeepAlive()` calls `DelayDeactivation(JobRecoveryOptions.KeepAliveSlice)` (default 5 minutes) when the first run starts and a grain timer renews it every third of the slice, so a job may run far longer than the slice. When the last run ends the timer is disposed and the delay is lifted (`DelayDeactivation(TimeSpan.Zero)`), so the grain is eligible for idle collection again.
- Measured with a real 3 s collection age and a 25 s recovery check period (so the checks cannot be what keeps it alive): the grain stayed active for 100 % of a 14 s job, both partial results were applied, nothing was resubmitted, and about 4 to 5 s after the job ended it was collected by normal idle collection. Without the keep-alive the same setup had it active only 27 % of the time.
- The price is memory: a grain with a running job is pinned for as long as the job runs. That is inherent in delivering results to an activation; if it is not acceptable, results have to reach the grain through something that can reactivate it, which is a callable surface again.
- `ForceActivationCollection` and idle collection both respect the delay (tests try to collect a grain with a job in flight for 3 s).

**When the activation is lost anyway** (silo shutdown, an administrator deactivating it): `OnDeactivateAsync` closes the mailbox (a worker that writes later finds it closed and its data is dropped and logged), applies what had already arrived, asks the pool to cancel the runs, and persists them as **released** (`OwnerSilo = null`); recovery then restarts them at once. Note that `IRequestPoolMonitor.TryCancelRequest` only cancels a request that is still queued: a run a worker is already executing cannot be cancelled that way, and it finishes with its data dropped. As a safety net, a reactivated grain also treats a `Processing` job that is owned by *this silo* but not run by *this activation* as lost, since results only reach the activation that started the run. The graceful-restart path is covered by a test with its own cluster and a journal carried across the restart.

### Recovering unfinished jobs

Pool work lives in memory. If the silo running a job dies, the journal still says `Processing` and nothing will ever finish it. Each record therefore stores the original `JobRequest`, the owning silo (`OwnerSilo`) and an `Attempts` counter, and the grain restarts jobs that nobody can deliver results for (`IsOrphaned`):

- the job has no owner (released by a deactivating activation), or
- it is owned by this silo but not run by this activation (a reactivated grain), or
- its owner is another silo that is gone (`Dead` or unknown to the membership view; joining, shutting-down and stopping silos still count as alive) **and** the job has had no activity for `JobRecoveryOptions.OrphanGracePeriod` (default 10 s). The grace period absorbs membership lag and a silo that is shutting down, so a job that is still running elsewhere is not resubmitted.

Recovery runs from a grain timer right after activation (not inline, to avoid calling into a grain that is still activating), on `RecoverUnfinishedJobsAsync()`, and from the scheduled check below. Orphans are claimed inside the write gate (`OwnerSilo` becomes the current silo, `Attempts` and `Epoch` increment, partial data is cleared) before being re-enqueued, so concurrent recoveries cannot resubmit a job twice. After `MaxRecoveryAttempts` (3) lost owners a job is marked `Failed` ("Abandoned…"). Recovery gives at-least-once execution: a job may run again from the start, so handlers should be idempotent. It only helps with **durable, shared** journal storage; with the volatile provider the journal dies with the silo, so the tests simulate owner loss with `FakeJobOwnerLiveness` and a shared journal.

- **Repeated, out-of-order and late data.** Every run of a job has an `Epoch` (a resubmit of the same id and each recovery get a new one), and every message carries its run's epoch plus a `sequence`. Epochs come from a journaled **per-grain counter** (`lastEpoch`), not from the job's own record, so an epoch is never handed out twice in a grain: when retention prunes a finished job and the id is submitted again, the new run does not start over at an epoch the first run already had (it did before, and a late duplicate of the first run's final result then completed the new run). A message is applied only if the epoch is the job's current one, the run has no final result yet, and the sequence is newer than the last one applied *in its own space* (`LastProgressSequence` for progress, `LastSequence` for results). Otherwise it is ignored without a journal write. A worker's messages arrive in order (the channel is FIFO, so the final result can never overtake its last partial result), but a message can still be delivered twice, or come from a run that was superseded by a recovery or a resubmit, which is exactly what epoch and sequence drop. `status = Processing` in a `ResultMessage` is a partial result (last write by sequence wins); a terminal status is final and counts toward `finished-count` exactly once per run. `ApplyResultAsync` throws for any other status.
- **Simulating several results per job.** Submit with `partialResults=N` (`POST /durable-jobs/{owner}/{jobId}?partialResults=5`, or `JobRequest.PartialResults`). The grain then dispatches a `StagedJobRequest`, handled by `StagedJobRequestHandler`: for each of the N stages it reports a progress delta carrying a `PartialResultDelta` (a partial *result*), which the grain posts to its mailbox, followed by the final result. Every second stage is posted **twice with the same sequence number** (simulated at-least-once delivery). `GET .../{jobId}` shows the latest partial in `output` while the job runs and `resultsReceived` (partial + final results applied to the current run: N + 1 when finished, never more despite the duplicates). Without `partialResults` the plain handler runs and `resultsReceived` ends at 1.
- **Request ids.** The pool identifies requests by id and rejects a second pending request with the same id, so each run is enqueued as `<owner>/<jobId>#<epoch>`. A job id is only unique within its grain, and a recovered run can be enqueued while the run it replaces is still pending.
- **Input validation and idempotency.** `SubmitAsync` rejects bad input *before* writing any state (`ArgumentException`; the HTTP endpoint answers `400`): an empty or over-128-character job id, control characters, an owner and job id that do not fit the pool's 256-character request id, a null request, or `PartialResults` outside 0..100 (each partial result is a journal write). A job id is an idempotency key only while the job is `Processing`; to make a client retry safe after it finished, pass `JobRequest.IdempotencyKey` (`?idempotencyKey=` over HTTP): submitting a job with the key it was last submitted with does nothing, while a different or missing key starts a new run.
- **Failure handling.** If the pool refuses a run (`EnqueueAsync` throws), the run is failed immediately (`Could not start: ...`, the client sees the exception, the idempotency key is released so a retry works) instead of staying `Processing` under a live owner, where recovery would never look at it. The same applies per job during recovery, so one refused job does not strand the ones claimed after it.
- **Run timeout.** Because a running job pins its grain, a hung handler would pin it forever, so every run has a limit, `JobRecoveryOptions.MaxRunDuration` (default 1 hour; `JobRecovery:MaxRunDuration`; null, zero or infinite disable it). It is enforced twice. (1) It is set as the request's `Timeout` in the pool, which cancels a handler that honours its cancellation token; the pool then reports the run as **`Cancelled`** (a 100-stage job under a 3 s limit ended `Cancelled` after 3.8 s with 16 results). (2) The pool's timeout is cooperative: a handler that ignores cancellation never returns, so the pool never calls back. The grain therefore also keeps a deadline per run (the limit plus an allowance of a quarter of it, between 250 ms and a minute) and fails the run itself: **`Failed`, "Timed out: the run did not finish within …"**. The keep-alive timer does the checking (it ticks at a third of the keep-alive slice, or a quarter of the limit if that is shorter). A failed run is finished like any other: whatever its worker sends later is ignored, the keep-alive is lifted if it was the last run in flight, the request is cancelled in the pool if it is still queued (a running handler cannot be stopped from outside), the idempotency key is released so a client retry runs again, and recovery does not restart it. Verified against the real pool with a handler that hangs: the job was `Failed` 3.7 s after submission (3 s limit), stayed that way, and ran exactly once.
- **Cancellation.** `CancelAsync(jobId)` (`POST /durable-jobs/{ownerId}/{jobId}/cancel`, answers `{ "cancelled": bool }`) cancels a job that is still `Processing`: it returns `false` for an unknown or finished job. The cancel is recorded first (the job becomes `Cancelled`, counted in `finished-count` once, with the idempotency key kept), then the run is untracked and, if its request is still queued in the pool, taken out of the queue. Because the job is terminal the moment the cancel is recorded, everything the run sends later is ignored (epoch fencing, same as a timed-out run) and recovery never restarts it, even if its owner was lost. As with the run timeout, a handler a worker is already executing **cannot be stopped from outside**: it keeps running and finishes with its data dropped. Submitting the same job id again starts a new run with a new epoch.
- **Retention.** A grain keeps at most `JobRecoveryOptions.MaxRetainedFinishedJobs` (default 1000) finished jobs; submitting a new run removes the oldest finished ones. Running jobs are never removed and `finished-count` is a separate counter.
- **Limits of this design.** The mailbox is unbounded (a worker never blocks on the grain); progress rate is bounded by the handlers and partial results by `MaxPartialResults`. Progress is not journaled on its own (it rides along with the next write), which is cosmetic. Results work only while the pool and the grain's activation are in the same process, which the keep-alive guarantees for the lifetime of a run. The mailbox drain on deactivation is not covered by a test: what it applies belongs to a run that is then released and recovered, so the effect is only visible for a final result that arrives in the last moments.
- **Scheduled recovery check (Orleans durable jobs).** Activation-time recovery only runs if something activates the grain, so an orphaned job in a grain nobody touches would stay `Processing`. While a grain has `Processing` jobs it keeps one `Microsoft.Orleans.DurableJobs` job scheduled (`recover-unfinished-jobs`, `JobRecoveryOptions.CheckPeriod` ahead, default 1 minute; `JobRecovery:CheckPeriod`). When it fires, Orleans reactivates the grain and calls `IDurableJobHandler.ExecuteJobAsync`, which runs recovery and schedules the next check if jobs are still processing. Durable jobs are one-shot, so the chain ends by itself once nothing is processing. A journaled `recoveryDueAt` prevents double scheduling, a check more than one period overdue is presumed lost and rescheduled, and a firing check clears the marker only if it is the check the marker was set for. Scheduling is best effort: a failure is logged and recovery falls back to activation only.
- Durable jobs fire at shard granularity (`DurableJobsOptions.ShardDuration`, one minute by default, configurable under `DurableJobs`), so a check can fire later than `CheckPeriod`. **Shard activation gotcha (alpha):** if a job's shard starts further away than `DurableJobsOptions.ShardActivationBufferPeriod`, it is only started by a periodic check whose interval this version does not expose, so the check can fire much later than `CheckPeriod`. With short settings this made the recovery tests time out about half the time; the tests set the buffer above `ShardDuration`. Treat the check as a safety net, not a precise timer. The schedule is only as durable as the provider: `UseInMemoryDurableJobs()` (used here) is lost with the silo, like the volatile journal.

HTTP: `POST /durable-jobs/{ownerId}/recover` (manual trigger, returns `{ "resubmitted": n }`), `POST /durable-jobs/{ownerId}/{jobId}/cancel`, `POST /durable-jobs/{ownerId}/{jobId}`, `GET /durable-jobs/{ownerId}/{jobId}`, `GET /durable-jobs/{ownerId}`, `GET /durable-jobs/{ownerId}/finished-count`.

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
