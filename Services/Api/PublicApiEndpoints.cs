using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiPulse.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace AiPulse.Services.Api;

/// <summary>Everything the endpoints need, assembled once. Tests build one over fixed data and a temp database.</summary>
public sealed class PublicApiRuntime
{
    public required IPublicApiData Data { get; init; }
    public required ApiTokenService Tokens { get; init; }
    public required ApiRateLimiter Limiter { get; init; }
    public PublicApiOptions Options { get; init; } = new();
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public ILogger? Logger { get; init; }

    public ApiRateLimitPolicy AnonymousPolicy => new("anonymous", Math.Max(1, Options.AnonymousPermitLimit), TimeSpan.FromSeconds(Math.Max(1, Options.WindowSeconds)));
    public ApiRateLimitPolicy TokenPolicy => new("token", Math.Max(1, Options.TokenPermitLimit), TimeSpan.FromSeconds(Math.Max(1, Options.WindowSeconds)));
}

/// <summary>Per-request context handed to each handler.</summary>
public sealed record ApiRequest(HttpContext Http, PublicApiRuntime Runtime, ApiEndpointDoc Doc, ApiPrincipal? Principal, DateTimeOffset Now)
{
    public IQueryCollection Query => Http.Request.Query;
    public string Origin => ApiHttp.Origin(Http.Request);
    public IPublicApiData Data => Runtime.Data;
    public string Route(string name) => Http.GetRouteValue(name)?.ToString() ?? "";
}

/// <summary>
/// The read-only JSON API under /api/v1 (see <see cref="AiPulse.Services.PublicApiAppExtensions"/> for the one-line wiring). Every response uses the same envelope ({ data, meta, links }) or an RFC 7807 problem, carries X-RateLimit-* headers, and public
/// data is CORS-open for GET, ETag'd and compressed.
/// </summary>
public static class PublicApiEndpoints
{
    public const string BasePath = "/api/v1";
    public const string ExposedHeaders = "ETag, Link, Retry-After, X-RateLimit-Limit, X-RateLimit-Remaining, X-RateLimit-Reset";

