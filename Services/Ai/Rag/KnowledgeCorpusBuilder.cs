using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AiPulse.Models;

namespace AiPulse.Services.Ai.Rag;

public sealed record KnowledgeInputs(
    IEnumerable<GlossaryTerm> Glossary,
    IEnumerable<ToolEntry> Tools,
    IEnumerable<PracticeTip> Practices,
    IEnumerable<LearningModule> Learning,
    IEnumerable<BenchmarkEntry> Benchmarks,
    IEnumerable<CourseEntry> Courses,
    IEnumerable<ModelDirectoryEntry> Models,
    IEnumerable<FeedItem> News);

/// <summary>Turns AiPulse's curated knowledge base and recent feed items into <see cref="KnowledgeDocument"/>s. Pure: no I/O, so it can be tested directly.</summary>
public static class KnowledgeCorpusBuilder
{
    private const int MaxSummaryChars = 1200;
    private const int MaxFullTextChars = 800;

    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static List<KnowledgeDocument> Build(KnowledgeInputs input)
    {
        var docs = new List<KnowledgeDocument>();

        foreach (var g in input.Glossary)
            docs.Add(new KnowledgeDocument(
                $"glossary:{Slug(g.Term)}", "Glossary", g.Term,
                Join(g.ShortDef, g.Details, Label("When to use", g.WhenToUse), g.Related.Length > 0 ? "Related: " + string.Join(", ", g.Related) + "." : ""),
                $"/glossary?q={Uri.EscapeDataString(g.Term)}",
                Keywords: string.Join(' ', g.Aliases)));

        foreach (var t in input.Tools)
            docs.Add(new KnowledgeDocument(
                $"tool:{Slug(t.Name)}", "Tool", t.Name,
                Join($"{t.Category}.", t.OneLiner,
                    t.BestFor.Length > 0 ? "Best for: " + string.Join("; ", t.BestFor) + "." : "",
                    t.NotIdealFor.Length > 0 ? "Not ideal for: " + string.Join("; ", t.NotIdealFor) + "." : "",
                    t.TokenTips.Length > 0 ? "Token tips: " + string.Join("; ", t.TokenTips) + "." : ""),
                t.Url ?? "/tools"));

        foreach (var p in input.Practices)
            docs.Add(new KnowledgeDocument($"practice:{Slug(p.Title)}", "Practice", p.Title, Join($"{p.Category}.", p.Body), "/tools"));

        foreach (var m in input.Learning)
            docs.Add(new KnowledgeDocument(
                $"learning:{Slug(m.Title)}", "Learning", m.Title,
                Join($"{m.Level} level.", Label("Why it matters", m.Why), m.Steps.Length > 0 ? "Steps: " + string.Join(" ", m.Steps.Select((s, i) => $"{i + 1}. {s.Text}")) : ""),
                "/learn",
                Keywords: string.Join(' ', m.RelatedTerms)));

        foreach (var b in input.Benchmarks)
            docs.Add(new KnowledgeDocument($"benchmark:{Slug(b.Name)}", "Benchmark", b.Name, Join($"{b.Category}.", b.Description), b.Url));

        foreach (var c in input.Courses)
            docs.Add(new KnowledgeDocument(
                $"course:{Slug(c.Name)}", "Course", c.Name,
                Join($"By {c.Provider}.", $"{c.Level} level.", c.OneLiner, c.HasFreeCertificate ? "Free certificate. " + c.CertificateNote : ""),
                c.Url,
                Keywords: string.Join(' ', c.Topics)));

        foreach (var m in input.Models)
            docs.Add(new KnowledgeDocument(
                $"model:{Slug(m.Name)}", "Model", m.Name,
                Join($"By {m.Publisher}.", m.OneLiner, $"Category: {m.Category}.",
                    string.IsNullOrWhiteSpace(m.License) ? "" : $"License: {m.License}.",
                    m.SizeGb > 0 ? $"Download size about {m.SizeGb.ToString("0.##", CultureInfo.InvariantCulture)} GB." : "",
                    m.MinRamGb > 0 ? $"Needs at least {m.MinRamGb.ToString("0.##", CultureInfo.InvariantCulture)} GB of RAM." : ""),
                m.OllamaUrl ?? m.HuggingFaceUrl ?? "/explore"));

        foreach (var n in input.News)
        {
            if (string.IsNullOrWhiteSpace(n.Link)) continue;

            var summary = Clip(Clean(n.Summary), MaxSummaryChars);
            var full = string.IsNullOrWhiteSpace(n.FullText) ? "" : Clip(Clean(n.FullText), MaxFullTextChars);
            var when = n.Published == default
                ? ""
                : $"Published {n.Published.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} ({n.Published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}).";

            docs.Add(new KnowledgeDocument(
                $"news:{StableHash(n.Link)}", "News", n.Title,
                Join(summary, full, when, $"Source: {n.SourceName}."),
                n.Link,
                Keywords: string.Join(' ', n.Tags),
                Published: n.Published == default ? null : n.Published,
                Source: n.SourceName));
        }

        return docs;
    }

    private static string Label(string label, string value) => string.IsNullOrWhiteSpace(value) ? "" : $"{label}: {value}";

    private static string Join(params string[] parts) =>
        string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

    private static string Clean(string html) => Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(html ?? "", " ")), " ").Trim();

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static string Slug(string value) =>
        Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static string StableHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
