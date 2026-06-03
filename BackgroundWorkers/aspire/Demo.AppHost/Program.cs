var builder = DistributedApplication.CreateBuilder(args);

// ── Services ─────────────────────────────────────────────────────────────────

// Standalone worker that continuously submits requests through the pool.
builder.AddProject<Projects.RequestProcessor_Demo>("worker-demo");

// Orleans silo that accepts job submissions from grain clients.
var orleansSample = builder.AddProject<Projects.RequestProcessor_Orleans>("orleans-demo");

// Blazor Server UI for creating, listing, and cancelling jobs.
// WaitFor ensures the silo's /alive health check passes before the UI starts.
builder.AddProject<Projects.Blazor_Dashboard>("blazor-dashboard")
       .WaitFor(orleansSample);

// ── Run ───────────────────────────────────────────────────────────────────────
builder.Build().Run();