    public static RouteGroupBuilder MapPublicApi(this IEndpointRouteBuilder endpoints, PublicApiRuntime runtime)
    {
        var group = endpoints.MapGroup(BasePath);
        var handlers = Handlers();
        foreach (var doc in ApiCatalog.Endpoints)
        {
            if (!handlers.TryGetValue(doc.OperationId, out var handler))
                throw new InvalidOperationException($"No handler for API operation {doc.OperationId}.");
            group.MapMethods(doc.Path, new[] { HttpMethods.Get, HttpMethods.Head }, (HttpContext ctx) => ExecuteAsync(ctx, runtime, doc, handler))
                .WithName("api-v1-" + doc.OperationId)
                .ExcludeFromDescription();
        }

        // CORS preflight: browsers send one before a cross-origin GET carrying If-None-Match or Authorization.
        group.MapMethods("/{**rest}", new[] { HttpMethods.Options }, (HttpContext ctx) =>
        {
            var path = "/" + (ctx.GetRouteValue("rest")?.ToString() ?? "");
            if (!IsPrivatePath(path)) AddCors(ctx.Response, preflight: true);
            ctx.Response.Headers.Allow = "GET, HEAD, OPTIONS";
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }).ExcludeFromDescription();

        // Anything else under /api/v1 answers in the API's own problem format instead of falling through to the HTML site.
        group.MapMethods("/{**rest}", new[] { HttpMethods.Get, HttpMethods.Head, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete },
            (HttpContext ctx) =>
            {
                AddCors(ctx.Response);
                var path = "/" + (ctx.GetRouteValue("rest")?.ToString() ?? "");
                var known = ApiCatalog.Endpoints.Any(e => TemplateMatches(e.Path, path));
                if (known && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
                {
                    ctx.Response.Headers.Allow = "GET, HEAD, OPTIONS";
                    return ApiHttp.WriteProblemAsync(ctx, new ApiProblem(405, "method-not-allowed", "Method not allowed", "The API is read-only: only GET (and HEAD/OPTIONS) are supported."));
                }
                return ApiHttp.WriteProblemAsync(ctx, ApiProblem.NotFound($"There is no {BasePath}{path} endpoint. The full list is at {BasePath}/openapi.json."));
            }).ExcludeFromDescription();

        return group;
    }

    private static bool IsPrivatePath(string path) => path.Equals("/me", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/me/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"/topics/{tag}" matches "/topics/agents". Used only to tell 404 from 405 on the fallback route.</summary>
    public static bool TemplateMatches(string template, string path)
    {
        var t = template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var p = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != p.Length) return false;
        for (var i = 0; i < t.Length; i++)
            if (!(t[i].StartsWith('{') || string.Equals(t[i], p[i], StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    private static void AddCors(HttpResponse response, bool preflight = false)
    {
        response.Headers.AccessControlAllowOrigin = "*";
        response.Headers.AccessControlExposeHeaders = ExposedHeaders;
        if (!preflight) return;
        response.Headers.AccessControlAllowMethods = "GET, HEAD, OPTIONS";
        response.Headers.AccessControlAllowHeaders = "Authorization, If-None-Match, Accept, Accept-Encoding";
        response.Headers.AccessControlMaxAge = "86400";
    }

    /// <summary>
    /// The partition an anonymous caller is counted under: its IPv4 address, or the /64 network of an IPv6 address (one machine
    /// can hold a whole /64, so counting single v6 addresses would let it multiply its allowance).
    /// </summary>
    public static string ClientPartition(IPAddress? ip)
    {
        if (ip is null) return "ip:unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return "ip6:" + new IPAddress(bytes) + "/64";
        }
        return "ip:" + ip;
    }

    private static async Task ExecuteAsync(HttpContext ctx, PublicApiRuntime runtime, ApiEndpointDoc doc, Func<ApiRequest, Task<ApiResult>> handler)
    {
        var response = ctx.Response;
        response.Headers.XContentTypeOptions = "nosniff";
        if (!doc.RequiresToken) AddCors(response);
        if (doc.RequiresToken) response.Headers.Append("Vary", "Authorization");

        var authHeader = ctx.Request.Headers.Authorization;
        ApiTokenValidation? validation = null;
        if (!StringValues.IsNullOrEmpty(authHeader))
        {
            var raw = ApiTokenFormat.FromAuthorizationHeader(authHeader.ToString());
            validation = raw is null ? ApiTokenValidation.Fail(ApiTokenFailure.Malformed) : await runtime.Tokens.ValidateAsync(raw);
        }
        var principal = validation?.IsValid == true ? validation.Principal : null;

        // A bad token is counted against the caller's address, so guessing tokens is limited like any anonymous traffic.
        var decision = principal is not null
            ? runtime.Limiter.Acquire("token:" + principal.TokenId, runtime.TokenPolicy)
            : runtime.Limiter.Acquire(ClientPartition(ctx.Connection.RemoteIpAddress), runtime.AnonymousPolicy);
        response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-RateLimit-Reset"] = decision.ResetSeconds.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-RateLimit-Policy"] = decision.Policy;
        if (!decision.Allowed)
        {
            response.Headers.RetryAfter = decision.ResetSeconds.ToString(CultureInfo.InvariantCulture);
            var hint = principal is null ? " Requests with a personal token get a higher limit." : "";
            await ApiHttp.WriteProblemAsync(ctx, new ApiProblem(429, "rate-limited", "Too many requests",
                $"Limit of {decision.Limit} requests per {(int)runtime.AnonymousPolicy.Window.TotalSeconds} seconds reached. Retry in {decision.ResetSeconds} s.{hint}"));
            return;
        }

        if (validation is { IsValid: false } failed)
        {
            var (status, code, detail) = failed.Failure switch
            {
                ApiTokenFailure.Expired => (401, "invalid-token", "The token has expired. Create a new one under Settings > API tokens."),
                ApiTokenFailure.Revoked => (401, "invalid-token", "The token has been revoked."),
                ApiTokenFailure.UserInactive => (401, "invalid-token", "The account this token belongs to is not active."),
                ApiTokenFailure.InsufficientScope => (403, "insufficient-scope", "The token does not carry the scope this endpoint needs."),
                ApiTokenFailure.Malformed => (401, "invalid-token", "Send the token as \"Authorization: Bearer aip_...\"."),
                _ => (401, "invalid-token", "The token is not recognised.")
            };
            response.Headers.WWWAuthenticate = status == 403
                ? "Bearer realm=\"AiPulse API\", error=\"insufficient_scope\", scope=\"read\""
                : $"Bearer realm=\"AiPulse API\", error=\"invalid_token\", error_description=\"{detail.Replace("\"", "'")}\"";
            await ApiHttp.WriteProblemAsync(ctx, new ApiProblem(status, code, status == 403 ? "Insufficient scope" : "Invalid token", detail));
            return;
        }

        if (doc.RequiresToken && principal is null)
        {
            response.Headers.WWWAuthenticate = "Bearer realm=\"AiPulse API\"";
            await ApiHttp.WriteProblemAsync(ctx, new ApiProblem(401, "unauthorized", "Token required",
                "This endpoint returns your own data. Send a personal token: \"Authorization: Bearer aip_...\" (create one under Settings > API tokens)."));
            return;
        }

        ApiResult result;
        try
        {
            result = await handler(new ApiRequest(ctx, runtime, doc, principal, runtime.Time.GetUtcNow()));
        }
        catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            runtime.Logger?.LogError(ex, "API {Operation} failed", doc.OperationId);
            result = ApiResult.Fail(new ApiProblem(500, "server-error", "Server error", "Something went wrong on our side. Try again shortly."));
        }
        await ApiHttp.WriteAsync(ctx, result);
    }

