using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver.Core.Configuration;
using Xunit;

namespace Mongo.Connection.Telemetry.Test;

public class MongoConnectionTracingExtensionsTests
{
    [Fact]
    public void AddMongoConnectionTracing_Parameterless_RegistersDefaults()
    {
        var services = new ServiceCollection();
        services.AddMongoConnectionTracing();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoConnectionTracingOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.SlowCheckoutThresholdInMilliseconds.Should().Be(10);
    }

    [Fact]
    public void AddMongoConnectionTracing_WithConfiguration_UsesDefaultSectionName()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            [$"{MongoConnectionTracingOptions.DefaultSectionName}:Enabled"] = "true",
            [$"{MongoConnectionTracingOptions.DefaultSectionName}:SlowCheckoutThresholdInMilliseconds"] = "50"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddMongoConnectionTracing(configuration);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoConnectionTracingOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.SlowCheckoutThresholdInMilliseconds.Should().Be(50);
    }

    [Fact]
    public void AddMongoConnectionTracing_BindsConfigurationAndRegistersTelemetry()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["CustomSection:Enabled"] = "true",
            ["CustomSection:SlowCheckoutThresholdInMilliseconds"] = "25"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMongoConnectionTracing(configuration, "CustomSection");

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoConnectionTracingOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.SlowCheckoutThresholdInMilliseconds.Should().Be(25);
    }

    [Fact]
    public void AddMongoConnectionTracing_WithAction_ConfiguresOptions()
    {
        var services = new ServiceCollection();
        services.AddMongoConnectionTracing(options =>
        {
            options.Enabled = false;
            options.SlowCheckoutThresholdInMilliseconds = 100;
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoConnectionTracingOptions>>().Value;

        options.Enabled.Should().BeFalse();
        options.SlowCheckoutThresholdInMilliseconds.Should().Be(100);
    }

    [Fact]
    public void AddMongoConnectionTracing_WithConfigurationAndAction_CombinesBoth()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            [$"{MongoConnectionTracingOptions.DefaultSectionName}:Enabled"] = "true",
            [$"{MongoConnectionTracingOptions.DefaultSectionName}:SlowCheckoutThresholdInMilliseconds"] = "50"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddMongoConnectionTracing(configuration, options =>
        {
            options.SlowCheckoutThresholdInMilliseconds = 75;
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoConnectionTracingOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.SlowCheckoutThresholdInMilliseconds.Should().Be(75);
    }

    [Fact]
    public void AddMongoConnectionTracing_ThrowsOnNullArguments()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var actNullServices = () => MongoConnectionTracingExtensions.AddMongoConnectionTracing(null!);
        actNullServices.Should().Throw<ArgumentNullException>();

        var actNullConfig = () => services.AddMongoConnectionTracing(null!);
        actNullConfig.Should().Throw<ArgumentNullException>();

        var actEmptySection = () => services.AddMongoConnectionTracing(configuration, "");
        actEmptySection.Should().Throw<ArgumentException>();

        var actNullAction = () => services.AddMongoConnectionTracing((Action<MongoConnectionTracingOptions>)null!);
        actNullAction.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SubscribeConnectionTracing_SubscribesWhenEnabled()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            [$"{MongoConnectionTracingOptions.DefaultSectionName}:Enabled"] = "true"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMongoConnectionTracing(configuration, MongoConnectionTracingOptions.DefaultSectionName);

        var provider = services.BuildServiceProvider();
        var clusterBuilder = new ClusterBuilder();

        var result = clusterBuilder.SubscribeConnectionTracing(provider, "test-pool");
        result.Should().BeSameAs(clusterBuilder);
    }

    [Fact]
    public void SubscribeConnectionTracing_WithExplicitOptions_SubscribesAndReturnsBuilder()
    {
        var clusterBuilder = new ClusterBuilder();
        var options = new MongoConnectionTracingOptions { Enabled = true };

        var result = clusterBuilder.SubscribeConnectionTracing("test-pool", options, NullLoggerFactory.Instance);
        result.Should().BeSameAs(clusterBuilder);
    }

    [Fact]
    public void SubscribeConnectionTracing_ThrowsOnInvalidArguments()
    {
        var clusterBuilder = new ClusterBuilder();
        var provider = new ServiceCollection().BuildServiceProvider();

        var actNullClusterBuilder = () => MongoConnectionTracingExtensions.SubscribeConnectionTracing(null!, provider, "test-pool");
        actNullClusterBuilder.Should().Throw<ArgumentNullException>();

        var actNullProvider = () => clusterBuilder.SubscribeConnectionTracing(null!, "test-pool");
        actNullProvider.Should().Throw<ArgumentNullException>();

        var actEmptyName = () => clusterBuilder.SubscribeConnectionTracing(provider, " ");
        actEmptyName.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateSubscriber_ReturnsNullWhenDisabled()
    {
        var options = new MongoConnectionTracingOptions { Enabled = false };
        var subscriber = MongoConnectionTracingExtensions.CreateSubscriber(
            options,
            NullLoggerFactory.Instance,
            "test-pool");

        subscriber.Should().BeNull();
    }

    [Fact]
    public void CreateSubscriber_ConvenienceOverload_WorksCorrectly()
    {
        var enabledSubscriber = MongoConnectionTracingExtensions.CreateSubscriber("test-pool");
        enabledSubscriber.Should().NotBeNull();

        var disabledSubscriber = MongoConnectionTracingExtensions.CreateSubscriber(
            "test-pool",
            new MongoConnectionTracingOptions { Enabled = false });
        disabledSubscriber.Should().BeNull();
    }
}
