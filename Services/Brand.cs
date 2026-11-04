namespace AiPulse.Services;

/// <summary>
/// The product's public name and one-line pitch, in one place. The project, namespaces, repository, service name and domain keep the
/// internal name "AiPulse" (renaming those would break deployments, data folders and links); only what people read uses these.
/// </summary>
public static class Brand
{
    public const string Name = "Lumen";
    public const string Tagline = "AI news, tools and learning in one clear view";
    public const string Description = "Lumen is a self-hosted dashboard that keeps developers current on the AI world: deduplicated news, a glossary, tool comparisons, a learning path and grounded Q&A.";
}
