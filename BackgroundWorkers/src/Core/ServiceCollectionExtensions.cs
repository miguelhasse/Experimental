using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RequestProcessor;
using RequestProcessor.HealthChecks;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130 // Namespace does not match folder structure

/// <summary>
/// Extension methods for setting up request pool services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TDispatcher"/> as the <see cref="IRequestDispatcher"/>,
    /// adds <see cref="RequestPoolService"/> as both a singleton <see cref="IRequestPool"/>
    /// and a hosted background service, and optionally configures pool options.
    /// </summary>
    public static IServiceCollection AddRequestPool<TDispatcher>(
        this IServiceCollection services,
        Action<RequestPoolOptions>? configureOptions = null)
        where TDispatcher : class, IRequestDispatcher
    {
        if (configureOptions is not null)
            services.Configure(configureOptions);

        services.AddRequestPoolCore();

        // Register the provided dispatcher implementation.
        services.AddSingleton<IRequestDispatcher, TDispatcher>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="MediatorRequestDispatcher"/> as the <see cref="IRequestDispatcher"/>
    /// and sets up the request pool. Use <see cref="AddRequestHandler{TRequest,THandler}"/>
    /// to register typed handlers.
    /// </summary>
    public static IServiceCollection AddMediatorRequestPool(
        this IServiceCollection services,
        Action<RequestPoolOptions>? configureOptions = null)
    {
        if (configureOptions is not null)
            services.Configure(configureOptions);

        services.AddRequestPoolCore();
        services.GetOrAddRequestHandlerRegistry();

        // Register the mediator dispatcher.
        services.AddSingleton<IRequestDispatcher, MediatorRequestDispatcher>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="RequestPoolHealthCheck"/> with the health check builder.
    /// </summary>
    public static IHealthChecksBuilder AddRequestPool(
        this IHealthChecksBuilder builder,
        string name = "request_pool",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Action<RequestPoolHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
            builder.Services.Configure(configure);

        return builder.AddCheck<RequestPoolHealthCheck>(
            name,
            failureStatus ?? HealthStatus.Unhealthy,
            tags ?? []);
    }

    /// <summary>
    /// Registers a typed request handler for use with <see cref="MediatorRequestDispatcher"/>.
    /// </summary>
    public static IServiceCollection AddRequestHandler<TRequest, THandler>(
        this IServiceCollection services)
        where TRequest : notnull
        where THandler : class, IRequestHandler<TRequest>
    {
        services.GetOrAddRequestHandlerRegistry().Add(typeof(TRequest));
        services.AddSingleton<IRequestHandler<TRequest>, THandler>();
        services.AddKeyedSingleton<IRequestHandlerWrapper>(
            typeof(TRequest),
            (sp, _) => new RequestHandlerWrapper<TRequest>(sp.GetRequiredService<IRequestHandler<TRequest>>()));
        return services;
    }

    private static RequestHandlerRegistry GetOrAddRequestHandlerRegistry(this IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(RequestHandlerRegistry)
                && descriptor.ImplementationInstance is RequestHandlerRegistry registry)
                return registry;
        }

        var newRegistry = new RequestHandlerRegistry();
        services.AddSingleton(newRegistry);
        return newRegistry;
    }

    // Shared registration: metrics, validator, pool service (all three interfaces).
    private static void AddRequestPoolCore(this IServiceCollection services)
    {
        // IMeterFactory requires metrics services to be registered.
        services.AddMetrics();

        // Validate options at host startup.
        services.AddSingleton<IValidateOptions<RequestPoolOptions>, RequestPoolOptionsValidator>();
        services.AddOptions<RequestPoolOptions>().ValidateOnStart();

        // Register once as singleton, expose via all three interfaces.
        services.AddSingleton<RequestPoolService>();
        services.AddSingleton<IRequestPool>(sp => sp.GetRequiredService<RequestPoolService>());
        services.AddSingleton<IRequestPoolMonitor>(sp => sp.GetRequiredService<RequestPoolService>());
        services.AddHostedService(sp => sp.GetRequiredService<RequestPoolService>());
    }
}
