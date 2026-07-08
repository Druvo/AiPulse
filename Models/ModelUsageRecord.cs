namespace AiPulse.Models;

/// <summary>Tracks a model the user has pulled or chatted with, so it can be offered as a quick
/// "pull again" shortcut even after they've deleted it locally to free up disk space.</summary>
public sealed class ModelUsageRecord
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}
