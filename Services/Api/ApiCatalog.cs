using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiPulse.Services.Api;

public sealed record ApiParameterDoc(
    string Name, string In, string Type, string Description, bool Required = false, string? Format = null,
    int? Minimum = null, int? Maximum = null, string? Default = null, string? Example = null, IReadOnlyList<string>? Enum = null);

public enum ApiShape { Object, List, Paged }

/// <summary>One endpoint, described once: the route table, the OpenAPI document and the /developers page are all built from these.</summary>
public sealed record ApiEndpointDoc(
    string OperationId, string Path, string Tag, string Summary, string Description, Type? DataType, ApiShape Shape,
    bool RequiresToken, IReadOnlyList<ApiParameterDoc> Parameters, ApiCache Cache, string Example)
{
    public string FullPath => PublicApiEndpoints.BasePath + (Path == "/" ? "" : Path);
    public bool HasPathParameter => Parameters.Any(p => p.In == "path");
}

/// <summary>The v1 endpoint catalogue and a hand-built OpenAPI 3.0 document generated from it (no runtime OpenAPI dependency).</summary>
public static class ApiCatalog
{
    public const string ApiVersion = "1.0.0";

    private static readonly ApiParameterDoc Page = new("page", "query", "integer", "1-based page number.", Minimum: 1, Maximum: ApiQuery.MaxPage, Default: "1");
    private static readonly ApiParameterDoc PageSize = new("pageSize", "query", "integer", "Items per page.", Minimum: 1, Maximum: ApiQuery.MaxPageSize, Default: ApiQuery.DefaultPageSize.ToString());
    private static readonly ApiParameterDoc Q = new("q", "query", "string", "Free text. Every word must appear in the title, summary, source, tags or author (case-insensitive).", Example: "agents");
    private static readonly ApiParameterDoc Source = new("source", "query", "string", "Source name, exact (case-insensitive). Repeat or comma-separate for several.", Example: "Hugging Face Blog");
    private static readonly ApiParameterDoc Since = new("since", "query", "string", "Only items published at or after this instant: ISO 8601 (2026-11-01, 2026-11-01T08:00:00Z, UTC when no offset) or a relative span (90m, 24h, 7d, 2w).", Example: "24h");
    private static readonly ApiParameterDoc Until = new("until", "query", "string", "Only items published at or before this instant. Same formats as since.");
    private static readonly ApiParameterDoc Sort = new("sort", "query", "string", "Order by publish time.", Default: "newest", Enum: new[] { "newest", "oldest" });

    private static readonly ApiParameterDoc[] NewsFilters =
    {
        Q,
        new("tag", "query", "string", "Topic tag, exact (case-insensitive).", Example: "RAG"),
        Source,
        new("category", "query", "string", "Source category, e.g. News, Research, Community.", Example: "Research"),
        new("type", "query", "string", "Content type, e.g. News, Tutorial, Paper, Discussion, Video. Releases have their own endpoint.", Example: "Paper"),
        new("level", "query", "string", "Beginner, Intermediate or Advanced.", Example: "Beginner"),
        Since, Until, Sort, Page, PageSize
    };

