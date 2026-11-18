using Microsoft.Extensions.Logging;

namespace MongoConnectionTelemetry;

/// <summary>
/// Source generated log messages for the MongoDb connection diagnostics.
/// </summary>
internal static partial class MongoConnectionLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Checking out a MongoDb connection from the `{PoolName}` pool failed after {WaitedInMilliseconds}ms, reason: {FailureReason}, {ConnectionsInUse} of a maximum of {MaxPoolSize} connections are in use")]
    public static partial void CheckoutFailed(
        this ILogger logger,
        string poolName,
        double waitedInMilliseconds,
        string failureReason,
        int connectionsInUse,
        int maxPoolSize);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Checking out a MongoDb connection from the `{PoolName}` pool took {WaitedInMilliseconds}ms because all {MaxPoolSize} connections of the pool are in use")]
    public static partial void CheckoutWaitedForExhaustedPool(
        this ILogger logger,
        string poolName,
        double waitedInMilliseconds,
        int maxPoolSize);
}
