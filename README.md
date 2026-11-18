# MongoConnectionTelemetry

OpenTelemetry instrumentation and metrics for MongoDB connection pools in .NET.

## Overview

The standard MongoDB .NET Driver OpenTelemetry instrumentation only creates spans when a command is sent—after a connection has already been checked out of the pool. As a result, time spent waiting for a free connection in the connection pool (such as during pool exhaustion or peak loads) is invisible in traces and appears as an unexplained latency gap before the MongoDB command span starts.

`MongoConnectionTelemetry` closes this observability gap by providing:
- **Connection Checkout Spans**: Emits an OpenTelemetry span for any checkout that exceeds a configured wait-time threshold (`SlowCheckoutThresholdInMilliseconds`), as well as for every failed checkout (e.g. timeouts).
- **Connection Pool Metrics**: Tracks real-time connection usage, peak utilization, queue wait times, and maximum pool size via OpenTelemetry `Meter`.
- **Structured Warnings**: Emits warning logs when checkouts fail or wait for exhausted connection pools.

## Getting Started

### 1. Register Services and Telemetry

Register the tracing options and OpenTelemetry sources with your `IServiceCollection`:

```csharp
using MongoConnectionTelemetry;

// Registers configuration options and OpenTelemetry ActivitySource / Meter
builder.Services.AddMongoConnectionTracing(builder.Configuration);
```

### 2. Configure MongoDB Client Settings

Subscribe the connection pool telemetry when configuring your `MongoClientSettings`:

```csharp
using MongoDB.Driver;
using MongoConnectionTelemetry;

var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");

settings.ClusterConfigurator = clusterBuilder =>
{
    clusterBuilder.SubscribeConnectionTracing(serviceProvider, "my-service-db");
};

var mongoClient = new MongoClient(settings);
```

## Configuration

Settings can be configured in `appsettings.json` under the `MongoConnectionTelemetry` section:

```json
{
  "MongoConnectionTelemetry": {
    "Enabled": true,
    "SlowCheckoutThresholdInMilliseconds": 10
  }
}
```

- `Enabled` (*bool*, default: `true`): Enables or disables connection pool tracing and metrics.
- `SlowCheckoutThresholdInMilliseconds` (*int*, default: `10`): Threshold in milliseconds above which a connection checkout is considered slow and recorded as a span in the trace.

## Exported Telemetry

### Tracing
- **ActivitySource**: `MongoDb.Connection.Telemetry`
- **Activity Name**: `MongoDb connection received from pool`
- **Tags**:
  - `db.client.connection.wait_time_ms`: Wait time in milliseconds.
  - `db.client.connection.count`: Number of connections in use.
  - `db.client.connection.max`: Maximum connection pool size.
  - `db.client.connection.checkout.failure_reason`: Reason for checkout failure (when failed).

### Metrics
- **Meter**: `MongoDb.Connection.Telemetry`
- **Instruments**:
  - `db.client.connection.count` (*ObservableGauge*): Highest number of connections in use since the previous collection.
  - `db.client.connection.wait_queue` (*ObservableGauge*): Highest number of requests waiting for a connection since the previous collection.
  - `db.client.connection.max` (*ObservableUpDownCounter*): Maximum configured pool size.
  - `db.client.connection.wait_time` (*Histogram*): Time spent waiting to obtain a connection from the pool.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