    public static readonly IReadOnlyList<ApiEndpointDoc> Endpoints = new ApiEndpointDoc[]
    {
        new("getIndex", "/", "Meta", "API index", "Links to every endpoint, the docs and the OpenAPI document.",
            null, ApiShape.Object, false, Array.Empty<ApiParameterDoc>(), ApiCache.Reference, "/api/v1"),
        new("listNews", "/news", "News", "Latest headlines",
            "The deduplicated headlines behind the public News Feed, newest first. Release notes are excluded here; use /releases.",
            typeof(NewsItemDto), ApiShape.Paged, false, NewsFilters, ApiCache.News, "/api/v1/news?tag=agents&since=7d&pageSize=5"),
        new("getNewsItem", "/news/{id}", "News", "One headline or release",
            "A single item by its id (the 40-character hex id every news item carries).",
            typeof(NewsItemDto), ApiShape.Object, false,
            new[] { new ApiParameterDoc("id", "path", "string", "Item id from a list response.", Required: true) }, ApiCache.News,
            "/api/v1/news/{id}"),
        new("listTopics", "/topics", "Topics", "Busiest topics",
            "Tags ranked by how many recent headlines carry them, as on /topics.",
            typeof(TopicDto), ApiShape.List, false, new[]
            {
                new ApiParameterDoc("days", "query", "integer", "Look-back window in days.", Minimum: 1, Maximum: 90, Default: "30"),
                new ApiParameterDoc("limit", "query", "integer", "Maximum topics returned.", Minimum: 1, Maximum: 200, Default: "60"),
                new ApiParameterDoc("minCount", "query", "integer", "Leave out tags carried by fewer headlines.", Minimum: 1, Maximum: 1000, Default: "2"),
            }, ApiCache.News, "/api/v1/topics?days=7&limit=10"),
        new("getTopic", "/topics/{tag}", "Topics", "Headlines for one topic",
            "Every saved headline carrying the tag (case-insensitive), newest first, as on /topics/{tag}.",
            typeof(NewsItemDto), ApiShape.Paged, false,
            new[] { new ApiParameterDoc("tag", "path", "string", "The tag, URL-encoded.", Required: true, Example: "agents"), Page, PageSize },
            ApiCache.News, "/api/v1/topics/agents"),
        new("listReleases", "/releases", "Releases", "Release notes",
            "New versions of the libraries, models and tools AiPulse follows, as on /releases.",
            typeof(NewsItemDto), ApiShape.Paged, false, new[] { Q, Source, Since, Until, Sort, Page, PageSize }, ApiCache.News,
            "/api/v1/releases?q=transformers"),
        new("listSources", "/sources", "Sources", "Sources followed",
            "Every enabled source behind the feed with its site (never the raw feed address), as on /coverage.",
            typeof(SourceDto), ApiShape.List, false, new[]
            {
                new ApiParameterDoc("q", "query", "string", "Text in the source name."),
                new ApiParameterDoc("category", "query", "string", "Source category, exact (case-insensitive)."),
                new ApiParameterDoc("type", "query", "string", "Content type the source produces, exact (case-insensitive)."),
            }, ApiCache.Reference, "/api/v1/sources?category=Research"),
        new("listGlossary", "/glossary", "Glossary", "Glossary terms",
            "The AI glossary, alphabetical, as on /glossary.",
            typeof(GlossaryTermDto), ApiShape.List, false, new[]
            {
                new ApiParameterDoc("q", "query", "string", "Text in the term, an alias or the short definition.", Example: "retrieval"),
                new ApiParameterDoc("category", "query", "string", "Concept, Technique, Architecture or Tooling."),
            }, ApiCache.Reference, "/api/v1/glossary?q=retrieval"),
        new("getGlossaryTerm", "/glossary/{slug}", "Glossary", "One glossary term",
            "A term by slug (from a list response), by its exact name, or by one of its aliases.",
            typeof(GlossaryTermDto), ApiShape.Object, false,
            new[] { new ApiParameterDoc("slug", "path", "string", "Slug, term or alias.", Required: true, Example: "rag") },
            ApiCache.Reference, "/api/v1/glossary/rag"),
        new("listTools", "/tools", "Tools", "Tools and when to use them",
            "The tools matrix from /tools: what each is best for, where it falls short, and token-saving tips.",
            typeof(ToolDto), ApiShape.List, false, new[]
            {
                new ApiParameterDoc("q", "query", "string", "Text in the name, one-liner or best-for list."),
                new ApiParameterDoc("category", "query", "string", "e.g. Coding Agent, IDE Assistant, Local Runtime, Framework."),
            }, ApiCache.Reference, "/api/v1/tools?category=Coding%20Agent"),
        new("getTool", "/tools/{slug}", "Tools", "One tool",
            "A tool by slug (from a list response) or exact name.",
            typeof(ToolDto), ApiShape.Object, false,
            new[] { new ApiParameterDoc("slug", "path", "string", "Slug or name.", Required: true) }, ApiCache.Reference, "/api/v1/tools/{slug}"),
        new("search", "/search", "Search", "Search everything",
            "One query across headlines, releases, topics, glossary and tools; best matches first in each section.",
            typeof(SearchResultsDto), ApiShape.Object, false, new[]
            {
                new ApiParameterDoc("q", "query", "string", $"What to look for (at least {ApiQuery.MinSearchLength} characters).", Required: true, Example: "mixture of experts"),
                new ApiParameterDoc("limit", "query", "integer", "Maximum results per section.", Minimum: 1, Maximum: 20, Default: "5"),
            }, ApiCache.News, "/api/v1/search?q=mixture%20of%20experts&limit=3"),
        new("getStats", "/stats", "Meta", "Site numbers",
            "Counts the public pages quote: sources, glossary terms, tools, headlines in the last 24 hours, last poll.",
            typeof(StatsDto), ApiShape.Object, false, Array.Empty<ApiParameterDoc>(), ApiCache.News, "/api/v1/stats"),
        new("getStatus", "/status", "Meta", "Service status",
            "Liveness for monitors: database reachable and whether the feed has been polled recently. 503 when the database is down. Never cached.",
            typeof(StatusDto), ApiShape.Object, false, Array.Empty<ApiParameterDoc>(), ApiCache.NoStore, "/api/v1/status"),
        new("getOpenApi", "/openapi.json", "Meta", "OpenAPI document",
            "This API described as OpenAPI 3.0.",
            null, ApiShape.Object, false, Array.Empty<ApiParameterDoc>(), ApiCache.Document, "/api/v1/openapi.json"),
        new("getMe", "/me", "Account", "The token's account",
            "Who the token belongs to, the token's own details, and reading counts. Requires a token.",
            typeof(MeDto), ApiShape.Object, true, Array.Empty<ApiParameterDoc>(), ApiCache.Private, "/api/v1/me"),
        new("listBookmarks", "/me/bookmarks", "Account", "Your bookmarks",
            "Your Reading List, most recently saved first. Read-only. Requires a token.",
            typeof(BookmarkDto), ApiShape.Paged, true, new[] { new ApiParameterDoc("q", "query", "string", "Text in the title, source or tags."), Page, PageSize },
            ApiCache.Private, "/api/v1/me/bookmarks"),
        new("listUnread", "/me/unread", "Account", "Your unread headlines",
            "Headlines you have not marked as read, with the same filters as /news. Read-only. Requires a token.",
            typeof(NewsItemDto), ApiShape.Paged, true, NewsFilters, ApiCache.Private, "/api/v1/me/unread?since=24h"),
    };

