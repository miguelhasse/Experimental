# Blazor.Dashboard

A **Blazor Server** application that provides a real-time UI for submitting and monitoring background jobs processed by the [`Demo.Orleans`](../Demo.Orleans/README.md) silo through the [`RequestProcessor`](../Core/README.md) request pool.

---

## Prerequisites

- .NET 10
- The **Demo.Orleans** silo must be running and reachable on the default localhost gateway port (`30000`) before the dashboard starts — otherwise the Orleans cluster client will fail to connect.

---

## Running

```bash
# Recommended: full solution via Aspire (handles startup ordering automatically)
dotnet run --project aspire/Demo.AppHost --launch-profile http
# Aspire dashboard: http://localhost:15004

# Standalone (start Demo.Orleans first)
dotnet run --project src/Demo.Orleans   # wait for it to be ready
dotnet run --project src/Blazor.Dashboard
```

When running under Aspire, the `Demo.AppHost` configuration includes `.WaitFor(orleansSample)`, which ensures the Blazor dashboard does not start until the Orleans silo's `/alive` health endpoint responds.

---

## Project layout

```
Blazor.Dashboard/
├── Components/
│   ├── App.razor            ← Blazor application root
│   ├── Layout/              ← MainLayout (navbar with Jobs + Notifications + Document Processing + Batch Processing + Reports + Durable Jobs links)
│   └── Pages/
│       ├── Jobs.razor              ← Jobs dashboard page (/)
│       ├── Notifications.razor     ← Notifications dashboard page (/notifications)
│       ├── DocumentProcessing.razor ← Document Processing pipeline page (/document-processing)
│       ├── BatchProcessing.razor   ← Batch Processing page (/batch-processing)
│       ├── Reports.razor           ← Reports page (/reports)
│       └── DurableJobs.razor       ← Durable jobs page (/durable-jobs)
├── Services/
│   ├── JobService.cs               ← Singleton: job state + Orleans client bridge
│   ├── NotificationService.cs      ← Singleton: notification grain state + Orleans client bridge
│   ├── DocumentProcessingService.cs ← Singleton: pipeline state + Orleans client bridge
│   ├── BatchService.cs             ← Singleton: batch state + Orleans client bridge
│   ├── ReportService.cs            ← Singleton: report state + Orleans client bridge
│   └── DurableJobService.cs        ← Singleton: stateless IDurableJobGrain client bridge
├── Program.cs               ← Host setup: Razor components, Orleans client, service defaults
├── GlobalUsings.cs          ← Project-wide using aliases
└── appsettings.json
```

---

## Dashboard features

### Jobs page (`/`)

The page has a three-tab card at the top and a live job table below.

#### Submit New Job tab

Submits a single job to `IJobGrain`. A **Job type** selector at the top of the form switches between three layouts:

| Job type | Fields | Orleans type |
|---|---|---|
| **Job** (default) | Payload (text) + Category + Priority | `JobRequest` |
| **Batch** | Items (textarea, one per line) + Category + Priority | `BatchJobRequest` |
| **Scheduled** | Payload + Scheduled At (datetime-local, default now+5 min) + Category + Priority | `ScheduledJobRequest` |

#### Generate Batch tab

Bulk-submits N jobs. A **Job type** selector controls which job type is generated:

| Field | Description |
|---|---|
| Count | Number of jobs to create (1–100) |
| Job type | Job / Batch / Scheduled |
| Category | Applied to all jobs |
| Priority | `High`, `Normal`, `Low`, or `Random` |

- **Job**: generates N `JobRequest`s with random payloads
- **Batch**: generates N `BatchJobRequest`s each with 3 random items
- **Scheduled**: generates N `ScheduledJobRequest`s with random payloads and `ScheduledAt = now + rand(1–60) min`

#### Pool Stats tab

Displays a live `RequestPoolStatsSnapshot` fetched from `IPoolStatsGrain "default"`, which aggregates stats across **all active silos**:

| Card | Metric |
|---|---|
| Enqueued | `TotalEnqueued` — cumulative accepted requests |
| Completed | `TotalCompleted` — cumulative successes |
| Failed | `TotalFailed` — cumulative failures (exception or `Success: false`) |
| Cancelled | `TotalCancelled` — pre-dispatch cancellations |
| Active Workers | `ActiveWorkers` — currently dispatching |
| Max Concurrency | `MaxConcurrency` — configured worker count |
| Queue: High / Normal / Low | Per-priority channel depth |
| Bounded Capacity | `BoundedCapacity` — per-channel limit |
| Silos | `SiloCount` — active silos contributing to the aggregation |

