using BlazorDashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// JobService is a singleton so job state is shared across SSR prerender and interactive
// circuit lifecycles, and persists across page refreshes within the same server process.
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<DocumentProcessingService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<BatchService>();

// Connect to the Orleans silo running in the same Aspire application.
// UseLocalhostClustering matches the silo's UseLocalhostClustering() config (gateway port 30000).
builder.Host.UseOrleansClient(client =>
{
    client.UseLocalhostClustering();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<BlazorDashboard.Components.App>()
   .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

await app.RunAsync();
