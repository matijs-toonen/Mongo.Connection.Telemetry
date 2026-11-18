using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;
using Xunit;

namespace MongoConnectionTelemetry.Test;

public class MongoConnectionEventSubscriberTests : IDisposable
{
    private const int SlowCheckoutThresholdInMilliseconds = 10;
    private const int MaxSize = 2;
    private const string Name = "test";

    private static readonly ClusterId ClusterId = new();
    private static readonly ServerId ServerId = new(ClusterId, new DnsEndPoint("localhost", 27017));
    private static readonly ServerId OtherServerId = new(ClusterId, new DnsEndPoint("other-host", 27018));
    private static readonly ConnectionId ConnectionId = new(ServerId, 1);
    private static readonly ConnectionId OtherConnectionId = new(OtherServerId, 2);

    private readonly List<Activity> _recordedActivities = [];
    private readonly ActivityListener _activityListener;

    public MongoConnectionEventSubscriberTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MongoConnectionEventSubscriber.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _recordedActivities.Add
        };

        ActivitySource.AddActivityListener(_activityListener);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _activityListener.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Checkout_WithoutWaiting_IsNotReported()
    {
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 1));

        _recordedActivities.Should().BeEmpty();
    }

    [Fact]
    public void Checkout_ThatWaited_IsReportedWithTheConnectionInformation()
    {
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: MaxSize)));

        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.FromMilliseconds(500), 1));

        var activity = _recordedActivities.Should().ContainSingle().Subject;
        activity.Duration.Should().Be(TimeSpan.FromMilliseconds(500));
        activity.Status.Should().Be(ActivityStatusCode.Unset);
        activity.GetTagItem("db.client.connection.wait_time_ms").Should().Be(500d);
        activity.GetTagItem("db.client.connection.count").Should().Be(1);
        activity.GetTagItem("db.client.connection.max").Should().Be(MaxSize);
        activity.TagObjects.Select(tag => tag.Key).Should().BeEquivalentTo(
            "db.client.connection.wait_time_ms",
            "db.client.connection.count",
            "db.client.connection.max");
    }

    [Fact]
    public void Checkout_FromAnExhausted_IsReportedAsExhausted()
    {
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: MaxSize)));

        for (var checkout = 0; checkout < MaxSize; checkout++)
        {
            Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.FromMilliseconds(500), 1));
        }

        var activity = _recordedActivities.Should().HaveCount(MaxSize).And.Subject.Last();
        activity.GetTagItem("db.client.connection.count").Should().Be(MaxSize);
    }

    [Fact]
    public void Checkout_OfAConnectionThatIsCheckedInAgain_DoesNotExhaustThe()
    {
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: MaxSize)));

        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.FromMilliseconds(500), 1));
        Publish(subscriber, new ConnectionPoolCheckedInConnectionEvent(ConnectionId, TimeSpan.Zero, 1));
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.FromMilliseconds(500), 2));

        _recordedActivities.Should().AllSatisfy(activity =>
            activity.GetTagItem("db.client.connection.count").Should().Be(1));
    }

    [Fact]
    public void Checkout_ThatFailed_IsReportedAsAnError()
    {
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolCheckingOutConnectionFailedEvent(
            ServerId,
            new TimeoutException(),
            operationId: 1,
            TimeSpan.FromSeconds(2),
            ConnectionCheckOutFailedReason.Timeout));

        var activity = _recordedActivities.Should().ContainSingle().Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.GetTagItem("db.client.connection.checkout.failure_reason").Should().Be(nameof(ConnectionCheckOutFailedReason.Timeout));
        activity.TagObjects.Select(tag => tag.Key).Should().BeEquivalentTo(
            "db.client.connection.wait_time_ms",
            "db.client.connection.count",
            "db.client.connection.max",
            "db.client.connection.checkout.failure_reason");
    }

    [Fact]
    public void Checkout_ThatFailed_IsReportedEvenWhenItWasFasterThanTheThreshold()
    {
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolCheckingOutConnectionFailedEvent(
            ServerId,
            new TimeoutException(),
            operationId: 1,
            TimeSpan.Zero,
            ConnectionCheckOutFailedReason.ConnectionError));

        _recordedActivities.Should().ContainSingle();
    }

    [Fact]
    public void CheckoutWaitIsRecordedAsMetricEvenWhenNoSpanIsCreated()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: MaxSize)));

        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.FromMilliseconds(5), 1));
        listener.RecordObservableInstruments();

        _recordedActivities.Should().BeEmpty();
        var waitTime = measurements.Should().ContainSingle(measurement =>
            measurement.InstrumentName == "db.client.connection.wait_time").Subject;
        ((double)waitTime.Measurement).Should().BeApproximately(5d, 0.000001d);
        waitTime.Tags.Should().BeEquivalentTo(Tags());
    }

    [Fact]
    public void FailedCheckoutWaitMetricIncludesTheFailureReason()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: 1)));
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 1));

        Publish(subscriber, new ConnectionPoolCheckingOutConnectionFailedEvent(
            ServerId,
            new TimeoutException(),
            operationId: 1,
            TimeSpan.FromMilliseconds(25),
            ConnectionCheckOutFailedReason.Timeout));

        var waitTime = measurements.Should().ContainSingle(measurement =>
            measurement.InstrumentName == "db.client.connection.wait_time" &&
            measurement.Tags.ContainsKey("db.client.connection.checkout.failure_reason")).Subject;
        waitTime.Tags["db.client.connection.checkout.failure_reason"].Should().Be(nameof(ConnectionCheckOutFailedReason.Timeout));
        waitTime.Tags.Should().Contain(Tags());
    }

    [Fact]
    public void ConnectionCountMetricReportsTheNumberOfConnectionsInUse()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: 3)));
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 1));
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 2));

        listener.RecordObservableInstruments();

        var inUse = ConnectionCountMeasurements(measurements).Should().ContainSingle().Subject;
        ((int)inUse.Measurement).Should().Be(2);
        inUse.Tags.Should().BeEquivalentTo(Tags());
    }

    [Fact]
    public void ConnectionCountMetricIsReportedPerServer()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 1));
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(OtherConnectionId, TimeSpan.Zero, 2));
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(OtherConnectionId, TimeSpan.Zero, 3));

        listener.RecordObservableInstruments();

        ConnectionCountMeasurements(measurements)
            .Select(measurement => ((int)measurement.Measurement, measurement.Tags["server.address"]))
            .Should().BeEquivalentTo([(1, "localhost"), (2, "other-host")]);
    }

    [Fact]
    public void MeasurementsOfDifferentNamesAreKeptApart()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        var otherSubscriber = CreateSubscriber("other-");
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 1));
        Publish(otherSubscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 2));

        listener.RecordObservableInstruments();

        ConnectionCountMeasurements(measurements)
            .Select(measurement => measurement.Tags["db.client.connection.pool.name"])
            .Should().BeEquivalentTo([Name, "other-"]);
    }

    [Fact]
    public void ConnectionCountMetricReportsAPeakThatIsAlreadyOverWhenTheMetricsAreCollected()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: 3)));

        for (var checkout = 1; checkout <= 3; checkout++)
        {
            Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, checkout));
        }

        for (var checkIn = 1; checkIn <= 3; checkIn++)
        {
            Publish(subscriber, new ConnectionPoolCheckedInConnectionEvent(ConnectionId, TimeSpan.Zero, checkIn));
        }

        listener.RecordObservableInstruments();

        ConnectionCountMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(3);
    }

    [Fact]
    public void ConnectionCountMetricOnlyReportsThePeakOfTheIntervalItBelongsTo()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: 3)));
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 1));
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 2));
        Publish(subscriber, new ConnectionPoolCheckedInConnectionEvent(ConnectionId, TimeSpan.Zero, 1));

        listener.RecordObservableInstruments();
        listener.RecordObservableInstruments();

        ConnectionCountMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(2, 1);
    }

    [Fact]
    public void CheckInOfAConnectionThatWasNotCheckedOutDoesNotChangeConnectionCountMetric()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolCheckedInConnectionEvent(ConnectionId, TimeSpan.Zero, 1));
        listener.RecordObservableInstruments();

        ConnectionCountMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(0);
    }

    [Fact]
    public void ConnectionCountNeverBecomesNegative()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: MaxSize)));

        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, 1));
        Publish(subscriber, new ConnectionPoolCheckedInConnectionEvent(ConnectionId, TimeSpan.Zero, 1));
        Publish(subscriber, new ConnectionPoolCheckedInConnectionEvent(ConnectionId, TimeSpan.Zero, 2));
        listener.RecordObservableInstruments();
        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.FromMilliseconds(500), 2));
        listener.RecordObservableInstruments();

        ConnectionCountMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(1, 1);
        _recordedActivities.Should().AllSatisfy(activity =>
            activity.GetTagItem("db.client.connection.count").Should().Be(1));
    }

    [Fact]
    public void MaxConnectionCountMetricIsReportedWhenTheIsOpened()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: 11)));
        listener.RecordObservableInstruments();

        var max = MaxConnectionCountMeasurements(measurements).Should().ContainSingle().Subject;
        ((int)max.Measurement).Should().Be(11);
        max.Tags.Should().BeEquivalentTo(Tags());
    }

    [Fact]
    public void MaxConnectionCountMetricIsReportedOnEveryCollection()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: 12)));

        listener.RecordObservableInstruments();
        listener.RecordObservableInstruments();

        MaxConnectionCountMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(12, 12);
    }

    [Fact]
    public void MaxConnectionCountMetricKeepsTheValueOfTheFirstReport()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: 13)));
        Publish(subscriber, new ConnectionPoolOpenedEvent(ServerId, new ConnectionPoolSettings(maxConnections: 14)));
        listener.RecordObservableInstruments();

        MaxConnectionCountMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(13);
    }

    [Fact]
    public void FailedCheckoutDoesNotChangeConnectionCountMetric()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolCheckingOutConnectionFailedEvent(
            ServerId,
            new TimeoutException(),
            operationId: 1,
            TimeSpan.FromMilliseconds(25),
            ConnectionCheckOutFailedReason.Timeout));
        listener.RecordObservableInstruments();

        ConnectionCountMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(0);
    }

    [Fact]
    public void PendingRequestMetricReportsCheckoutsWaitingForAConnection()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolCheckingOutConnectionEvent(ServerId, operationId: 1));
        listener.RecordObservableInstruments();

        var pendingRequests = PendingRequestMeasurements(measurements).Should().ContainSingle().Subject;
        ((int)pendingRequests.Measurement).Should().Be(1);
        pendingRequests.Tags.Should().BeEquivalentTo(Tags());
    }

    [Fact]
    public void PendingRequestMetricIsReportedPerServer()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolCheckingOutConnectionEvent(ServerId, operationId: 1));
        Publish(subscriber, new ConnectionPoolCheckingOutConnectionEvent(OtherServerId, operationId: 2));
        Publish(subscriber, new ConnectionPoolCheckingOutConnectionEvent(OtherServerId, operationId: 3));
        listener.RecordObservableInstruments();

        PendingRequestMeasurements(measurements)
            .Select(measurement => ((int)measurement.Measurement, measurement.Tags["server.address"]))
            .Should().BeEquivalentTo([(1, "localhost"), (2, "other-host")]);
    }

    [Fact]
    public void PendingRequestMetricReportsAPeakThatIsAlreadyOverWhenTheMetricsAreCollected()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();

        for (var operation = 1; operation <= 3; operation++)
        {
            Publish(subscriber, new ConnectionPoolCheckingOutConnectionEvent(ServerId, operation));
        }

        for (var operation = 1; operation <= 3; operation++)
        {
            Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(
                ConnectionId, TimeSpan.Zero, operation));
        }

        listener.RecordObservableInstruments();
        listener.RecordObservableInstruments();

        PendingRequestMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(3, 0);
    }

    [Fact]
    public void FailedCheckoutLeavesThePendingRequestQueue()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();
        Publish(subscriber, new ConnectionPoolCheckingOutConnectionEvent(ServerId, operationId: 1));

        Publish(subscriber, new ConnectionPoolCheckingOutConnectionFailedEvent(
            ServerId,
            new TimeoutException(),
            operationId: 1,
            TimeSpan.FromMilliseconds(25),
            ConnectionCheckOutFailedReason.Timeout));
        listener.RecordObservableInstruments();
        listener.RecordObservableInstruments();

        PendingRequestMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(1, 0);
    }

    [Fact]
    public void CheckedOutConnectionWithoutACheckoutStartDoesNotMakePendingRequestCountNegative()
    {
        var measurements = new List<RecordedMeasurement>();
        using var listener = CreateMeterListener(measurements);
        var subscriber = CreateSubscriber();

        Publish(subscriber, new ConnectionPoolCheckedOutConnectionEvent(ConnectionId, TimeSpan.Zero, operationId: 1));
        listener.RecordObservableInstruments();

        PendingRequestMeasurements(measurements)
            .Select(measurement => (int)measurement.Measurement)
            .Should().Equal(0);
    }

    private static List<RecordedMeasurement> ConnectionCountMeasurements(List<RecordedMeasurement> measurements) =>
        measurements
            .Where(measurement =>
                measurement.OfTheSubscriberOfTheTest && measurement.InstrumentName == "db.client.connection.count")
            .ToList();

    private static List<RecordedMeasurement> PendingRequestMeasurements(List<RecordedMeasurement> measurements) =>
        measurements
            .Where(measurement =>
                measurement.OfTheSubscriberOfTheTest && measurement.InstrumentName == "db.client.connection.wait_queue")
            .ToList();

    private static List<RecordedMeasurement> MaxConnectionCountMeasurements(List<RecordedMeasurement> measurements) =>
        measurements
            .Where(measurement =>
                measurement.OfTheSubscriberOfTheTest && measurement.InstrumentName == "db.client.connection.max")
            .ToList();

    private static Dictionary<string, object?> Tags(string name = Name) =>
        new()
        {
            ["db.client.connection.pool.name"] = name,
            ["server.address"] = "localhost",
            ["server.port"] = 27017
        };

    private static MongoConnectionEventSubscriber CreateSubscriber(string name = Name) =>
        new(
            name,
            new MongoConnectionTracingOptions { SlowCheckoutThresholdInMilliseconds = SlowCheckoutThresholdInMilliseconds },
            NullLogger.Instance);

    // The meter is static, the instruments of the subscribers created by the other tests stay published on it and
    // keep reporting. The instruments that are published after the listener has started belong to the subscriber of
    // the test itself, which therefore has to be created after the listener, their measurements are marked as such.
    private static MeterListener CreateMeterListener(List<RecordedMeasurement> measurements)
    {
        var started = false;
        var instrumentsOfTheTest = new HashSet<Instrument>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name != MongoConnectionEventSubscriber.MeterName)
                {
                    return;
                }

                if (started)
                {
                    instrumentsOfTheTest.Add(instrument);
                }

                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, _) =>
            measurements.Add(new RecordedMeasurement(
                instrument.Name, measurement, GetTags(tags), instrumentsOfTheTest.Contains(instrument))));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            measurements.Add(new RecordedMeasurement(
                instrument.Name, measurement, GetTags(tags), instrumentsOfTheTest.Contains(instrument))));
        listener.Start();
        started = true;
        return listener;
    }

    private static Dictionary<string, object?> GetTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>();

        foreach (var tag in tags)
        {
            result.Add(tag.Key, tag.Value);
        }

        return result;
    }

    private static void Publish<TEvent>(IEventSubscriber subscriber, TEvent @event)
    {
        subscriber.TryGetEventHandler<TEvent>(out var handler).Should().BeTrue();

        handler(@event);
    }

    private sealed record RecordedMeasurement(
        string InstrumentName,
        object Measurement,
        Dictionary<string, object?> Tags,
        bool OfTheSubscriberOfTheTest);
}