#### Job table

Each row shows a submitted job with:

- **ID** — truncated GUID (`ui-{guid}`)
- **Payload** — the text submitted; small muted progress message below when processing
- **Type** — badge indicating the job type (Job / Batch / Scheduled)
- **Category** — optional tag
- **Priority** — badge (High / Normal / Low)
- **Status** — colour-coded badge: Pending (secondary), Processing (primary), Completed (success), Failed (danger), Cancelled (warning), Unknown (light)
- **Progress** — 4 px animated progress bar below the status badge; visible only while `Processing`
- **Actions** — Cancel button (active while `Pending`); Remove button (active when terminal)

---

### Notifications page (`/notifications`)

Demonstrates creating `INotificationGrain` instances and invoking each of the three idempotent channel methods independently per row.

#### Create form

Enter a **Notification ID** (any non-empty string) and click **Create**. Creating with an existing ID is a no-op (idempotent). The grain itself is activated lazily on first method call — no Orleans state is created by the form alone.

#### Notification table

Each row represents one notification grain instance with columns:

| Column | Description |
|---|---|
| **ID** | The grain key / notification identifier |
| **Created** | Local timestamp when the row was added |
| **Email** | Status badge + **Send** button (shown when status allows re-dispatch) |
| **SMS** | Status badge + **Send** button |
| **Push** | Status badge + **Send** button |
| **Actions** | **Send All** dispatches all dispatchable channels in parallel; **Remove** removes the row (enabled only when no channels are Pending/Processing) |

Status badge colours follow the same palette as the Jobs page (Pending → secondary, Processing → primary, Completed → success, Failed → danger, Cancelled → warning, Unknown → light).

**Send** buttons are shown only when the channel status is `Unknown`, `Failed`, or `Cancelled` (retryable states). Clicking **Send** invokes the corresponding grain method (`SendEmailAsync`, `SendSmsAsync`, or `SendPushAsync`). The grain's idempotency guard means concurrent clicks are safe — the grain returns the current status immediately if a request is already in flight or has already completed successfully. **Completed** channels never show a Send button and the grain rejects any re-dispatch attempt for them.

Channel request content is auto-generated from the notification ID:
- Email: `recipient = "user@example.com"`, `subject = "Notification: {id}"`
- SMS: `phoneNumber = "+15550001234"`, `body = "Notification {id} received"`
- Push: `deviceToken = "device-{id}"` (first 8 chars), `title = "Notification: {id}"`

The auto-refresh toggle and **↺ Refresh** button on this page behave the same as on the Jobs page (500ms timer, calls `NotificationService.RefreshAllAsync()`).

---

### Document Processing page (`/document-processing`)

Demonstrates running `IDocumentProcessingGrain` pipeline instances: **Extract → Transform → Index** — where each step's typed output feeds the next step's input.

#### Run Pipelines tab

| Field | Description |
|---|---|
| Count | Number of pipelines to start (1–20, default 3) |

Clicking **Run** generates N auto-generated IDs (`pipe-XXXX`), creates and starts each `IDocumentProcessingGrain` in parallel.

#### Pool Stats tab

Identical layout to the Jobs page Pool Stats tab — shows the same shared `RequestPoolStatsSnapshot`.

#### Pipeline table

Each row represents one pipeline with columns:

| Column | Description |
|---|---|
| **Pipeline ID** | Grain key (`pipe-XXXX`) |
| **Started** | Local timestamp |
| **Extract** | Step 1 status badge + progress bar + word count on completion |
| **Transform** | Step 2 status badge + progress bar + keyword count and sentiment on completion |
| **Index** | Step 3 status badge + progress bar + index ID and tag count on completion |
| **Actions** | **Restart** (when all steps are non-running and at least one has failed/cancelled/completed); **Remove** (when no steps are pending/processing) |

Progress bars show the animated striped style while a step is `Processing`, and a solid colour (green/red/yellow) when the step has completed in any terminal state.

---

### Batch Processing page (`/batch-processing`)

