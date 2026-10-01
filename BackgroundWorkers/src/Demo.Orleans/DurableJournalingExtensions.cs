using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace OrleansSample;

internal static class DurableJournalingExtensions
{
    /// <summary>
    /// Configures Orleans Journaling with in-memory (volatile) journal storage and the JSON journal format,
    /// plus in-memory Orleans durable jobs for the scheduled recovery check.
    /// Swap the volatile provider for e.g. <c>AddAzureBlobJournalStorage()</c> to survive silo restarts.
    /// </summary>
    public static ISiloBuilder AddDurableJobJournaling(this ISiloBuilder silo)
    {
        // AddJournalStorage() registers only the core services; a storage provider must be added explicitly.
        silo.ConfigureServices(services =>
        {
            services.AddSingleton<VolatileJournalStorageProvider>();
            services.AddSingleton<IJournalStorageProvider>(sp => sp.GetRequiredService<VolatileJournalStorageProvider>());
        });

        // TryAdd so tests can substitute their own liveness.
        silo.ConfigureServices(services =>
            services.TryAddSingleton<IJobOwnerLiveness, SiloJobOwnerLiveness>());

        // Scheduled recovery checks run as Orleans durable jobs (in-memory store: lost with the silo, like the
        // volatile journal; use a durable jobs provider together with durable journal storage in production).
        silo.UseInMemoryDurableJobs();

        return silo.AddJournalStorage()
            .UseJsonJournalFormat(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
    }
}
