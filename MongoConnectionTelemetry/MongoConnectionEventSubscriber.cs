using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;

namespace MongoConnectionTelemetry;

/// <summary>
/// Reports MongoDb connection pool and wait queue metrics, as well as spans for checkouts that had to wait in the
/// queue for a connection to become available from the pool.
///
/// The spans created by the MongoDb driver instrumentation only start once a command is sent, which is after a
/// connection has been checked out of the pool. Time spent waiting in the wait queue for a free connection is therefore
/// invisible in the traces: it shows up as an unexplained gap between the application span and the MongoDb command span.
/// This subscriber closes that gap by emitting a span for every checkout that waited in the queue longer than the configured
/// threshold, and for every checkout that failed (for instance because it timed out waiting in the wait queue for the pool).
/// </summary>
public sealed class MongoConnectionEventSubscriber : IEventSubscriber
{
    /// <summary>
    /// Name of the <see cref="System.Diagnostics.ActivitySource"/> used, it has to be registered on the tracer
    /// provider for the spans to be exported.
    /// </summary>
    public const string ActivitySourceName = "MongoDb.Connection.Telemetry";

    /// <summary>
    /// Name of the <see cref="Meter"/> used to publish connection pool and wait queue metrics.
    /// </summary>
    public const string MeterName = "MongoDb.Connection.Telemetry";