    // ---------------------------------------------------------------- envelopes

    public static object Envelope(object data, ApiRequest req, IDictionary<string, object?>? meta = null) => new Dictionary<string, object?>
    {
        ["data"] = data,
        ["meta"] = meta ?? new Dictionary<string, object?>(),
        ["links"] = new Dictionary<string, object?> { ["self"] = SelfUrl(req.Http.Request) }
    };

    public static object PagedEnvelope<T>(PageResult<T> page, ApiRequest req, IDictionary<string, object?>? extraMeta = null)
    {
        var meta = new Dictionary<string, object?>
        {
            ["page"] = page.Page,
            ["pageSize"] = page.PageSize,
            ["total"] = page.Total,
            ["totalPages"] = page.TotalPages
        };
        if (extraMeta is not null) foreach (var kv in extraMeta) meta[kv.Key] = kv.Value;

        var links = PageLinks(req.Http.Request, page);
        var linkHeader = new List<string>();
        foreach (var rel in new[] { "first", "prev", "next", "last" })
            if (links[rel] is string url) linkHeader.Add($"<{url}>; rel=\"{rel}\"");
        if (linkHeader.Count > 0) req.Http.Response.Headers.Link = string.Join(", ", linkHeader);

        return new Dictionary<string, object?> { ["data"] = page.Items, ["meta"] = meta, ["links"] = links };
    }

    public static string SelfUrl(HttpRequest request) => ApiHttp.Origin(request) + request.Path + request.QueryString;

    /// <summary>self/first/prev/next/last, each the request URL with only "page" changed. prev/next are null at the ends.</summary>
    public static Dictionary<string, object?> PageLinks<T>(HttpRequest request, PageResult<T> page)
    {
        string At(int n)
        {
            var parts = request.Query.Where(kv => !string.Equals(kv.Key, "page", StringComparison.OrdinalIgnoreCase))
                .SelectMany(kv => kv.Value.Select(v => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(v ?? "")))
                .Append("page=" + n.ToString(CultureInfo.InvariantCulture));
            return ApiHttp.Origin(request) + request.Path + "?" + string.Join("&", parts);
        }
        var last = Math.Max(1, page.TotalPages);
        return new Dictionary<string, object?>
        {
            ["self"] = SelfUrl(request),
            ["first"] = At(1),
            ["prev"] = page.Page > 1 ? At(Math.Min(page.Page - 1, last)) : null,
            ["next"] = page.Page < page.TotalPages ? At(page.Page + 1) : null,
            ["last"] = At(last)
        };
    }

    // ---------------------------------------------------------------- handlers

    private static Dictionary<string, Func<ApiRequest, Task<ApiResult>>> Handlers() => new()
    {
        ["getIndex"] = r => Sync(Index(r)),
        ["listNews"] = r => Sync(ListNews(r)),
        ["getNewsItem"] = r => Sync(GetNewsItem(r)),
        ["listTopics"] = r => Sync(ListTopics(r)),
        ["getTopic"] = r => Sync(GetTopic(r)),
        ["listReleases"] = r => Sync(ListReleases(r)),
        ["listSources"] = r => Sync(ListSources(r)),
        ["listGlossary"] = r => Sync(ListGlossary(r)),
        ["getGlossaryTerm"] = r => Sync(GetGlossaryTerm(r)),
        ["listTools"] = r => Sync(ListTools(r)),
        ["getTool"] = r => Sync(GetTool(r)),
        ["search"] = r => Sync(Search(r)),
        ["getStats"] = r => Sync(Stats(r)),
        ["getStatus"] = Status,
        ["getOpenApi"] = r => Sync(ApiResult.Ok(ApiCatalog.OpenApi(r.Origin), ApiCache.Document)),
        ["getMe"] = r => Sync(Me(r)),
        ["listBookmarks"] = r => Sync(ListBookmarks(r)),
        ["listUnread"] = r => Sync(ListUnread(r)),
    };

