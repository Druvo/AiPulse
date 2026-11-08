using System.Text;
using AiPulse.Models;

namespace AiPulse.Services.Api;

// Wire shapes for /api/v1. Field names are part of the public contract: serialized camelCase, every field always present (null
// when unknown) so clients can rely on the shape. Add fields freely; never rename or remove one inside v1.

public sealed record NewsItemDto(
    string Id, string Title, string Url, string Summary, DateTimeOffset PublishedAt, string Source, string Category,
    string Type, string Level, IReadOnlyList<string> Tags, IReadOnlyList<string> AlsoSeenOn, string? Author, string? ImageUrl,
    int? ReadingMinutes);

public sealed record TopicDto(string Tag, int Count, string Url, string WebUrl);

public sealed record SourceDto(string Name, string Category, string Type, string Level, string? Site, IReadOnlyList<string> Tags);

public sealed record GlossaryTermDto(
    string Slug, string Term, IReadOnlyList<string> Aliases, string Category, string ShortDefinition, string Details,
    string WhenToUse, IReadOnlyList<string> Related, string? LearnMoreUrl);

public sealed record ToolDto(
    string Slug, string Name, string Category, string OneLiner, IReadOnlyList<string> BestFor, IReadOnlyList<string> NotIdealFor,
    IReadOnlyList<string> TokenTips, string? Url);

public sealed record StatsDto(
    int ActiveSources, int GlossaryTerms, int Tools, int LearningModules, int Headlines, int Releases, int ItemsLast24Hours,
    DateTimeOffset? NewestItemAt, DateTimeOffset? LastPolledAt, int PollMinutes);

public sealed record StatusDto(string Status, string ApiVersion, DateTimeOffset Time, DateTimeOffset? NewestItemAt, DateTimeOffset? LastPolledAt, bool FeedFresh, string Database);

public sealed record SearchResultsDto(
    string Query, IReadOnlyList<NewsItemDto> News, IReadOnlyList<NewsItemDto> Releases, IReadOnlyList<TopicDto> Topics,
    IReadOnlyList<GlossaryTermDto> Glossary, IReadOnlyList<ToolDto> Tools);

public sealed record BookmarkDto(
    string Id, string Title, string Url, string Source, string Category, string Type, string Level, IReadOnlyList<string> Tags,
    DateTimeOffset SavedAt, string? Summary, string? ImageUrl);

public sealed record TokenSummaryDto(string Name, string Prefix, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt);

public sealed record MeDto(int Id, string Username, string Role, TokenSummaryDto Token, int Bookmarks, int ReadItems, DateTimeOffset? LastNewsVisit);

/// <summary>Maps internal models to the wire shapes. Pure.</summary>
public static class ApiMapper
{
    /// <summary>A stable 40-hex id derived from the item's link (same hash the Atom feed uses for its entry ids).</summary>
    public static string ItemId(string link) => PublicFeeds.EntryId(link)["urn:sha1:".Length..];

    public static NewsItemDto News(FeedItem i) => new(
        ItemId(i.Link), i.Title, i.Link, PublicFeeds.Excerpt(i.Summary), i.Published.ToUniversalTime(), i.SourceName, i.Category,
        i.ContentType, i.Level, i.Tags, i.AlsoSeenOn, string.IsNullOrWhiteSpace(i.Author) ? null : i.Author,
        string.IsNullOrWhiteSpace(i.ImageUrl) ? null : i.ImageUrl, i.ReadingMinutes);

    public static TopicDto Topic(TopicCount t, string origin) =>
        new(t.Tag, t.Count, $"{origin}/api/v1/topics/{Uri.EscapeDataString(t.Tag)}", origin + PublicFeeds.TagPath(t.Tag));

    public static SourceDto Source(FeedSource s) => new(s.Name, s.Category, s.ContentType, s.Level, SiteOf(s.Url), s.Tags);

    /// <summary>The source's own site (scheme + host), never the feed address: the same thing /coverage shows.</summary>
    public static string? SiteOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? $"{u.Scheme}://{u.Host}" : null;

    public static GlossaryTermDto Glossary(GlossaryTerm t) => new(
        Slug(t.Term), t.Term, t.Aliases, t.Category, t.ShortDef, t.Details, t.WhenToUse, t.Related, t.LearnMoreUrl);

    public static ToolDto Tool(ToolEntry t) => new(Slug(t.Name), t.Name, t.Category, t.OneLiner, t.BestFor, t.NotIdealFor, t.TokenTips, t.Url);

    public static BookmarkDto Bookmark(BookmarkItem b) => new(
        ItemId(b.Link), b.Title, b.Link, b.SourceName, b.Category, b.ContentType, b.Level, b.Tags, b.SavedAt.ToUniversalTime(),
        b.Summary is null ? null : PublicFeeds.Excerpt(b.Summary), string.IsNullOrWhiteSpace(b.ImageUrl) ? null : b.ImageUrl);

    /// <summary>"Retrieval-Augmented Generation (RAG)" becomes "retrieval-augmented-generation-rag".</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        var dash = false;
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) { sb.Append(c); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }
        return sb.ToString().TrimEnd('-');
    }
}