Demonstrates `IBatchCoordinatorGrain`: a coordinator that fans a batch of items out across multiple worker grains and reports an aggregated `BatchCoordinationSummary`.

#### Run Batches tab

| Field | Description |
|---|---|
| Item Count | Items per batch (1–1000, default 100) |
| Worker Count | Workers sharing the batch (1–20, default 5) |

Clicking **Run** generates a `batch-XXXX` ID and calls `ProcessBatchAsync(itemCount, workerCount)` fire-and-forget, so the ID appears in the table immediately and status arrives through polling.

#### Pool Stats tab

Identical layout to the Jobs page Pool Stats tab — shows the same shared `RequestPoolStatsSnapshot`.

#### Batches table

| Column | Description |
|---|---|
| **Batch ID** | Grain key (`batch-XXXX`) |
| **Started** | Local timestamp |
| **Items** / **Workers** | The configuration the batch was started with |
| **Status** | Overall status badge (`OverallStatus` from the summary) |
| **Progress** | Bar of (completed + failed + cancelled) / items, with `completed/items` text and ✕ (failed) / ⊘ (cancelled) counts |
| **Actions** | **Restart** (Completed/Failed/Cancelled; starts a new batch with the same configuration); **Remove** (when not Pending/Processing) |

Header actions: **Remove All** clears every non-active entry; **Cancel All Pending** (table header) calls `CancelAsync()` on every Pending/Processing batch. Only work still queued in the pool is cancelled — already-dispatched items are skipped.

---

### Reports page (`/reports`)

Demonstrates `IReportGrain`, which runs three independent operations per report: **Generate → Review → Publish**. Each operation is idempotent at the grain level and tracked under its own key.

#### Run Reports tab

| Field | Description |
|---|---|
| Count | Number of reports to start (1–20, default 3) |

Clicking **Run** generates N `report-XXXX` IDs and dispatches all three operations for each report.

#### Pool Stats tab

Identical layout to the Jobs page Pool Stats tab — shows the same shared `RequestPoolStatsSnapshot`.

#### Reports table

| Column | Description |
|---|---|
| **Report ID** | Grain key (`report-XXXX`) |
| **Started** | Local timestamp |
| **Generate** / **Review** / **Publish** | Per-operation status badge; while `Processing`, a striped progress bar with the progress message, and a solid green/red/yellow bar once terminal |
| **Actions** | **Restart** (when no Generate is active and any operation Failed/Cancelled; re-dispatches all three); **Remove** (when no operation is Pending/Processing) |

Header actions: **Remove All** clears every report with no active operation; **Cancel All Pending** calls `CancelAsync()` on every report with a Pending/Processing operation.

---

### Durable Jobs page (`/durable-jobs`)

Demonstrates `IDurableJobGrain`: a string-keyed **owner** grain that journals many jobs durably and can recover unfinished ones if the silo running them dies.

#### Header

| Control | Description |
|---|---|
| **Owner** | Grain key (default `demo-owner`); changing it loads that owner's jobs |
| **Auto-refresh** / **↺ Refresh** | Same 500 ms timer behaviour as the other pages |
| **♻ Recover unfinished** | Calls `RecoverUnfinishedJobsAsync()` and reports how many jobs were resubmitted |
| **↻ Retry failed** | Resubmits every `Failed` job with its original request (enabled only when there is one) |

#### Submit Job tab

| Field | Description |
|---|---|
| Payload | Required text |
| Category | Optional |
| Priority | `Low`, `Normal`, `High` |
| Partial results | 0–100 partial results the job emits before its final result |

The job id is generated on submit (`job-XXXXXXXX`). Errors thrown by the grain are shown in an alert under the form.

#### Pool Stats tab

Identical layout to the Jobs page Pool Stats tab — shows the same shared `RequestPoolStatsSnapshot`.

#### Jobs table

Unlike the other pages, nothing is tracked locally: each refresh reads `GetJobsAsync()` and `GetFinishedCountAsync()`, so the table always reflects the grain's durable state. Columns: job id, status badge, progress bar, message, attempts, epoch, results received, owner silo and last update. Processing jobs show a **Cancel** button and failed jobs a **Retry** button in an Actions column. Click a row to expand the request, output and error.

---

## `JobService` — singleton, shared across all circuits

