using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver.Core.Configuration;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Mongo.Connection.Telemetry;

/// <summary>
/// Startup extensions for the MongoDb connection tracing and metrics.
/// </summary>
public static class MongoConnectionTracingExtensions
{
    /// <summary>
    /// Subscribes the connection tracing on a MongoDb client that is being built using explicit options and logger factory.
    /// </summary>
    /// <param name="clusterBuilder">The cluster builder to configure.</param>
    /// <param name="name">Name identifying the client belongs to.</param>
    /// <param name="options">Settings for tracing and metrics.</param>
    /// <param name="loggerFactory">Logger factory used to create subscriber loggers.</param>
    /// <returns>The cluster builder for chaining.</returns>
    public static ClusterBuilder SubscribeConnectionTracing(
        this ClusterBuilder clusterBuilder,
        string name,
        MongoConnectionTracingOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(clusterBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        loggerFactory ??= NullLoggerFactory.Instance;
        var subscriber = CreateSubscriber(name, options, loggerFactory);

        if (subscriber is not null)
        {
            clusterBuilder.Subscribe(subscriber);
        }

        return clusterBuilder;
    }

    /// <summary>
    /// Subscribes the connection tracing on a MongoDb client that is being built using an optional options configuration delegate and logger factory.
    /// </summary>
    /// <param name="clusterBuilder">The cluster builder to configure.</param>
    /// <param name="name">Name identifying the client belongs to.</param>
    /// <param name="configureOptions">An optional delegate to configure the tracing and metrics options.</param>
    /// <param name="loggerFactory">Optional logger factory used to create subscriber loggers. Defaults to <see cref="NullLoggerFactory.Instance"/> if not provided.</param>
    /// <returns>The cluster builder for chaining.</returns>
    public static ClusterBuilder SubscribeConnectionTracing(
        this ClusterBuilder clusterBuilder,
        string name,
        Action<MongoConnectionTracingOptions>? configureOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        var options = new MongoConnectionTracingOptions();
        configureOptions?.Invoke(options);
        return clusterBuilder.SubscribeConnectionTracing(name, options, loggerFactory);
    }

    /// <summary>
    /// Creates the subscriber, or <see langword="null"/> when the tracing and metrics are disabled.
    /// </summary>
    /// <param name="name">Name identifying the client belongs to.</param>
    /// <param name="options">Settings for the tracing and metrics.</param>
    /// <param name="loggerFactory">Logger factory used to create subscriber loggers.</param>
    /// <returns>A new <see cref="MongoConnectionEventSubscriber"/> instance, or null if disabled.</returns>
    private static MongoConnectionEventSubscriber? CreateSubscriber(
        string name,
        MongoConnectionTracingOptions options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return options.Enabled
            ? new MongoConnectionEventSubscriber(name, options, loggerFactory.CreateLogger<MongoConnectionEventSubscriber>())
            : null;
    }

    /// <summary>
    /// Adds the MongoDB connection tracing activity source to the OpenTelemetry tracer provider builder.
    /// </summary>
    /// <param name="tracerProviderBuilder">The tracer provider builder to configure.</param>
    /// <returns>The tracer provider builder for chaining.</returns>
    public static TracerProviderBuilder AddMongoConnectionTracing(this TracerProviderBuilder tracerProviderBuilder)
    {
        ArgumentNullException.ThrowIfNull(tracerProviderBuilder);
        return tracerProviderBuilder.AddSource(MongoConnectionEventSubscriber.ActivitySourceName);
    }

    /// <summary>
    /// Adds the MongoDB connection metrics meter to the OpenTelemetry meter provider builder.
    /// </summary>
    /// <param name="metricProviderBuilder">The meter provider builder to configure.</param>
    /// <param name="bucketHistogramConfiguration">Optional explicit bucket histogram configuration for the meter views. Defaults to explicit boundaries: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15, 20, 50, 100, 250, 500, 1000, 5000, 10_000] if not provided.</param>
    /// <returns>The meter provider builder for chaining.</returns>
    public static MeterProviderBuilder AddMongoConnectionMetrics(this MeterProviderBuilder metricProviderBuilder, ExplicitBucketHistogramConfiguration? bucketHistogramConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(metricProviderBuilder);
        bucketHistogramConfiguration ??= new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15, 20, 50, 100, 250, 500, 1000, 5000, 10_000]
        };

        return metricProviderBuilder.AddMeter(MongoConnectionEventSubscriber.MeterName)
            .AddView(MongoConnectionEventSubscriber.MeterName, bucketHistogramConfiguration);
    }
}

