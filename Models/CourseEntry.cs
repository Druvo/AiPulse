namespace AiPulse.Models;

/// <summary>A curated free course entry for the Learning Hub's "Free courses" section.</summary>
public sealed class CourseEntry
{
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public required string Url { get; init; }
    public required string Level { get; init; } // Beginner/Intermediate/Advanced, matches LearningModule.Level
    public required string OneLiner { get; init; }
    public bool HasFreeCertificate { get; init; }
    public string CertificateNote { get; init; } = "";
    public string[] Topics { get; init; } = Array.Empty<string>();
}
