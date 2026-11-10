namespace Backdafuckup.Server.Telemetry;

/// <summary>The Prometheus scrape endpoint.</summary>
public static class MetricsEndpoint
{
    public const string Path = "/metrics";

    /// <summary>Signed-in users, full API tokens and metrics-only API tokens.</summary>
    public const string Policy = "Metrics";
}
