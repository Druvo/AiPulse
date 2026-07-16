namespace AiPulse.Models;

/// <summary>Progress while importing an OPML file - reachability-checking each new URL is the slow part.</summary>
public sealed class OpmlImportProgress
{
    public int Done { get; init; }
    public int Total { get; init; }
    public string? CurrentName { get; init; }

    public int PercentComplete => Total > 0 ? (int)(100.0 * Done / Total) : 0;
}
