using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Mongo.Connection.Telemetry.Test;

public class MongoConnectionTracingExtensionsTests
{
    private const string PoolName = "test-pool";

    [Fact]
    public void SubscribeConnectionTracing_WithExplicitOptionsAndLoggerFactory_SubscribesWhenEnabled()
    {
        var clusterBuilder = new ClusterBuilder();
        var options = new MongoConnectionTracingOptions { Enabled = true };
        var loggerFactory = NullLoggerFactory.Instance;

        var result = clusterBuilder.SubscribeConnectionTracing(PoolName, options, loggerFactory);

        result.Should().BeSameAs(clusterBuilder);
    }

    [Fact]
    public void SubscribeConnectionTracing_WithExplicitOptionsAndLoggerFactory_DoesNotSubscribeWhenDisabled()
    {
        var clusterBuilder = new ClusterBuilder();
        var options = new MongoConnectionTracingOptions { Enabled = false };
        var loggerFactory = NullLoggerFactory.Instance;

        var result = clusterBuilder.SubscribeConnectionTracing(PoolName, options, loggerFactory);

        result.Should().BeSameAs(clusterBuilder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SubscribeConnectionTracing_WithExplicitOptionsAndLoggerFactory_ThrowsOnInvalidName(string? invalidName)
    {
        var clusterBuilder = new ClusterBuilder();
        var options = new MongoConnectionTracingOptions();

        var act = () => clusterBuilder.SubscribeConnectionTracing(invalidName!, options, NullLoggerFactory.Instance);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SubscribeConnectionTracing_WithExplicitOptionsAndLoggerFactory_ThrowsOnNullArguments()
    {
        var clusterBuilder = new ClusterBuilder();
        var options = new MongoConnectionTracingOptions();

        var actNullBuilder = () => MongoConnectionTracingExtensions.SubscribeConnectionTracing(null!, PoolName, options, NullLoggerFactory.Instance);
        actNullBuilder.Should().Throw<ArgumentNullException>();

        var actNullOptions = () => clusterBuilder.SubscribeConnectionTracing(PoolName, (MongoConnectionTracingOptions)null!, NullLoggerFactory.Instance);
        actNullOptions.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SubscribeConnectionTracing_WithExplicitOptions_DefaultsLoggerFactoryWhenNull()
    {
        var clusterBuilder = new ClusterBuilder();
        var options = new MongoConnectionTracingOptions { Enabled = true };

        var result = clusterBuilder.SubscribeConnectionTracing(PoolName, options, null);

        result.Should().BeSameAs(clusterBuilder);
    }

    [Fact]
    public void SubscribeConnectionTracing_WithOptionsActionAndLoggerFactory_UsesDefaultsWhenNull()
    {
        var clusterBuilder = new ClusterBuilder();

        var result = clusterBuilder.SubscribeConnectionTracing(PoolName);

        result.Should().BeSameAs(clusterBuilder);
    }

    [Fact]
    public void SubscribeConnectionTracing_WithOptionsActionAndLoggerFactory_AppliesConfigureAction()
    {
        var clusterBuilder = new ClusterBuilder();
        var actionInvoked = false;

        var result = clusterBuilder.SubscribeConnectionTracing(
            PoolName,
            options =>
            {
                options.SlowCheckoutThresholdInMilliseconds = 100;
                actionInvoked = true;
            },
            NullLoggerFactory.Instance);

        result.Should().BeSameAs(clusterBuilder);
        actionInvoked.Should().BeTrue();
    }

    [Fact]
    public void SubscribeConnectionTracing_WithOptionsActionAndLoggerFactory_DoesNotSubscribeWhenDisabled()
    {
        var clusterBuilder = new ClusterBuilder();

        var result = clusterBuilder.SubscribeConnectionTracing(
            PoolName,
            options => options.Enabled = false,
            NullLoggerFactory.Instance);

        result.Should().BeSameAs(clusterBuilder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SubscribeConnectionTracing_WithOptionsActionAndLoggerFactory_ThrowsOnInvalidName(string? invalidName)
    {
        var clusterBuilder = new ClusterBuilder();

        var act = () => clusterBuilder.SubscribeConnectionTracing(invalidName!, _ => { });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SubscribeConnectionTracing_WithOptionsActionAndLoggerFactory_ThrowsOnNullBuilder()
    {
        var act = () => MongoConnectionTracingExtensions.SubscribeConnectionTracing(null!, PoolName);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddMongoConnectionTracing_RegistersActivitySource()
    {
        var builder = Sdk.CreateTracerProviderBuilder();
        var result = builder.AddMongoConnectionTracing();

        result.Should().BeSameAs(builder);

        using var tracerProvider = builder.Build();
        tracerProvider.Should().NotBeNull();
    }

    [Fact]
    public void AddMongoConnectionTracing_ThrowsOnNullBuilder()
    {
        var act = () => MongoConnectionTracingExtensions.AddMongoConnectionTracing(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddMongoConnectionMetrics_RegistersMeter_WithDefaultConfiguration()
    {
        var builder = Sdk.CreateMeterProviderBuilder();
        var result = builder.AddMongoConnectionMetrics();

        result.Should().BeSameAs(builder);

        using var meterProvider = builder.Build();
        meterProvider.Should().NotBeNull();
    }

    [Fact]
    public void AddMongoConnectionMetrics_RegistersMeter_WithCustomHistogramConfiguration()
    {
        var builder = Sdk.CreateMeterProviderBuilder();
        var customConfiguration = new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [5, 10, 25, 50, 100]
        };

        var result = builder.AddMongoConnectionMetrics(customConfiguration);

        result.Should().BeSameAs(builder);

        using var meterProvider = builder.Build();
        meterProvider.Should().NotBeNull();
    }

    [Fact]
    public void AddMongoConnectionMetrics_ThrowsOnNullBuilder()
    {
        var act = () => MongoConnectionTracingExtensions.AddMongoConnectionMetrics(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
