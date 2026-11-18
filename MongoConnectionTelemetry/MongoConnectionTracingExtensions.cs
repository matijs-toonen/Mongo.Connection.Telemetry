using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver.Core.Configuration;

namespace MongoConnectionTelemetry;

/// <summary>
/// Startup extensions for the MongoDb connection tracing and metrics.
/// </summary>
public static class MongoConnectionTracingExtensions
{
    /// <summary>
    /// Binds the settings and registers the activity source and meter with OpenTelemetry, without them the spans and
    /// metrics are not exported.
    /// </summary>
    /// <param name="services">The service collection to add telemetry to.</param>
    /// <param name="configuration">The configuration to bind options from.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMongoConnectionTracing(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<MongoConnectionTracingOptions>()
            .Bind(configuration.GetSection(MongoConnectionTracingOptions.SectionName));

        services
            .AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(MongoConnectionEventSubscriber.ActivitySourceName))
            .WithMetrics(metrics => metrics.AddMeter(MongoConnectionEventSubscriber.MeterName));

        return services;
    }

    /// <summary>
    /// Subscribes the connection tracing on a MongoDb client that is being built.
    /// </summary>
    /// <param name="clusterBuilder">The cluster builder to configure.</param>
    /// <param name="serviceProvider">The service provider to resolve options and logger factory.</param>
    /// <param name="name">Name identifying the client belongs to.</param>
    public static void SubscribeConnectionTracing(this ClusterBuilder clusterBuilder, IServiceProvider serviceProvider, string name)
    {
        var subscriber = CreateSubscriber(
            serviceProvider.GetRequiredService<IOptions<MongoConnectionTracingOptions>>().Value,
            serviceProvider.GetRequiredService<ILoggerFactory>(),
            name);

        if (subscriber is not null)
        {
            clusterBuilder.Subscribe(subscriber);
        }
    }

    /// <summary>
    /// Creates the subscriber, or <see langword="null"/> when the tracing and metrics are disabled.
    /// </summary>
    /// <param name="options">Settings for the tracing and metrics.</param>
    /// <param name="loggerFactory">Logger factory used to create subscriber loggers.</param>
    /// <param name="name">Name identifying the client belongs to.</param>
    /// <returns>A new <see cref="MongoConnectionEventSubscriber"/> instance, or null if disabled.</returns>
    public static MongoConnectionEventSubscriber? CreateSubscriber(MongoConnectionTracingOptions options, ILoggerFactory loggerFactory, string name) =>
        options.Enabled
            ? new MongoConnectionEventSubscriber(name, options, loggerFactory.CreateLogger<MongoConnectionEventSubscriber>())
            : null;
}

