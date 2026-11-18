using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver.Core.Configuration;
using Xunit;

namespace MongoConnectionTelemetry.Test;

public class MongoConnectionTracingExtensionsTests
{
    [Fact]
    public void AddMongoConnectionTracing_BindsConfigurationAndRegistersTelemetry()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["MongoConnectionTelemetry:Enabled"] = "true",
            ["MongoConnectionTelemetry:SlowCheckoutThresholdInMilliseconds"] = "25"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMongoConnectionTracing(configuration);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoConnectionTracingOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.SlowCheckoutThresholdInMilliseconds.Should().Be(25);
    }

    [Fact]
    public void SubscribeConnectionTracing_SubscribesWhenEnabled()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["MongoConnectionTelemetry:Enabled"] = "true"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMongoConnectionTracing(configuration);

        var provider = services.BuildServiceProvider();
        var clusterBuilder = new ClusterBuilder();

        clusterBuilder.SubscribeConnectionTracing(provider, "test-pool");
    }

    [Fact]
    public void CreateSubscriber_ReturnsNullWhenDisabled()
    {
        var options = new MongoConnectionTracingOptions { Enabled = false };
        var subscriber = MongoConnectionTracingExtensions.CreateSubscriber(
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            "test-pool");

        subscriber.Should().BeNull();
    }
}
