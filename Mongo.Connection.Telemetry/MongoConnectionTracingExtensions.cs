using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver.Core.Configuration;

namespace Mongo.Connection.Telemetry;

/// <summary>
/// Startup extensions for the MongoDb connection tracing and metrics.
/// </summary>
public static class MongoConnectionTracingExtensions
{
    /// <summary>
    /// Registers MongoDb connection tracing and metrics with OpenTelemetry using default options.
    /// </summary>
    /// <param name="services">The service collection to add telemetry to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMongoConnectionTracing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<MongoConnectionTracingOptions>();

        RegisterOpenTelemetry(services);

        return services;
    }

    /// <summary>
    /// Binds the settings and registers the activity source and meter with OpenTelemetry, without them the spans and metrics are not exported.
    /// </summary>
    /// <param name="services">The service collection to add telemetry to.</param>
    /// <param name="configuration">The configuration to bind options from.</param>
    /// <param name="sectionName">The name of the configuration section holding these settings. Defaults to <see cref="MongoConnectionTracingOptions.DefaultSectionName"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMongoConnectionTracing(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = MongoConnectionTracingOptions.DefaultSectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        services
            .AddOptions<MongoConnectionTracingOptions>()
            .Bind(configuration.GetSection(sectionName));

        RegisterOpenTelemetry(services);

        return services;
    }

    /// <summary>
    /// Configures settings via an action and registers the activity source and meter with OpenTelemetry.
    /// </summary>
    /// <param name="services">The service collection to add telemetry to.</param>
    /// <param name="configureOptions">The action used to configure options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMongoConnectionTracing(
        this IServiceCollection services,
        Action<MongoConnectionTracingOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.Configure(configureOptions);

        RegisterOpenTelemetry(services);

        return services;
    }

    /// <summary>
    /// Binds settings from configuration using the default section name, allows further customization via an action, and registers OpenTelemetry telemetry.
    /// </summary>
    /// <param name="services">The service collection to add telemetry to.</param>
    /// <param name="configuration">The configuration to bind options from.</param>
    /// <param name="configureOptions">The action used to further configure options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMongoConnectionTracing(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<MongoConnectionTracingOptions> configureOptions) =>
        AddMongoConnectionTracing(services, configuration, MongoConnectionTracingOptions.DefaultSectionName, configureOptions);

    /// <summary>
    /// Binds settings from configuration, allows further customization via an action, and registers OpenTelemetry telemetry.
    /// </summary>
    /// <param name="services">The service collection to add telemetry to.</param>
    /// <param name="configuration">The configuration to bind options from.</param>
    /// <param name="sectionName">The name of the configuration section holding these settings.</param>
    /// <param name="configureOptions">The action used to further configure options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMongoConnectionTracing(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName,
        Action<MongoConnectionTracingOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services
            .AddOptions<MongoConnectionTracingOptions>()
            .Bind(configuration.GetSection(sectionName))
            .Configure(configureOptions);

        RegisterOpenTelemetry(services);

        return services;
    }

    /// <summary>
    /// Subscribes the connection tracing on a MongoDb client that is being built.
    /// </summary>
    /// <param name="clusterBuilder">The cluster builder to configure.</param>
    /// <param name="serviceProvider">The service provider to resolve options and logger factory.</param>
    /// <param name="name">Name identifying the client belongs to.</param>
    /// <returns>The cluster builder for chaining.</returns>
    public static ClusterBuilder SubscribeConnectionTracing(
        this ClusterBuilder clusterBuilder,
        IServiceProvider serviceProvider,
        string name)
    {
        ArgumentNullException.ThrowIfNull(clusterBuilder);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var options = serviceProvider.GetService<IOptions<MongoConnectionTracingOptions>>()?.Value
                      ?? new MongoConnectionTracingOptions();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>()
                            ?? NullLoggerFactory.Instance;

        var subscriber = CreateSubscriber(options, loggerFactory, name);

        if (subscriber is not null)
        {
            clusterBuilder.Subscribe(subscriber);
        }

        return clusterBuilder;
    }

    /// <summary>
    /// Subscribes the connection tracing on a MongoDb client that is being built using explicit options and logger factory.
    /// </summary>
    /// <param name="clusterBuilder">The cluster builder to configure.</param>
    /// <param name="name">Name identifying the client belongs to.</param>
    /// <param name="options">Optional settings for tracing and metrics.</param>
    /// <param name="loggerFactory">Optional logger factory used to create subscriber loggers.</param>
    /// <returns>The cluster builder for chaining.</returns>
    public static ClusterBuilder SubscribeConnectionTracing(
        this ClusterBuilder clusterBuilder,
        string name,
        MongoConnectionTracingOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(clusterBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var subscriber = CreateSubscriber(name, options, loggerFactory);

        if (subscriber is not null)
        {
            clusterBuilder.Subscribe(subscriber);
        }

        return clusterBuilder;
    }

    /// <summary>
    /// Creates the subscriber, or <see langword="null"/> when the tracing and metrics are disabled.
    /// </summary>
    /// <param name="options">Settings for the tracing and metrics.</param>
    /// <param name="loggerFactory">Logger factory used to create subscriber loggers.</param>
    /// <param name="name">Name identifying the client belongs to.</param>
    /// <returns>A new <see cref="MongoConnectionEventSubscriber"/> instance, or null if disabled.</returns>
    public static MongoConnectionEventSubscriber? CreateSubscriber(
        MongoConnectionTracingOptions options,
        ILoggerFactory loggerFactory,
        string name)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return options.Enabled
            ? new MongoConnectionEventSubscriber(name, options, loggerFactory.CreateLogger<MongoConnectionEventSubscriber>())
            : null;
    }

    /// <summary>
    /// Creates the subscriber, or <see langword="null"/> when the tracing and metrics are disabled.
    /// </summary>
    /// <param name="name">Name identifying the client belongs to.</param>
    /// <param name="options">Optional settings for the tracing and metrics.</param>
    /// <param name="loggerFactory">Optional logger factory used to create subscriber loggers.</param>
    /// <returns>A new <see cref="MongoConnectionEventSubscriber"/> instance, or null if disabled.</returns>
    public static MongoConnectionEventSubscriber? CreateSubscriber(
        string name,
        MongoConnectionTracingOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var effectiveOptions = options ?? new MongoConnectionTracingOptions();
        var effectiveLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

        return CreateSubscriber(effectiveOptions, effectiveLoggerFactory, name);
    }

    private static void RegisterOpenTelemetry(IServiceCollection services)
    {
        services
            .AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(MongoConnectionEventSubscriber.ActivitySourceName))
            .WithMetrics(metrics => metrics.AddMeter(MongoConnectionEventSubscriber.MeterName));
    }
}