`JobService` is registered as a **singleton**, meaning the in-memory `_jobs` list is shared across every browser tab and SSR prerender within the same server process:

```csharp
builder.Services.AddSingleton<JobService>();
```

This is intentional — job state persists across page refreshes and is visible from any open tab. All mutations to `_jobs` are protected by a `Lock`.

### Key methods

| Method | Description |
|---|---|
| `SubmitJobRequestAsync(payload, category, priority, ct)` | Creates a `JobRequest`, calls `IJobGrain.SubmitAsync`, tracks locally |
| `SubmitBatchJobRequestAsync(items, category, priority, ct)` | Creates a `BatchJobRequest` from a list of item strings, tracks locally |
| `SubmitScheduledJobRequestAsync(payload, scheduledAt, category, priority, ct)` | Creates a `ScheduledJobRequest`, tracks locally |
| `RefreshAllAsync(ct)` | Fans out `GetStatusAsync()` + `GetProgressAsync()` (for Processing jobs) to all tracked grains in parallel |
| `CancelAsync(jobId)` | Calls `IJobGrain.TryCancelJobAsync()`; updates local status on success |
| `Remove(jobId)` | Removes a terminal job from the local list |
| `GetPoolStatsAsync(ct)` | Calls `IPoolStatsGrain("default").GetSnapshotAsync()`; returns `null` on failure |
| `Jobs` | Read-only snapshot of the list, most-recent first |

---

## `NotificationService` — singleton, shared across all circuits

`NotificationService` is also registered as a **singleton**, with the same shared-state semantics as `JobService`.

### Key methods

| Method | Description |
|---|---|
| `CreateAsync(notificationId)` | Adds a new entry (idempotent: no-op if ID already tracked) |
| `SendEmailAsync(id)` | Calls `INotificationGrain.SendEmailAsync`; updates local `EmailStatus` |
| `SendSmsAsync(id)` | Calls `INotificationGrain.SendSmsAsync`; updates local `SmsStatus` |
| `SendPushAsync(id)` | Calls `INotificationGrain.SendPushAsync`; updates local `PushStatus` |
| `SendAllAsync(id)` | Calls all three send methods in parallel (only dispatchable channels) |
| `RefreshAllAsync(ct)` | Calls `INotificationGrain.GetAllStatusAsync()` for all tracked entries in parallel |
| `Remove(id)` | Removes entry from the local list |
| `Notifications` | Read-only snapshot of all tracked entries, most-recent first |

---

## `DocumentProcessingService` — singleton, shared across all circuits

`DocumentProcessingService` is registered as a **singleton** with the same shared-state semantics as the other services.

### Key methods

| Method | Description |
|---|---|
| `GenerateId()` | Returns a unique `pipe-XXXX` ID (incrementing counter) |
| `CreateBatchAsync(count)` | Generates `count` IDs, creates `PipelineEntry` records, then calls `RunAsync` for each |
| `RunAsync(pipelineId)` | Calls `IDocumentProcessingGrain.RunAsync()`; sets Step1Status to Processing |
| `RefreshAllAsync(ct)` | Calls `IDocumentProcessingGrain.GetSummaryAsync()` for all tracked pipelines; populates per-step status, progress, and typed outputs |
| `Remove(pipelineId)` | Removes entry from the local list |
| `IsRestartable(entry)` | Returns `true` when no step is `Pending`/`Processing` and at least one is non-`Unknown` |
| `Pipelines` | Read-only snapshot, most-recent first |

The `PipelineEntry` record tracks status and progress for all three steps, plus the typed outputs (`ExtractedContentOutput?`, `TransformedContentOutput?`, `IndexedContentOutput?`) populated after `RefreshAllAsync`.

---

## `BatchService` — singleton, shared across all circuits

`BatchService` is registered as a **singleton** with the same shared-state semantics as the other services.

### Key methods