    private static Task<ApiResult> Sync(ApiResult result) => Task.FromResult(result);

    private static ApiResult Index(ApiRequest r)
    {
        var data = new Dictionary<string, object?>
        {
            ["name"] = "AiPulse API",
            ["version"] = "v1",
            ["docs"] = r.Origin + "/developers",
            ["openapi"] = r.Origin + BasePath + "/openapi.json",
            ["atom"] = r.Origin + "/feed.xml",
            ["endpoints"] = ApiCatalog.Endpoints.Select(e => new Dictionary<string, object?>
            {
                ["operationId"] = e.OperationId,
                ["method"] = "GET",
                ["path"] = e.FullPath,
                ["summary"] = e.Summary,
                ["requiresToken"] = e.RequiresToken
            }).ToList()
        };
        return ApiResult.Ok(Envelope(data, r), ApiCache.Reference);
    }

    private static ApiResult ListNews(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var filter = ApiQuery.ParseNewsFilter(r.Query, r.Now, errors);
        var paging = ApiQuery.ParsePaging(r.Query, errors);
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));
        if (string.Equals(filter.Type, "Release", StringComparison.OrdinalIgnoreCase))
            return ApiResult.Fail(ApiProblem.BadRequest("type", $"Release notes are served by {BasePath}/releases, not {BasePath}/news."));

        var matches = ApiQuery.Apply(r.Data.Items.Where(PublicFeeds.IsHeadline), filter).Select(ApiMapper.News).ToList();
        return ApiResult.Ok(PagedEnvelope(ApiQuery.Paginate(matches, paging), r), ApiCache.News);
    }

    private static ApiResult GetNewsItem(ApiRequest r)
    {
        var id = r.Route("id").Trim().ToLowerInvariant();
        var item = id.Length == 40 ? r.Data.Items.FirstOrDefault(i => ApiMapper.ItemId(i.Link) == id) : null;
        return item is null
            ? ApiResult.Fail(ApiProblem.NotFound($"No news item has id \"{id}\". Items leave the history after 90 days."))
            : ApiResult.Ok(Envelope(ApiMapper.News(item), r, new Dictionary<string, object?> { ["isRelease"] = !PublicFeeds.IsHeadline(item) }), ApiCache.News);
    }

    private static ApiResult ListTopics(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var days = ApiQuery.ParseInt(r.Query, "days", 30, 1, 90, errors);
        var limit = ApiQuery.ParseInt(r.Query, "limit", 60, 1, 200, errors);
        var minCount = ApiQuery.ParseInt(r.Query, "minCount", 2, 1, 1000, errors);
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));

        var since = r.Now.AddDays(-days);
        var topics = PublicFeeds.Topics(r.Data.Items, since, limit, minCount).Select(t => ApiMapper.Topic(t, r.Origin)).ToList();
        return ApiResult.Ok(Envelope(topics, r, new Dictionary<string, object?> { ["total"] = topics.Count, ["days"] = days }), ApiCache.News);
    }

    private static ApiResult GetTopic(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var paging = ApiQuery.ParsePaging(r.Query, errors);
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));

        var tag = r.Route("tag").Trim();
        var items = PublicFeeds.ForTag(r.Data.Items, tag, int.MaxValue);
        if (items.Count == 0) return ApiResult.Fail(ApiProblem.NotFound($"No saved headline carries the tag \"{tag}\"."));

        var display = items.SelectMany(i => i.Tags).Where(t => string.Equals(t.Trim(), tag, StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t.Trim()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        var page = ApiQuery.Paginate(items.Select(ApiMapper.News).ToList(), paging);
        return ApiResult.Ok(PagedEnvelope(page, r, new Dictionary<string, object?> { ["tag"] = display, ["webUrl"] = r.Origin + PublicFeeds.TagPath(display) }), ApiCache.News);
    }

    private static ApiResult ListReleases(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var filter = ApiQuery.ParseNewsFilter(r.Query, r.Now, errors);
        var paging = ApiQuery.ParsePaging(r.Query, errors);
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));

        var releases = PublicFeeds.Releases(r.Data.Items, r.Now, int.MaxValue);
        var matches = ApiQuery.Apply(releases, filter with { Type = null }).Select(ApiMapper.News).ToList();
        return ApiResult.Ok(PagedEnvelope(ApiQuery.Paginate(matches, paging), r), ApiCache.News);
    }

    private static ApiResult ListSources(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var q = ApiQuery.ParseText(r.Query, "q", errors);
        var category = ApiQuery.ParseText(r.Query, "category", errors, 100);
        var type = ApiQuery.ParseText(r.Query, "type", errors, 100);
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));

        IEnumerable<FeedSource> sources = r.Data.Sources;
        if (q is not null) { var terms = ApiQuery.Terms(q); sources = sources.Where(s => ApiQuery.MatchesAll(terms, s.Name)); }
        if (category is not null) sources = sources.Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase));
        if (type is not null) sources = sources.Where(s => string.Equals(s.ContentType, type, StringComparison.OrdinalIgnoreCase));
        var list = sources.OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(ApiMapper.Source).ToList();
        return ApiResult.Ok(Envelope(list, r, new Dictionary<string, object?> { ["total"] = list.Count }), ApiCache.Reference);
    }

    private static ApiResult ListGlossary(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var q = ApiQuery.ParseText(r.Query, "q", errors);
        var category = ApiQuery.ParseText(r.Query, "category", errors, 100);
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));

        IEnumerable<GlossaryTerm> terms = r.Data.Glossary;
        if (q is not null) { var words = ApiQuery.Terms(q); terms = terms.Where(t => ApiQuery.MatchesAll(words, t.Term, t.ShortDef, string.Join(' ', t.Aliases))); }
        if (category is not null) terms = terms.Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase));
        var list = terms.OrderBy(t => t.Term, StringComparer.OrdinalIgnoreCase).Select(ApiMapper.Glossary).ToList();
        return ApiResult.Ok(Envelope(list, r, new Dictionary<string, object?> { ["total"] = list.Count }), ApiCache.Reference);
    }

    private static ApiResult GetGlossaryTerm(ApiRequest r)
    {
        var key = r.Route("slug").Trim();
        var slug = ApiMapper.Slug(key);
        var term = r.Data.Glossary.FirstOrDefault(t => ApiMapper.Slug(t.Term) == slug)
                   ?? r.Data.Glossary.FirstOrDefault(t => t.Aliases.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase) || ApiMapper.Slug(a) == slug));
        return term is null
            ? ApiResult.Fail(ApiProblem.NotFound($"No glossary term or alias matches \"{key}\"."))
            : ApiResult.Ok(Envelope(ApiMapper.Glossary(term), r, new Dictionary<string, object?> { ["webUrl"] = r.Origin + "/glossary" }), ApiCache.Reference);
    }

    private static ApiResult ListTools(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var q = ApiQuery.ParseText(r.Query, "q", errors);
        var category = ApiQuery.ParseText(r.Query, "category", errors, 100);
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));

        IEnumerable<ToolEntry> tools = r.Data.Tools;
        if (q is not null) { var words = ApiQuery.Terms(q); tools = tools.Where(t => ApiQuery.MatchesAll(words, t.Name, t.OneLiner, string.Join(' ', t.BestFor))); }
        if (category is not null) tools = tools.Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase));
        var list = tools.OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(ApiMapper.Tool).ToList();
        return ApiResult.Ok(Envelope(list, r, new Dictionary<string, object?> { ["total"] = list.Count }), ApiCache.Reference);
    }

    private static ApiResult GetTool(ApiRequest r)
    {
        var key = r.Route("slug").Trim();
        var slug = ApiMapper.Slug(key);
        var tool = r.Data.Tools.FirstOrDefault(t => ApiMapper.Slug(t.Name) == slug);
        return tool is null
            ? ApiResult.Fail(ApiProblem.NotFound($"No tool matches \"{key}\"."))
            : ApiResult.Ok(Envelope(ApiMapper.Tool(tool), r, new Dictionary<string, object?> { ["webUrl"] = r.Origin + "/tools" }), ApiCache.Reference);
    }

    private static ApiResult Search(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var q = ApiQuery.ParseText(r.Query, "q", errors);
        var limit = ApiQuery.ParseInt(r.Query, "limit", 5, 1, 20, errors);
        if (q is null && !errors.Any) errors.Add("q", "Required.");
        else if (q is not null && q.Length < ApiQuery.MinSearchLength) errors.Add("q", $"Use at least {ApiQuery.MinSearchLength} characters.");
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));

        var results = ApiQuery.Search(q!, limit, r.Data.Items, r.Data.Glossary, r.Data.Tools, r.Now, r.Origin);
        return ApiResult.Ok(Envelope(results, r, new Dictionary<string, object?> { ["limit"] = limit }), ApiCache.News);
    }

    public static StatsDto ComputeStats(IPublicApiData data, DateTimeOffset now)
    {
        var items = data.Items;
        var site = SiteStatsService.Compute(data.Sources.Count, data.Glossary.Count, data.LearningModules, items, now, data.LastPolled, data.PollMinutes);
        var releases = items.Count(i => !PublicFeeds.IsHeadline(i) && i.Published <= now);
        return new StatsDto(site.ActiveSources, site.GlossaryTerms, data.Tools.Count, site.LearningModules, items.Count(PublicFeeds.IsHeadline),
            releases, site.ItemsLast24Hours, site.NewestItem?.ToUniversalTime(), site.LastPolled?.ToUniversalTime(), site.PollMinutes);
    }

    private static ApiResult Stats(ApiRequest r) => ApiResult.Ok(Envelope(ComputeStats(r.Data, r.Now), r), ApiCache.News);

    private static async Task<ApiResult> Status(ApiRequest r)
    {
        var dbOk = await r.Data.DatabaseHealthyAsync(r.Http.RequestAborted);
        var stats = ComputeStats(r.Data, r.Now);
        // Fresh = polled within three poll intervals. Before the first poll after a restart there is no time yet; not "stale".
        var fresh = stats.LastPolledAt is not { } polled || r.Now - polled <= TimeSpan.FromMinutes(3 * Math.Max(1, stats.PollMinutes));
        var status = new StatusDto(!dbOk ? "down" : fresh ? "ok" : "degraded", "v1", r.Now.ToUniversalTime(), stats.NewestItemAt, stats.LastPolledAt, fresh, dbOk ? "ok" : "unreachable");
        var body = Envelope(status, r);
        return dbOk ? ApiResult.Ok(body, ApiCache.NoStore) : ApiResult.Unavailable(body);
    }

    private static ApiResult Me(ApiRequest r)
    {
        var p = r.Principal!;
        var state = r.Data.ReadingStateFor(p.Username);
        var me = new MeDto(p.UserId, p.Username, p.Role, new TokenSummaryDto(p.TokenName, p.Prefix, p.Scopes, p.ExpiresAt?.ToUniversalTime()),
            state?.Bookmarks.Count ?? 0, state?.ReadLinks.Count ?? 0,
            state is null || state.LastNewsVisit == DateTimeOffset.MinValue ? null : state.LastNewsVisit.ToUniversalTime());
        return ApiResult.Ok(Envelope(me, r), ApiCache.Private);
    }

    private static ApiResult ListBookmarks(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var q = ApiQuery.ParseText(r.Query, "q", errors);
        var paging = ApiQuery.ParsePaging(r.Query, errors);
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));

        IEnumerable<BookmarkItem> bookmarks = r.Data.ReadingStateFor(r.Principal!.Username)?.Bookmarks ?? new List<BookmarkItem>();
        if (q is not null) { var words = ApiQuery.Terms(q); bookmarks = bookmarks.Where(b => ApiQuery.MatchesAll(words, b.Title, b.SourceName, string.Join(' ', b.Tags))); }
        var list = bookmarks.OrderByDescending(b => b.SavedAt).ThenBy(b => b.Link, StringComparer.Ordinal).Select(ApiMapper.Bookmark).ToList();
        return ApiResult.Ok(PagedEnvelope(ApiQuery.Paginate(list, paging), r), ApiCache.Private);
    }

    private static ApiResult ListUnread(ApiRequest r)
    {
        var errors = new ApiQueryErrors();
        var filter = ApiQuery.ParseNewsFilter(r.Query, r.Now, errors);
        var paging = ApiQuery.ParsePaging(r.Query, errors);
        if (errors.Any) return ApiResult.Fail(ApiProblem.InvalidParameters(errors));

        var read = r.Data.ReadingStateFor(r.Principal!.Username)?.ReadLinks ?? new HashSet<string>();
        var unread = r.Data.Items.Where(i => PublicFeeds.IsHeadline(i) && !read.Contains(i.Link));
        var matches = ApiQuery.Apply(unread, filter).Select(ApiMapper.News).ToList();
        return ApiResult.Ok(PagedEnvelope(ApiQuery.Paginate(matches, paging), r, new Dictionary<string, object?> { ["readItems"] = read.Count }), ApiCache.Private);
    }
}

