namespace MongoConnectionTelemetry.Diagnostics;

/// <summary>
/// Settings for the MongoDb connection pool tracing and metrics.
/// </summary>
public class MongoConnectionPoolTracingOptions
{
    /// <summary>
    /// Name of the configuration section holding these settings.
    /// </summary>
    public const string SectionName = "MongoConnectionPoolTracing";

    /// <summary>
    /// Whether the connection pool checkout information is added to the traces and metrics.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A checkout taking at least this amount of milliseconds is reported as a separate span in the trace.
    ///
    /// Checking out a connection from a pool that has a connection available takes close to no time, so a low
    /// threshold is enough to only report the checkouts that actually waited for a connection to become available.
    /// </summary>
    public int SlowCheckoutThresholdInMilliseconds { get; set; } = 10;
}