| Method | Description |
|---|---|
| `GenerateId()` | Returns a unique `batch-XXXX` ID (4 random lower-alphanumeric characters) |
| `CreateBatchAsync(itemCount, workerCount)` | Registers a `BatchEntry`, then calls `IBatchCoordinatorGrain.ProcessBatchAsync` fire-and-forget; returns the ID immediately |
| `RefreshAllAsync(ct)` | Calls `IBatchCoordinatorGrain.GetSummaryAsync()` for all tracked batches in parallel; updates `Status` and `Summary` |
| `CancelAllPendingAsync()` | Calls `CancelAsync()` on every Pending/Processing batch, then refreshes |
| `Remove(id)` / `RemoveAll()` | Removes one entry / all non-active entries |
| `IsRestartable(entry)` | `true` when status is Completed, Failed or Cancelled |
| `Batches` | Read-only snapshot, most-recent first |

---

## `ReportService` — singleton, shared across all circuits

`ReportService` is registered as a **singleton** with the same shared-state semantics as the other services.

### Key methods

| Method | Description |
|---|---|
| `GenerateId()` | Returns a unique `report-XXXX` ID |
| `CreateBatchAsync(count)` | Generates `count` IDs and calls `RunAllAsync` for each in parallel |
| `RunAllAsync(reportId)` | Tracks the report if new, then calls `GenerateAsync`, `ReviewAsync` and `PublishAsync` on `IReportGrain` and records the returned statuses |
| `RefreshAllAsync(ct)` | Calls `IReportGrain.GetSummaryAsync()` for all tracked reports in parallel; updates per-operation status and progress |
| `CancelAllPendingAsync()` | Calls `CancelAsync()` on every report with a Pending/Processing operation, then refreshes |
| `Remove(id)` / `RemoveAll()` | Removes one entry / all entries with no active operation |
| `IsRestartable(entry)` | `true` when Generate is not active and any operation Failed or was Cancelled |
| `Reports` | Read-only snapshot, most-recent first |

---

## `DurableJobService` — singleton, stateless

Registered as a singleton but holds no state; every call resolves `IDurableJobGrain` for the given owner id.

| Method | Description |
|---|---|
| `SubmitAsync(ownerId, jobId, request)` | Calls `IDurableJobGrain.SubmitAsync` |
| `GetJobsAsync(ownerId)` | Returns all `DurableJobRecord`s for the owner |
| `GetFinishedCountAsync(ownerId)` | Returns the number of terminal jobs |
| `CancelAsync(ownerId, jobId)` | Cancels a `Processing` job; returns `false` if it is unknown or already finished. A handler that is already running is not interrupted, its data is dropped |
| `RetryAsync(ownerId, jobId)` | Resubmits a `Failed` job with its stored request (a new run, new epoch); returns `false` if the job is not retryable |
| `RetryAllFailedAsync(ownerId)` | Retries every failed job of the owner; returns how many were resubmitted |
| `RecoverAsync(ownerId)` | Calls `RecoverUnfinishedJobsAsync`; returns the count recovered |

---

The Jobs page starts a 3-second `PeriodicTimer` on component initialisation:

- Calls `JobService.RefreshAllAsync()` to update statuses and progress
- Calls `InvokeAsync(StateHasChanged)` to re-render on the Blazor circuit thread
- The timer is disposed when the component is disposed (page navigation or circuit close)
- An **Auto-refresh** toggle switch at the top of the page pauses/resumes the timer
- A **↺ Refresh** button triggers an immediate refresh at any time

---

## Orleans cluster client

`Program.cs` connects to the Orleans silo using `UseLocalhostClustering`, which targets the default gateway port `30000`:

```csharp
builder.Host.UseOrleansClient(client =>
{
    client.UseLocalhostClustering(); // gateway port 30000
});
```

The client is available throughout the application via DI as `IClusterClient`. `JobService` receives it through its primary constructor.

---

## Extension points

### Adding a new job type

1. Define the request record in `Demo.Orleans` (implement `IJobRequest`, add `[GenerateSerializer]`)
2. Register a handler in `Demo.Orleans/Program.cs` with `AddRequestHandler<TNewRequest, TNewHandler>()`
3. Add a new form or tab to `Jobs.razor` to collect the new fields
4. Call `IJobGrain.SubmitAsync(new TNewRequest(...))` from `JobService` (or add a new method)
5. Optionally add the new type to `JobService.SubmitAsync`'s switch/if chain

### Using a non-localhost cluster

Replace `UseLocalhostClustering()` with the appropriate clustering provider (e.g., `UseAzureStorageClustering`, `UseAdoNetClustering`) and bind the connection string from configuration. The rest of the Blazor code is cluster-topology agnostic.
