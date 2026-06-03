using Microsoft.Extensions.Options;
using Orleans.Dashboard;
using OrleansSample;
using System.Diagnostics.Metrics;
using System.Threading.Tasks.Schedulers;

// ── Host setup ───────────────────────────────────────────────────────────────
// WebApplication is required so we can call app.MapOrleansDashboard() to serve
// the Orleans dashboard over HTTP.
var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OTel (traces + metrics), health checks, service discovery.
builder.AddServiceDefaults();
builder.AddRequestPoolHealthCheck();

builder.Services.AddSingleton<IJobTracker, InMemoryJobTracker>();

// ── Request pool scheduler ────────────────────────────────────────────────────
// One QueuedTaskScheduler owns dedicated background threads so that long-running
// handler work never competes with Orleans grain continuations for ThreadPool
// threads.  BelowNormal priority ensures Orleans work preempts pool work under
// CPU pressure.  Three sub-queues map RequestPriority tiers to QTS priorities
// (lower int = higher QTS priority).
builder.Services.AddOptions<QueuedTaskSchedulerOptions>()
    .BindConfiguration("RequestPoolScheduler");

// Read Enabled early so we can conditionally wire up the scheduler.
// When false, no dedicated threads are created; the pool uses the default ThreadPool.
// Set RequestPoolScheduler:Enabled = false in appsettings.json to benchmark the difference.
var schedulerEnabled = builder.Configuration.GetValue("RequestPoolScheduler:Enabled", defaultValue: true);

if (schedulerEnabled)
{
    // Register the root scheduler as a DI singleton so grains can resolve it if needed.
    // The DI container owns disposal and provides IMeterFactory for scheduler metrics.
    builder.Services.AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<QueuedTaskSchedulerOptions>>().Value;
        return new QueuedTaskScheduler(
            threadCount: opts.ThreadCount,
            threadName: "RequestPoolWorker",
            threadPriority: opts.ThreadPriority,
            meterFactory: sp.GetRequiredService<IMeterFactory>());
    });

    builder.Services.AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<QueuedTaskSchedulerOptions>>().Value;
        var workerScheduler = sp.GetRequiredService<QueuedTaskScheduler>();
        var schedulers = new RequestPoolTaskSchedulers(
            High: workerScheduler.ActivateNewQueue(priority: 0, quantum: opts.HighQuantum),
            Normal: workerScheduler.ActivateNewQueue(priority: 1, quantum: opts.NormalQuantum),
            Low: workerScheduler.ActivateNewQueue(priority: 2, quantum: opts.LowQuantum));
        workerScheduler.Freeze();
        return schedulers;
    });

    builder.Services.AddOptions<RequestPoolOptions>()
        .Configure<IOptions<QueuedTaskSchedulerOptions>>((poolOpts, schedulerOpts) =>
            poolOpts.MaxConcurrency = schedulerOpts.Value.ThreadCount)
        .Configure<RequestPoolTaskSchedulers>((options, schedulers) =>
        {
            options.TaskSchedulerFactory = priority => priority switch
            {
                RequestPriority.High => schedulers.High,
                RequestPriority.Normal => schedulers.Normal,
                RequestPriority.Low => schedulers.Low,
                _ => throw new ArgumentOutOfRangeException(nameof(priority)),
            };
        });
}

builder.Services
    .AddMediatorRequestPool(options =>
    {
        options.BoundedCapacity = 500;
        options.PartitionFairnessEnabled = true;
        options.PriorityAgingThreshold = TimeSpan.FromSeconds(30);
    })
    .AddRequestHandler<JobRequest, JobRequestHandler>()
    .AddRequestHandler<BatchJobRequest, BatchJobRequestHandler>()
    .AddRequestHandler<ScheduledJobRequest, ScheduledJobRequestHandler>()
    .AddRequestHandler<BatchWorkerItemRequest, BatchWorkerItemRequestHandler>()
    .AddRequestHandler<GenerateReportRequest, GenerateReportHandler>()
    .AddRequestHandler<ReviewReportRequest, ReviewReportHandler>()
    .AddRequestHandler<PublishReportRequest, PublishReportHandler>()
    .AddRequestHandler<EmailNotificationRequest, EmailNotificationHandler>()
    .AddRequestHandler<SmsNotificationRequest, SmsNotificationHandler>()
    .AddRequestHandler<PushNotificationRequest, PushNotificationHandler>()
    .AddRequestHandler<ExtractContentRequest, ExtractContentHandler>()
    .AddRequestHandler<TransformContentRequest, TransformContentHandler>()
    .AddRequestHandler<IndexContentRequest, IndexContentHandler>();

builder.Services.AddSingleton<IRequestPoolGrainServiceClient, RequestPoolGrainServiceClient>();

builder.Services.AddOrleans(silo =>
{
    silo.UseLocalhostClustering();

    // In-memory reminder service is required by the Orleans dashboard
    // to display the Reminders tab.
    silo.UseInMemoryReminderService();

    // Register dashboard instrumentation grains.  The web endpoint is
    // wired up below with app.MapOrleansDashboard().
    silo.AddDashboard();

    // Start the per-silo grain service that exposes IRequestPoolMonitor
    // to grains via IRequestPoolGrainServiceClient.
    silo.AddGrainService<RequestPoolGrainService>();
});

// Submits demo jobs once the silo is ready; does not block the web server.
//builder.Services.AddHostedService<JobBootstrapService>();

// ── Middleware pipeline ───────────────────────────────────────────────────────
var app = builder.Build();

// Serve the Orleans dashboard at the application root (http://localhost:8080/).
app.MapOrleansDashboard();

await app.RunAsync();

internal sealed record RequestPoolTaskSchedulers(TaskScheduler High, TaskScheduler Normal, TaskScheduler Low);
