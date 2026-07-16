namespace AiPulse.Models;

/// <summary>Point-in-time health summary for one source, backing the Source Health dashboard.</summary>
public sealed class SourceHealthSnapshot
{
    public required string SourceName { get; init; }
    public int HealthyDays { get; init; }
    public int TotalDays { get; init; }
    public DateTimeOffset? LastAttempt { get; init; }
    public bool? LastSuccess { get; init; }
    public string? LastError { get; init; }
    public double? LastResponseMs { get; init; }
    public double? AvgResponseMs { get; init; }

    /// <summary>Null (not just 0%) when nothing has been recorded yet - distinct from "always failing".</summary>
    public double? UptimePercent => TotalDays > 0 ? 100.0 * HealthyDays / TotalDays : null;
}