    private const string CheckoutActivityName = "MongoDb connection received from pool";
    private const string IdleTagName = "db.client.connection.pool.idle";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);

    private readonly ConcurrentDictionary<ServerId, PoolState> _poolStates = new();
    private readonly ILogger _logger;
    private readonly string _connectionInstanceName;
    private readonly TimeSpan _slowCheckoutThreshold;
    private readonly TimeSpan _idleAfterNoRequests;
    private readonly Histogram<double> _connectionWaitTime;

    private readonly Action<ConnectionPoolOpenedEvent> _openedHandler;
    private readonly Action<ConnectionPoolCheckingOutConnectionEvent> _checkingOutHandler;
    private readonly Action<ConnectionPoolCheckedOutConnectionEvent> _checkedOutHandler;
    private readonly Action<ConnectionPoolCheckedInConnectionEvent> _checkedInHandler;
    private readonly Action<ConnectionPoolCheckingOutConnectionFailedEvent> _checkingOutFailedHandler;

    /// <summary>
    /// Creates the subscriber for a single MongoDb client.
    /// </summary>
    /// <param name="connectionInstanceName">Unique name identifying the client belongs to, used in the log messages and metric tags to identify the source of the metric.</param>
    /// <param name="options">Settings for the tracing and metrics.</param>
    /// <param name="logger">Logger used to report failed checkouts.</param>
    public MongoConnectionEventSubscriber(string connectionInstanceName, MongoConnectionTracingOptions options, ILogger logger)
    {
        _connectionInstanceName = connectionInstanceName;
        _logger = logger;
        _slowCheckoutThreshold = TimeSpan.FromMilliseconds(options.SlowCheckoutThresholdInMilliseconds);
        _idleAfterNoRequests = TimeSpan.FromSeconds(options.IdleAfterNoRequestsInSeconds);
        _openedHandler = Handle;
        _checkingOutHandler = Handle;
        _checkedOutHandler = Handle;
        _checkedInHandler = Handle;
        _checkingOutFailedHandler = Handle;

        // Recording the number of connections in use on every checkout would only export the value of the moment the
        // metrics are collected, peaks that are over before the next collection would be invisible. The number of
        // connections in use is therefore tracked as a high watermark that is published by an observable instrument:
        // every collection reports the highest number of connections that were in use since the previous collection,
        // so a peak that only lasted a millisecond is still exported.
        //
        // A client talks to every server of the cluster through a pool of its own, and the meter is shared by all
        // clients of the process. Every measurement therefore carries the name of the pool and the address of the
        // server it belongs to, which keeps the series of the separate pools apart: the instruments are shared, the
        // measurements are not. Instrument level tags are not used for this, the OpenTelemetry SDK drops them
        // because the specification does not support them yet.
        _ = Meter.CreateObservableGauge(
            "db.client.connection.count",
            ObserveConnectionsInUse,
            "{connection}",
            "Highest number of connections in use since the previous collection.");
        _ = Meter.CreateObservableGauge(
            "db.client.connection.wait_queue",
            ObservePendingRequests,
            "{request}",
            "Highest number of requests waiting for a connection since the previous collection.");
        _ = Meter.CreateObservableUpDownCounter(
            "db.client.connection.max",
            ObserveMaxConnections,
            "{connection}",
            "Maximum number of connections in the pool.");
        _connectionWaitTime = Meter.CreateHistogram<double>(
            "db.client.connection.wait_time",
            "ms",
            "Time spent waiting to obtain a connection from the pool.");
    }

    /// <inheritdoc />
    public bool TryGetEventHandler<TEvent>(out Action<TEvent> handler)
    {
        if (typeof(TEvent) == typeof(ConnectionPoolOpenedEvent))
        {
            handler = Unsafe.As<Action<TEvent>>(_openedHandler);
            return true;
        }

        if (typeof(TEvent) == typeof(ConnectionPoolCheckingOutConnectionEvent))
        {
            handler = Unsafe.As<Action<TEvent>>(_checkingOutHandler);
            return true;
        }

        if (typeof(TEvent) == typeof(ConnectionPoolCheckedOutConnectionEvent))
        {
            handler = Unsafe.As<Action<TEvent>>(_checkedOutHandler);
            return true;
        }

        if (typeof(TEvent) == typeof(ConnectionPoolCheckedInConnectionEvent))
        {
            handler = Unsafe.As<Action<TEvent>>(_checkedInHandler);
            return true;
        }

        if (typeof(TEvent) == typeof(ConnectionPoolCheckingOutConnectionFailedEvent))
        {
            handler = Unsafe.As<Action<TEvent>>(_checkingOutFailedHandler);
            return true;
        }

        handler = null!;
        return false;
    }

    // The maximum pool size is a setting of the client, it never changes after the pool has been opened, so it is
    // only stored the first time the pool reports it. It is published by an observable instrument because the value
    // has to be reported on every collection interval for the alerting to keep seeing it.
    private void Handle(ConnectionPoolOpenedEvent @event) =>
        GetPoolState(@event.ServerId).TrySetMaxPoolSize(@event.ConnectionPoolSettings.MaxConnections);

    private void Handle(ConnectionPoolCheckingOutConnectionEvent @event)
    {
        var poolState = GetPoolState(@event.ServerId);
        poolState.RecordRequest();
        poolState.BeginCheckout();
        poolState.TrackPeakPendingRequests();
    }

    private void Handle(ConnectionPoolCheckedOutConnectionEvent @event)
    {
        var poolState = GetPoolState(@event.ConnectionId.ServerId);
        poolState.RecordRequest();
        var connectionsInUse = poolState.Acquire();
        poolState.TryEndCheckout();
        var maxPoolSize = poolState.MaxPoolSize;

        poolState.TrackPeakConnectionsInUse();
        RecordWaitTime(poolState, @event.Duration, failureReason: null);

        if (@event.Duration < _slowCheckoutThreshold)
        {
            return;
        }

        Report(@event.Duration, connectionsInUse, maxPoolSize, failureReason: null);
    }

    private void Handle(ConnectionPoolCheckedInConnectionEvent @event) =>
        GetPoolState(@event.ConnectionId.ServerId).TryRelease();

    private void Handle(ConnectionPoolCheckingOutConnectionFailedEvent @event)
    {
        var poolState = GetPoolState(@event.ServerId);
        poolState.RecordRequest();
        poolState.TryEndCheckout();
        var failureReason = @event.Reason.ToString();
        var connectionsInUse = poolState.ConnectionsInUse;
        var maxPoolSize = poolState.MaxPoolSize;

        RecordWaitTime(poolState, @event.Duration, failureReason);
        Report(@event.Duration, connectionsInUse, maxPoolSize, failureReason);
    }

    private PoolState GetPoolState(ServerId serverId) =>
        _poolStates.GetOrAdd(
            serverId,
            static (id, state) => new PoolState(id, state.Name, state.IdleAfterNoRequests),
            (Name: _connectionInstanceName, IdleAfterNoRequests: _idleAfterNoRequests));

    // Every server of the client keeps its own counters and its own high water mark, each of them is reported as a
    // measurement of its own that is tagged with the server it belongs to. The high water mark is reset to the number
    // of connections that is in use right now, the next collection reports the peak of its own interval.
    private IEnumerable<Measurement<int>> ObserveConnectionsInUse() =>
        _poolStates.Values.Select(poolState =>
            new Measurement<int>(poolState.ExchangePeakConnectionsInUse(), poolState.CreateTags()));

    private IEnumerable<Measurement<int>> ObservePendingRequests() =>
        _poolStates.Values.Select(poolState =>
            new Measurement<int>(poolState.ExchangePeakPendingRequests(), poolState.CreateTags()));

    private IEnumerable<Measurement<int>> ObserveMaxConnections() =>
        _poolStates.Values
            .Where(poolState => poolState.MaxPoolSize > 0)
            .Select(poolState => new Measurement<int>(poolState.MaxPoolSize, poolState.CreateTags()));

    private void RecordWaitTime(PoolState poolState, TimeSpan waited, string? failureReason)
    {
        var tags = poolState.CreateTags();

        if (failureReason is not null)
        {
            tags.Add("db.client.connection.checkout.failure_reason", failureReason);
        }

        _connectionWaitTime.Record(waited.TotalMilliseconds, tags);
    }

    private void Report(TimeSpan waited, int connectionsInUse, int maxPoolSize, string? failureReason)
    {
        var exhausted = maxPoolSize > 0 && connectionsInUse >= maxPoolSize;

        AddSpan(waited, connectionsInUse, maxPoolSize, failureReason);

        if (failureReason is not null)
        {
            _logger.CheckoutFailed(_connectionInstanceName, waited.TotalMilliseconds, failureReason, connectionsInUse, maxPoolSize);
        }
        else if (exhausted)
        {
            _logger.CheckoutWaitedForExhaustedPool(_connectionInstanceName, waited.TotalMilliseconds, maxPoolSize);
        }
    }

    private static void AddSpan(TimeSpan waited, int connectionsInUse, int maxPoolSize, string? failureReason)
    {
        var endTime = DateTimeOffset.UtcNow;

        // The span is created after the fact, its start time is calculated back from the duration reported by the driver.
        using var activity = ActivitySource.StartActivity(
            CheckoutActivityName,
            ActivityKind.Client,
            Activity.Current?.Context ?? default,
            startTime: endTime - waited);

        if (activity is null)
        {
            return;
        }

        activity.SetTag("db.client.connection.wait_time_ms", waited.TotalMilliseconds);
        activity.SetTag("db.client.connection.count", connectionsInUse);
        activity.SetTag("db.client.connection.max", maxPoolSize);

        if (failureReason is not null)
        {
            activity.SetTag("db.client.connection.checkout.failure_reason", failureReason);
            activity.SetStatus(ActivityStatusCode.Error, $"Checking out a MongoDb connection failed: {failureReason}.");
        }

        activity.SetEndTime(endTime.UtcDateTime);
    }

    /// <summary>
    /// State of the pool and wait queue of a single server of the client. The counters and the high water marks of a server are
    /// kept apart from those of the other servers, the measurements of the server are tagged with
    /// <see cref="Tags"/> so they form a series of their own.
    /// </summary>
    private sealed class PoolState
    {
        private readonly TimeSpan _idleAfterNoRequests;
        private readonly TagList _idleTags;
        private readonly TagList _busyTags;

        private int _connectionsInUse;
        private int _pendingRequests;
        private int _maxPoolSize;
        private int _peakConnectionsInUse;
        private int _peakPendingRequests;
        private long _lastRequestTimestamp;

        public PoolState(ServerId serverId, string connectionName, TimeSpan idleAfterNoRequests)
        {
            _idleAfterNoRequests = idleAfterNoRequests;

            // The server id itself is not used as a tag: it contains the cluster id, a counter that starts over on
            // every start of the process, which would create a new series on every restart. The endpoint of the
            // server is the stable part of it.
            var tags = new TagList { { "db.client.connection.name", connectionName } };

            switch (serverId.EndPoint)
            {
                case DnsEndPoint dnsEndPoint:
                    tags.Add("db.server.connection.address", dnsEndPoint.Host);
                    tags.Add("db.server.connection.port", dnsEndPoint.Port);
                    break;
                case IPEndPoint ipEndPoint:
                    tags.Add("db.server.connection.address", ipEndPoint.Address.ToString());
                    tags.Add("db.server.connection.port", ipEndPoint.Port);
                    break;
                default:
                    tags.Add("db.server.connection.address", serverId.EndPoint.ToString());
                    break;
            }

            Tags = tags;

            // The idle state is the only part of the tags that changes, and it only has two possible values. Both
            // variants are built once here so that reporting a measurement is a copy of a readonly struct instead of
            // building the tag list over again on every single measurement.
            var idleTags = tags;
            idleTags.Add(IdleTagName, true);
            _idleTags = idleTags;

            var busyTags = tags;
            busyTags.Add(IdleTagName, false);
            _busyTags = busyTags;
        }

        /// <summary>
        /// Tags identifying the pool, they are added to every measurement reported for this server.
        /// </summary>
        public TagList Tags
        {
            get;
        }

        /// <summary>
        /// Whether the pool is idle: no connection is in use, no request is waiting in the wait queue and no
        /// checkout request has been received within the configured inactivity window.
        /// </summary>
        public bool IsIdle => ConnectionsInUse == 0 && PendingRequests == 0 && HasNotSeenRequestsRecently();

        /// <summary>
        /// Tags of a measurement: the tags identifying the pool plus the current idle state of the pool. Both
        /// variants are prepared upfront, the caller receives a copy of the matching one.
        /// </summary>
        public TagList CreateTags() => IsIdle ? _idleTags : _busyTags;

        public int MaxPoolSize => Volatile.Read(ref _maxPoolSize);

        public int ConnectionsInUse => Volatile.Read(ref _connectionsInUse);

        public int PendingRequests => Volatile.Read(ref _pendingRequests);

        /// <summary>
        /// Stores the maximum pool size the first time it is reported, later reports are ignored.
        /// </summary>
        /// <returns><c>true</c> when the maximum pool size was set by this call.</returns>
        public bool TrySetMaxPoolSize(int value) => Interlocked.CompareExchange(ref _maxPoolSize, value, 0) == 0;

        /// <summary>
        /// Remembers the moment a checkout request was received, the pool is not reported as idle until the
        /// configured inactivity window has passed since then.
        /// </summary>
        public void RecordRequest() => Volatile.Write(ref _lastRequestTimestamp, Stopwatch.GetTimestamp());

        public int Acquire() => Interlocked.Increment(ref _connectionsInUse);

        public void BeginCheckout() => Interlocked.Increment(ref _pendingRequests);

        public bool TryEndCheckout() => TryDecrement(ref _pendingRequests);

        /// <summary>
        /// Lowers the number of connections in use. Check-ins of connections whose checkout was not seen, for
        /// instance connections that were already in use before this subscriber started following the pool, are
        /// ignored so the number of connections in use can never become negative.
        /// </summary>
        /// <returns><c>true</c> when the number of connections in use was lowered by this call.</returns>
        public bool TryRelease() => TryDecrement(ref _connectionsInUse);

        public void TrackPeakConnectionsInUse() => TrackPeak(ref _peakConnectionsInUse, ConnectionsInUse);

        public void TrackPeakPendingRequests() => TrackPeak(ref _peakPendingRequests, PendingRequests);

        /// <summary>
        /// Reads the highest number of connections that were in use since the previous collection and starts the
        /// next interval at the number of connections that is in use right now.
        /// </summary>
        public int ExchangePeakConnectionsInUse() => Interlocked.Exchange(ref _peakConnectionsInUse, ConnectionsInUse);

        /// <summary>
        /// Reads the highest number of requests that were waiting for a connection since the previous collection and
        /// starts the next interval at the number of requests that is waiting right now.
        /// </summary>
        public int ExchangePeakPendingRequests() => Interlocked.Exchange(ref _peakPendingRequests, PendingRequests);

        private bool HasNotSeenRequestsRecently()
        {
            var lastRequestTimestamp = Volatile.Read(ref _lastRequestTimestamp);

            // No request has ever been received, a pool that was never used is idle from the moment it is opened.
            if (lastRequestTimestamp == 0)
            {
                return true;
            }

            return Stopwatch.GetElapsedTime(lastRequestTimestamp) >= _idleAfterNoRequests;
        }

        private static void TrackPeak(ref int peak, int value)
        {
            var currentPeak = Volatile.Read(ref peak);

            while (value > currentPeak)
            {
                var current = Interlocked.CompareExchange(ref peak, value, currentPeak);

                if (current == currentPeak)
                {
                    return;
                }

                currentPeak = current;
            }
        }

        private static bool TryDecrement(ref int value)
        {
            while (true)
            {
                var currentValue = Volatile.Read(ref value);
                if (currentValue == 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref value, currentValue - 1, currentValue) == currentValue)
                {
                    return true;
                }
            }
        }
    }
}
