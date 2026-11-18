namespace MongoConnectionTelemetry;

/// <summary>
/// Settings for the MongoDb connection tracing and metrics.
/// </summary>
public class MongoConnectionTracingOptions
{
    /// <summary>
    /// The default configuration section name for MongoDb connection telemetry.
    /// </summary>
    public const string DefaultSectionName = "MongoConnectionTelemetry";

    /// <summary>
    /// Whether the connection checkout information is added to the traces and metrics.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A checkout taking at least this amount of milliseconds is reported as a separate span in the trace.
    ///
    /// Checking out a connection that has a connection available takes close to no time, so a low
    /// threshold is enough to only report the checkouts that actually waited for a connection to become available.
    /// </summary>
    public int SlowCheckoutThresholdInMilliseconds { get; set; } = 10;
}