    public static ApiEndpointDoc Get(string operationId) => Endpoints.First(e => e.OperationId == operationId);

    // ---------------------------------------------------------------- OpenAPI

    private static readonly Type[] SchemaTypes =
    {
        typeof(NewsItemDto), typeof(TopicDto), typeof(SourceDto), typeof(GlossaryTermDto), typeof(ToolDto), typeof(StatsDto),
        typeof(StatusDto), typeof(SearchResultsDto), typeof(BookmarkDto), typeof(TokenSummaryDto), typeof(MeDto)
    };

    public static string SchemaName(Type t) => t.Name.EndsWith("Dto", StringComparison.Ordinal) ? t.Name[..^3] : t.Name;

    public static JsonObject OpenApi(string origin)
    {
        var paths = new JsonObject();
        foreach (var e in Endpoints)
        {
            var operation = new JsonObject
            {
                ["operationId"] = e.OperationId,
                ["tags"] = new JsonArray(e.Tag),
                ["summary"] = e.Summary,
                ["description"] = e.Description,
                ["parameters"] = new JsonArray(e.Parameters.Select(ParameterNode).ToArray<JsonNode?>()),
                ["responses"] = Responses(e)
            };
            operation["security"] = e.RequiresToken
                ? new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() })
                : new JsonArray(new JsonObject(), new JsonObject { ["bearerAuth"] = new JsonArray() });
            paths[e.Path] = new JsonObject { ["get"] = operation };
        }

        var schemas = new JsonObject();
        foreach (var t in SchemaTypes) schemas[SchemaName(t)] = ObjectSchema(t);
        schemas["PageMeta"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("page", "pageSize", "total", "totalPages"),
            ["properties"] = new JsonObject
            {
                ["page"] = new JsonObject { ["type"] = "integer" },
                ["pageSize"] = new JsonObject { ["type"] = "integer" },
                ["total"] = new JsonObject { ["type"] = "integer" },
                ["totalPages"] = new JsonObject { ["type"] = "integer" }
            },
            ["additionalProperties"] = true
        };
        schemas["Links"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("self"),
            ["properties"] = new JsonObject
            {
                ["self"] = Str("uri"), ["first"] = Str("uri"), ["prev"] = Str("uri"), ["next"] = Str("uri"), ["last"] = Str("uri")
            }
        };
        schemas["Problem"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "RFC 7807 problem details (served as application/problem+json).",
            ["required"] = new JsonArray("type", "title", "status", "detail", "instance"),
            ["properties"] = new JsonObject
            {
                ["type"] = Str("uri"),
                ["title"] = new JsonObject { ["type"] = "string" },
                ["status"] = new JsonObject { ["type"] = "integer" },
                ["detail"] = new JsonObject { ["type"] = "string" },
                ["instance"] = new JsonObject { ["type"] = "string" },
                ["traceId"] = new JsonObject { ["type"] = "string" },
                ["errors"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                }
            }
        };

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = "AiPulse API",
                ["version"] = ApiVersion,
                ["description"] = "Read-only JSON API over AiPulse's deduplicated AI news, topics, releases, sources, glossary and tools. " +
                                  "Public data needs no key. Your own bookmarks and unread items need a personal token (Settings > API tokens). " +
                                  $"Guide: {origin}/developers"
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = origin + PublicApiEndpoints.BasePath }),
            ["tags"] = new JsonArray(Endpoints.Select(e => e.Tag).Distinct().Select(t => (JsonNode?)new JsonObject { ["name"] = t }).ToArray()),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["schemas"] = schemas,
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerAuth"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["scheme"] = "bearer",
                        ["description"] = "A personal API token (aip_...) created under Settings > API tokens. Scope: read."
                    }
                },
                ["headers"] = new JsonObject
                {
                    ["X-RateLimit-Limit"] = RateHeader("X-RateLimit-Limit", "Requests allowed in the current window."),
                    ["X-RateLimit-Remaining"] = RateHeader("X-RateLimit-Remaining", "Requests left in the current window."),
                    ["X-RateLimit-Reset"] = RateHeader("X-RateLimit-Reset", "Seconds until the window resets."),
                    ["Retry-After"] = RateHeader("Retry-After", "Seconds to wait before retrying."),
                    ["ETag"] = new JsonObject { ["description"] = "Weak validator; send it back as If-None-Match to get 304 Not Modified.", ["schema"] = new JsonObject { ["type"] = "string" } }
                }
            }
        };
    }

    private static JsonObject RateHeader(string name, string description) =>
        new() { ["description"] = description, ["schema"] = new JsonObject { ["type"] = "integer" } };

    private static JsonObject Str(string? format = null)
    {
        var o = new JsonObject { ["type"] = "string" };
        if (format is not null) o["format"] = format;
        return o;
    }

    private static JsonObject ParameterNode(ApiParameterDoc p)
    {
        var schema = new JsonObject { ["type"] = p.Type };
        if (p.Format is not null) schema["format"] = p.Format;
        if (p.Minimum is { } min) schema["minimum"] = min;
        if (p.Maximum is { } max) schema["maximum"] = max;
        if (p.Default is not null) schema["default"] = p.Type == "integer" && int.TryParse(p.Default, out var d) ? JsonValue.Create(d) : JsonValue.Create(p.Default);
        if (p.Enum is not null) schema["enum"] = new JsonArray(p.Enum.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        var node = new JsonObject
        {
            ["name"] = p.Name,
            ["in"] = p.In,
            ["required"] = p.Required || p.In == "path",
            ["description"] = p.Description,
            ["schema"] = schema
        };
        if (p.Example is not null) node["example"] = p.Example;
        return node;
    }

    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };

    private static JsonObject Responses(ApiEndpointDoc e)
    {
        JsonNode data = e.DataType is null
            ? new JsonObject { ["type"] = "object" }
            : e.Shape == ApiShape.Object ? Ref(SchemaName(e.DataType)) : new JsonObject { ["type"] = "array", ["items"] = Ref(SchemaName(e.DataType)) };

        JsonNode okSchema = e.OperationId == "getOpenApi"
            ? new JsonObject { ["type"] = "object", ["description"] = "An OpenAPI 3.0 document (not wrapped in the envelope)." }
            : new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("data", "meta", "links"),
                ["properties"] = new JsonObject
                {
                    ["data"] = data,
                    ["meta"] = e.Shape == ApiShape.Paged ? Ref("PageMeta") : new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                    ["links"] = Ref("Links")
                }
            };

        var okHeaders = new JsonObject
        {
            ["X-RateLimit-Limit"] = new JsonObject { ["$ref"] = "#/components/headers/X-RateLimit-Limit" },
            ["X-RateLimit-Remaining"] = new JsonObject { ["$ref"] = "#/components/headers/X-RateLimit-Remaining" },
            ["X-RateLimit-Reset"] = new JsonObject { ["$ref"] = "#/components/headers/X-RateLimit-Reset" }
        };
        if (e.Cache.UseETag) okHeaders["ETag"] = new JsonObject { ["$ref"] = "#/components/headers/ETag" };

        var responses = new JsonObject
        {
            ["200"] = new JsonObject
            {
                ["description"] = "OK",
                ["headers"] = okHeaders,
                ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = okSchema } }
            }
        };
        if (e.Cache.UseETag) responses["304"] = new JsonObject { ["description"] = "Not Modified (the If-None-Match ETag still matches)." };
        if (e.Parameters.Any(p => p.In == "query")) responses["400"] = Problem("A query parameter is not valid.");
        responses["401"] = Problem(e.RequiresToken ? "No token, or the token is invalid, expired or revoked." : "A token was sent but is invalid, expired or revoked.");
        if (e.HasPathParameter) responses["404"] = Problem("Nothing with that id.");
        responses["429"] = new JsonObject
        {
            ["description"] = "Rate limit exceeded.",
            ["headers"] = new JsonObject { ["Retry-After"] = new JsonObject { ["$ref"] = "#/components/headers/Retry-After" } },
            ["content"] = new JsonObject { ["application/problem+json"] = new JsonObject { ["schema"] = Ref("Problem") } }
        };
        if (e.OperationId == "getStatus") responses["503"] = new JsonObject { ["description"] = "The database is not reachable." };
        return responses;
    }

    private static JsonObject Problem(string description) => new()
    {
        ["description"] = description,
        ["content"] = new JsonObject { ["application/problem+json"] = new JsonObject { ["schema"] = Ref("Problem") } }
    };

    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>Schema for a DTO by reflection, so the document cannot drift from what is actually serialized.</summary>
    public static JsonObject ObjectSchema(Type type)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetMethod is not null && p.GetIndexParameters().Length == 0))
        {
            if (p.Name == "EqualityContract") continue;
            var name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            var nullable = Nullability.Create(p).ReadState == NullabilityState.Nullable;
            props[name] = TypeSchema(p.PropertyType, nullable);
            required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["required"] = required, ["properties"] = props };
    }

    private static JsonNode TypeSchema(Type t, bool nullable)
    {
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying is not null) { t = underlying; nullable = true; }

        JsonObject schema;
        if (t == typeof(string)) schema = new JsonObject { ["type"] = "string" };
        else if (t == typeof(int) || t == typeof(long)) schema = new JsonObject { ["type"] = "integer" };
        else if (t == typeof(bool)) schema = new JsonObject { ["type"] = "boolean" };
        else if (t == typeof(double)) schema = new JsonObject { ["type"] = "number" };
        else if (t == typeof(DateTimeOffset) || t == typeof(DateTime)) schema = new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        else if (t.IsArray || (t.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(t)))
        {
            var element = t.IsArray ? t.GetElementType()! : t.GetGenericArguments()[0];
            schema = new JsonObject { ["type"] = "array", ["items"] = TypeSchema(element, false) };
        }
        else if (SchemaTypes.Contains(t))
        {
            // $ref cannot carry siblings in OpenAPI 3.0, so a nullable reference is wrapped in allOf.
            return nullable
                ? new JsonObject { ["nullable"] = true, ["allOf"] = new JsonArray(Ref(SchemaName(t))) }
                : Ref(SchemaName(t));
        }
        else schema = new JsonObject { ["type"] = "object" };

        if (nullable) schema["nullable"] = true;
        return schema;
    }
}
