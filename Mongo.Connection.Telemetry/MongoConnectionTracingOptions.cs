namespace Mongo.Connection.Telemetry;

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

    /// <summary>
    /// A pool is only reported as idle when it has not received a checkout request for at least this amount of
    /// seconds, on top of having no connection in use and no request waiting in the wait queue.
    ///
    /// A pool that is only used now and then would otherwise flip between idle and busy between two collections,
    /// the window keeps it marked as busy until it has really been without traffic for a while. A value of zero
    /// reports the pool as idle as soon as nothing is in use and nothing is waiting.
    /// </summary>
    public int IdleAfterNoRequestsInSeconds { get; set; } = 60;
}
