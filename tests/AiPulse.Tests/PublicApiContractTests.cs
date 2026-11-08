using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AiPulse.Models;
using AiPulse.Services.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiPulse.Tests;

/// <summary>The real /api/v1 endpoints on an in-memory server, over fixed data and a throwaway database.</summary>
public class PublicApiContractTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static HttpRequestMessage Get(string url, string? token = null, string? ip = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ip is not null) req.Headers.Add("X-Test-IP", ip);
        return req;
    }

    private static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : res.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : null;

    private static async Task<string> TokenFor(ApiHost host, string username, TimeSpan? lifetime = null)
    {
        var user = await host.Db.UserAsync(username);
        var (created, _) = await host.Runtime.Tokens.CreateAsync(user.Id, "test", lifetime);
        return created!.Token;
    }

    // ---------------------------------------------------------------- envelope and lists

    [Fact]
    public async Task News_returns_the_envelope_with_paging_meta_and_links()
    {
        await using var host = await ApiHost.StartAsync();
        var res = await host.Client.GetAsync("/api/v1/news?pageSize=2");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);

        var body = await Json(res);
        Assert.Equal(new[] { "data", "meta", "links" }, body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, body.GetProperty("data").GetArrayLength());
        var meta = body.GetProperty("meta");
        Assert.Equal(1, meta.GetProperty("page").GetInt32());
        Assert.Equal(2, meta.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, meta.GetProperty("total").GetInt32()); // headlines only: releases are excluded
        Assert.Equal(3, meta.GetProperty("totalPages").GetInt32());

        var links = body.GetProperty("links");
        Assert.Equal(JsonValueKind.Null, links.GetProperty("prev").ValueKind);
        Assert.Equal("http://localhost/api/v1/news?pageSize=2&page=2", links.GetProperty("next").GetString());
        Assert.Equal("http://localhost/api/v1/news?pageSize=2&page=3", links.GetProperty("last").GetString());
        Assert.Contains("rel=\"next\"", Header(res, "Link"));

        var first = body.GetProperty("data")[0];
        Assert.Equal("Agents get memory", first.GetProperty("title").GetString());
        Assert.Equal(40, first.GetProperty("id").GetString()!.Length);
    }

    [Fact]
    public async Task News_never_includes_releases_and_points_type_Release_at_releases()
    {
        await using var host = await ApiHost.StartAsync();
        var body = await Json(await host.Client.GetAsync("/api/v1/news?pageSize=100"));
        Assert.DoesNotContain(body.GetProperty("data").EnumerateArray(), i => i.GetProperty("type").GetString() == "Release");

        var res = await host.Client.GetAsync("/api/v1/news?type=Release");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("/releases", (await Json(res)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task News_filters_combine()
    {
        await using var host = await ApiHost.StartAsync();
        var body = await Json(await host.Client.GetAsync("/api/v1/news?tag=agents&since=7d&sort=oldest"));
        var titles = body.GetProperty("data").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
        Assert.Equal(new[] { "Mixture of experts explained", "New agent framework", "Agents get memory" }, titles);

        var bySource = await Json(await host.Client.GetAsync("/api/v1/news?source=Research%20Weekly,Dev%20Digest"));
        Assert.Equal(2, bySource.GetProperty("meta").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Page_past_the_end_is_empty_with_a_prev_link()
    {
        await using var host = await ApiHost.StartAsync();
        var body = await Json(await host.Client.GetAsync("/api/v1/news?page=50"));
        Assert.Equal(0, body.GetProperty("data").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("links").GetProperty("next").ValueKind);
        Assert.EndsWith("page=1", body.GetProperty("links").GetProperty("prev").GetString());
    }

    [Fact]
    public async Task One_item_by_id_and_404_for_unknown_ids()
    {
        await using var host = await ApiHost.StartAsync();
        var list = await Json(await host.Client.GetAsync("/api/v1/releases"));
        var id = list.GetProperty("data")[0].GetProperty("id").GetString();

        var one = await Json(await host.Client.GetAsync($"/api/v1/news/{id}"));
        Assert.Equal("transformers v5.0.0", one.GetProperty("data").GetProperty("title").GetString());
        Assert.True(one.GetProperty("meta").GetProperty("isRelease").GetBoolean());

        var missing = await host.Client.GetAsync("/api/v1/news/" + new string('0', 40));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Releases_exclude_future_dated_items()
    {
        await using var host = await ApiHost.StartAsync();
        var body = await Json(await host.Client.GetAsync("/api/v1/releases"));
        var titles = body.GetProperty("data").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
        Assert.Equal(new[] { "transformers v5.0.0", "llama.cpp b4000" }, titles);
    }

    [Fact]
    public async Task Topics_and_one_topic()
    {
        await using var host = await ApiHost.StartAsync();
        var topics = await Json(await host.Client.GetAsync("/api/v1/topics"));
        var first = topics.GetProperty("data")[0];
        Assert.Equal("agents", first.GetProperty("tag").GetString());
        Assert.Equal(3, first.GetProperty("count").GetInt32()); // the 40-day-old item is outside the 30-day window
        Assert.Equal("http://localhost/topics/agents", first.GetProperty("webUrl").GetString());

        var topic = await Json(await host.Client.GetAsync("/api/v1/topics/AGENTS"));
        Assert.Equal("agents", topic.GetProperty("meta").GetProperty("tag").GetString());
        Assert.Equal(4, topic.GetProperty("meta").GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/v1/topics/nothing-here")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/v1/topics?days=365")).StatusCode);
    }

    [Fact]
    public async Task Sources_show_sites_not_feed_urls()
    {
        await using var host = await ApiHost.StartAsync();
        var raw = await (await host.Client.GetAsync("/api/v1/sources")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("feed.xml", raw);
        Assert.DoesNotContain("/rss", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(3, body.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.Contains(body.GetProperty("data").EnumerateArray(), s => s.GetProperty("site").GetString() == "https://example.com");

        var research = await Json(await host.Client.GetAsync("/api/v1/sources?category=research"));
        Assert.Equal("Research Weekly", research.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Glossary_list_detail_by_slug_and_alias()
    {
        await using var host = await ApiHost.StartAsync();
        var all = await Json(await host.Client.GetAsync("/api/v1/glossary"));
        Assert.Equal(new[] { "Agent", "Mixture of Experts", "Retrieval-Augmented Generation" },
            all.GetProperty("data").EnumerateArray().Select(t => t.GetProperty("term").GetString()));

        var bySlug = await Json(await host.Client.GetAsync("/api/v1/glossary/mixture-of-experts"));
        Assert.Equal("Mixture of Experts", bySlug.GetProperty("data").GetProperty("term").GetString());
        var byAlias = await Json(await host.Client.GetAsync("/api/v1/glossary/rag"));
        Assert.Equal("Retrieval-Augmented Generation", byAlias.GetProperty("data").GetProperty("term").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/v1/glossary/zzz")).StatusCode);

        var q = await Json(await host.Client.GetAsync("/api/v1/glossary?q=expert"));
        Assert.Equal(1, q.GetProperty("meta").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Tools_list_filter_and_detail()
    {
        await using var host = await ApiHost.StartAsync();
        var agents = await Json(await host.Client.GetAsync("/api/v1/tools?category=coding%20agent"));
        Assert.Equal("claude-code", agents.GetProperty("data")[0].GetProperty("slug").GetString());
        var one = await Json(await host.Client.GetAsync("/api/v1/tools/ollama"));
        Assert.Equal("Ollama", one.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Search_requires_q_and_returns_every_section()
    {
        await using var host = await ApiHost.StartAsync();
        var missing = await host.Client.GetAsync("/api/v1/search");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True((await Json(missing)).GetProperty("errors").TryGetProperty("q", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/v1/search?q=a")).StatusCode);

        var body = await Json(await host.Client.GetAsync("/api/v1/search?q=agents"));
        var data = body.GetProperty("data");
        foreach (var section in new[] { "news", "releases", "topics", "glossary", "tools" })
            Assert.Equal(JsonValueKind.Array, data.GetProperty(section).ValueKind);
        Assert.True(data.GetProperty("news").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Stats_and_status()
    {
        await using var host = await ApiHost.StartAsync();
        var stats = (await Json(await host.Client.GetAsync("/api/v1/stats"))).GetProperty("data");
        Assert.Equal(3, stats.GetProperty("activeSources").GetInt32());
        Assert.Equal(3, stats.GetProperty("glossaryTerms").GetInt32());
        Assert.Equal(2, stats.GetProperty("tools").GetInt32());
        Assert.Equal(5, stats.GetProperty("headlines").GetInt32());
        Assert.Equal(2, stats.GetProperty("releases").GetInt32());
        Assert.Equal(2, stats.GetProperty("itemsLast24Hours").GetInt32());

        var res = await host.Client.GetAsync("/api/v1/status");
        Assert.True(res.Headers.CacheControl!.NoStore);
        Assert.Null(res.Headers.ETag);
        Assert.Equal("ok", (await Json(res)).GetProperty("data").GetProperty("status").GetString());

        host.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal("degraded", (await Json(await host.Client.GetAsync("/api/v1/status"))).GetProperty("data").GetProperty("status").GetString());

        host.Data.DbHealthy = false;
        var down = await host.Client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("down", (await Json(down)).GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Index_lists_every_endpoint()
    {
        await using var host = await ApiHost.StartAsync();
        foreach (var url in new[] { "/api/v1", "/api/v1/" })
        {
            var body = await Json(await host.Client.GetAsync(url));
            Assert.Equal(ApiCatalog.Endpoints.Count, body.GetProperty("data").GetProperty("endpoints").GetArrayLength());
            Assert.Equal("http://localhost/developers", body.GetProperty("data").GetProperty("docs").GetString());
        }
    }

    // ---------------------------------------------------------------- errors

    [Fact]
    public async Task Invalid_parameters_give_a_problem_document_listing_each_one()
    {
        await using var host = await ApiHost.StartAsync();
        var res = await host.Client.GetAsync("/api/v1/news?pageSize=500&since=whenever");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
        Assert.True(res.Headers.CacheControl!.NoStore);

        var p = await Json(res);
        Assert.Equal("http://localhost/developers#error-invalid-parameter", p.GetProperty("type").GetString());
        Assert.Equal(400, p.GetProperty("status").GetInt32());
        Assert.Equal("/api/v1/news?pageSize=500&since=whenever", p.GetProperty("instance").GetString());
        var errors = p.GetProperty("errors");
        Assert.True(errors.TryGetProperty("pageSize", out _));
        Assert.True(errors.TryGetProperty("since", out _));
    }

    [Fact]
    public async Task Unknown_paths_are_problem_404s_and_writes_are_405()
    {
        await using var host = await ApiHost.StartAsync();
        var res = await host.Client.GetAsync("/api/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("not-found", (await Json(res)).GetProperty("type").GetString()!.Split('-', 2)[1]);

        var post = await host.Client.PostAsync("/api/v1/news", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Contains("GET", string.Join(",", post.Content.Headers.Allow));

        var delete = await host.Client.DeleteAsync("/api/v1/me/bookmarks");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync("/api/v1/nope", null)).StatusCode);
    }

    [Fact]
    public async Task Handler_exceptions_become_a_500_problem()
    {
        var data = FakeApiData.Sample(DateTimeOffset.UtcNow);
        data.GlossaryList.Add(null!); // a broken data source
        await using var host = await ApiHost.StartAsync(data: data);
        var res = await host.Client.GetAsync("/api/v1/glossary");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("Server error", (await Json(res)).GetProperty("title").GetString());
    }

    // ---------------------------------------------------------------- caching, compression, CORS

    [Fact]
    public async Task ETag_round_trip_gives_304_and_changes_with_the_data()
    {
        await using var host = await ApiHost.StartAsync();
        var first = await host.Client.GetAsync("/api/v1/news");
        var etag = first.Headers.ETag!;
        Assert.True(etag.IsWeak);
        Assert.True(first.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromSeconds(60), first.Headers.CacheControl.MaxAge);

        var again = Get("/api/v1/news");
        again.Headers.IfNoneMatch.Add(etag);
        var notModified = await host.Client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(etag, notModified.Headers.ETag);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());

        host.Data.ItemList.Add(FakeApiData.Item("Breaking", host.Time.GetUtcNow()));
        var changed = Get("/api/v1/news");
        changed.Headers.IfNoneMatch.Add(etag);
        var fresh = await host.Client.SendAsync(changed);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.NotEqual(etag, fresh.Headers.ETag);
    }

    [Fact]
    public async Task Reference_data_is_cached_longer()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(300), (await host.Client.GetAsync("/api/v1/glossary")).Headers.CacheControl!.MaxAge);
        Assert.Equal(TimeSpan.FromSeconds(3600), (await host.Client.GetAsync("/api/v1/openapi.json")).Headers.CacheControl!.MaxAge);
    }

    [Fact]
    public async Task Large_bodies_are_gzipped_when_asked_and_decode_to_the_same_json()
    {
        await using var host = await ApiHost.StartAsync();
        var plain = await (await host.Client.GetAsync("/api/v1/openapi.json")).Content.ReadAsStringAsync();

        var req = Get("/api/v1/openapi.json");
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        var res = await host.Client.SendAsync(req);
        Assert.Equal("gzip", res.Content.Headers.ContentEncoding.Single());
        Assert.Contains("Accept-Encoding", Header(res, "Vary"));
        await using var gz = new GZipStream(await res.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        Assert.Equal(plain, await reader.ReadToEndAsync());

        var small = Get("/api/v1/tools/ollama");
        small.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        Assert.Empty((await host.Client.SendAsync(small)).Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Public_endpoints_are_cors_open_and_preflight_is_answered()
    {
        await using var host = await ApiHost.StartAsync();
        var res = await host.Client.GetAsync("/api/v1/news");
        Assert.Equal("*", Header(res, "Access-Control-Allow-Origin"));
        Assert.Contains("X-RateLimit-Remaining", Header(res, "Access-Control-Expose-Headers"));
        Assert.Contains("ETag", Header(res, "Access-Control-Expose-Headers"));

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/news");
        preflight.Headers.Add("Origin", "https://someone.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "if-none-match");
        var pre = await host.Client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, pre.StatusCode);
        Assert.Equal("*", Header(pre, "Access-Control-Allow-Origin"));
        Assert.Contains("If-None-Match", Header(pre, "Access-Control-Allow-Headers"));
    }

    [Fact]
    public async Task Private_endpoints_are_not_cors_open()
    {
        await using var host = await ApiHost.StartAsync();
        var token = await TokenFor(host, "cora");
        var res = await host.Client.SendAsync(Get("/api/v1/me", token));
        Assert.Null(Header(res, "Access-Control-Allow-Origin"));
        Assert.True(res.Headers.CacheControl!.Private);
        Assert.True(res.Headers.CacheControl.NoCache);
        Assert.Contains("Authorization", Header(res, "Vary"));

        var preflight = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/v1/me/bookmarks"));
        Assert.Null(Header(preflight, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Head_requests_send_headers_without_a_body()
    {
        await using var host = await ApiHost.StartAsync();
        var res = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/v1/news"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(res.Headers.ETag);
        Assert.Empty(await res.Content.ReadAsByteArrayAsync());
    }

    // ---------------------------------------------------------------- tokens

    [Fact]
    public async Task Me_needs_a_token()
    {
        await using var host = await ApiHost.StartAsync();
        var res = await host.Client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.StartsWith("Bearer", res.Headers.WwwAuthenticate.ToString());
        Assert.Equal("http://localhost/developers#error-unauthorized", (await Json(res)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Me_with_a_token_shows_the_account_token_and_reading_counts()
    {
        await using var host = await ApiHost.StartAsync();
        var token = await TokenFor(host, "dana");
        host.Data.States["dana"] = new ReadingState
        {
            Bookmarks = { new BookmarkItem { Title = "Saved", Link = "https://example.com/saved", SavedAt = host.Time.GetUtcNow() } },
            ReadLinks = { "https://example.com/agents-get-memory" }
        };

        var me = (await Json(await host.Client.SendAsync(Get("/api/v1/me", token)))).GetProperty("data");
        Assert.Equal("dana", me.GetProperty("username").GetString());
        Assert.Equal("User", me.GetProperty("role").GetString());
        Assert.Equal(token[..12], me.GetProperty("token").GetProperty("prefix").GetString());
        Assert.Equal(1, me.GetProperty("bookmarks").GetInt32());
        Assert.Equal(1, me.GetProperty("readItems").GetInt32());
    }

    [Fact]
    public async Task Bookmarks_and_unread_are_per_token_owner()
    {
        await using var host = await ApiHost.StartAsync();
        var a = await TokenFor(host, "ann");
        var b = await TokenFor(host, "ben");
        var now = host.Time.GetUtcNow();
        host.Data.States["ann"] = new ReadingState
        {
            Bookmarks =
            {
                new BookmarkItem { Title = "Older", Link = "https://example.com/1", SavedAt = now.AddDays(-1), Tags = new[] { "rag" } },
                new BookmarkItem { Title = "Newer", Link = "https://example.com/2", SavedAt = now },
            },
            ReadLinks = { "https://example.com/agents-get-memory" }
        };

        var annMarks = await Json(await host.Client.SendAsync(Get("/api/v1/me/bookmarks", a)));
        Assert.Equal(new[] { "Newer", "Older" }, annMarks.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        var filtered = await Json(await host.Client.SendAsync(Get("/api/v1/me/bookmarks?q=rag", a)));
        Assert.Equal(1, filtered.GetProperty("meta").GetProperty("total").GetInt32());

        var benMarks = await Json(await host.Client.SendAsync(Get("/api/v1/me/bookmarks", b)));
        Assert.Equal(0, benMarks.GetProperty("meta").GetProperty("total").GetInt32());

        var annUnread = await Json(await host.Client.SendAsync(Get("/api/v1/me/unread", a)));
        Assert.Equal(4, annUnread.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.DoesNotContain(annUnread.GetProperty("data").EnumerateArray(), x => x.GetProperty("title").GetString() == "Agents get memory");
        var benUnread = await Json(await host.Client.SendAsync(Get("/api/v1/me/unread", b)));
        Assert.Equal(5, benUnread.GetProperty("meta").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Revoked_expired_and_garbage_tokens_get_401_even_on_public_endpoints()
    {
        await using var host = await ApiHost.StartAsync();
        var user = await host.Db.UserAsync("eve");
        var (revoked, _) = await host.Runtime.Tokens.CreateAsync(user.Id, "r", null);
        await host.Runtime.Tokens.RevokeAsync(user.Id, revoked!.Info.Id);
        var (expiring, _) = await host.Runtime.Tokens.CreateAsync(user.Id, "e", TimeSpan.FromDays(1));
        host.Time.Advance(TimeSpan.FromDays(2));

        foreach (var (token, words) in new[] { (revoked.Token, "revoked"), (expiring!.Token, "expired"), ("garbage", "Bearer"), (ApiTokenFormat.Generate(), "not recognised") })
        {
            var res = await host.Client.SendAsync(Get("/api/v1/news", token));
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Contains("invalid_token", res.Headers.WwwAuthenticate.ToString());
            Assert.Contains(words, (await Json(res)).GetProperty("detail").GetString());
        }

        var basic = Get("/api/v1/news");
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(basic)).StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_s_token_stops_working()
    {
        await using var host = await ApiHost.StartAsync();
        var user = await host.Db.UserAsync("fay");
        var (created, _) = await host.Runtime.Tokens.CreateAsync(user.Id, "t", null);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(Get("/api/v1/me", created!.Token))).StatusCode);
        await host.Db.Users.SetStatusAsync(user.Id, UserStatuses.Disabled);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(Get("/api/v1/me", created.Token))).StatusCode);
    }

    // ---------------------------------------------------------------- rate limits

    [Fact]
    public async Task Anonymous_callers_are_limited_per_ip_with_headers_and_retry_after()
    {
        await using var host = await ApiHost.StartAsync(new PublicApiOptions { AnonymousPermitLimit = 3, TokenPermitLimit = 10, WindowSeconds = 60 });
        for (var i = 0; i < 3; i++)
        {
            var ok = await host.Client.SendAsync(Get("/api/v1/stats", ip: "203.0.113.1"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("3", Header(ok, "X-RateLimit-Limit"));
            Assert.Equal((2 - i).ToString(), Header(ok, "X-RateLimit-Remaining"));
            Assert.Equal("60", Header(ok, "X-RateLimit-Reset"));
        }

        host.Time.Advance(TimeSpan.FromSeconds(15));
        var limited = await host.Client.SendAsync(Get("/api/v1/stats", ip: "203.0.113.1"));
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal("45", Header(limited, "Retry-After"));
        Assert.Equal("0", Header(limited, "X-RateLimit-Remaining"));
        var problem = await Json(limited);
        Assert.Equal("http://localhost/developers#error-rate-limited", problem.GetProperty("type").GetString());
        Assert.Equal("*", Header(limited, "Access-Control-Allow-Origin"));

        // Another address has its own allowance; and the window resets.
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(Get("/api/v1/stats", ip: "203.0.113.2"))).StatusCode);
        host.Time.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(Get("/api/v1/stats", ip: "203.0.113.1"))).StatusCode);
    }

    [Fact]
    public async Task Token_callers_are_limited_per_token_not_per_ip()
    {
        await using var host = await ApiHost.StartAsync(new PublicApiOptions { AnonymousPermitLimit = 1, TokenPermitLimit = 3, WindowSeconds = 60 });
        var token = await TokenFor(host, "gus");

        for (var i = 0; i < 3; i++)
        {
            var res = await host.Client.SendAsync(Get("/api/v1/news", token, ip: "198.51.100." + i));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("3", Header(res, "X-RateLimit-Limit"));
            Assert.Equal("token", Header(res, "X-RateLimit-Policy"));
        }
        Assert.Equal((HttpStatusCode)429, (await host.Client.SendAsync(Get("/api/v1/me", token, ip: "198.51.100.9"))).StatusCode);

        // The anonymous allowance of the same address is untouched by token traffic.
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(Get("/api/v1/news", ip: "198.51.100.0"))).StatusCode);
    }

    [Fact]
    public async Task Bad_tokens_count_against_the_callers_address()
    {
        await using var host = await ApiHost.StartAsync(new PublicApiOptions { AnonymousPermitLimit = 2, TokenPermitLimit = 100, WindowSeconds = 60 });
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(Get("/api/v1/news", ApiTokenFormat.Generate(), "192.0.2.7"))).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await host.Client.SendAsync(Get("/api/v1/news", ApiTokenFormat.Generate(), "192.0.2.7"))).StatusCode);
    }

    // ---------------------------------------------------------------- OpenAPI and route table

    [Fact]
    public async Task OpenApi_document_describes_every_mapped_endpoint_and_nothing_else()
    {
        await using var host = await ApiHost.StartAsync();
        var doc = await Json(await host.Client.GetAsync("/api/v1/openapi.json"));
        Assert.Equal("3.0.3", doc.GetProperty("openapi").GetString());
        Assert.Equal("http://localhost/api/v1", doc.GetProperty("servers")[0].GetProperty("url").GetString());

        var documented = doc.GetProperty("paths").EnumerateObject().Select(p => p.Name).OrderBy(p => p).ToList();

        var mapped = host.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .Where(t => !t.Contains("{**rest}"))
            .Select(t => t.Length > "/api/v1".Length ? t["/api/v1".Length..] : "/")
            .Select(t => t == "" ? "/" : t)
            .OrderBy(p => p).ToList();
        Assert.Equal(mapped, documented);
    }

    [Fact]
    public async Task OpenApi_schemas_match_what_the_api_actually_serializes()
    {
        await using var host = await ApiHost.StartAsync();
        var token = await TokenFor(host, "hal");
        var doc = await Json(await host.Client.GetAsync("/api/v1/openapi.json"));
        var schemas = doc.GetProperty("components").GetProperty("schemas");

        async Task Check(string url, string schema, bool list, string? auth = null)
        {
            var body = await Json(await host.Client.SendAsync(Get(url, auth)));
            var sample = list ? body.GetProperty("data")[0] : body.GetProperty("data");
            var actual = sample.EnumerateObject().Select(p => p.Name).ToList();
            var declared = schemas.GetProperty(schema).GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(declared, actual);
            var required = schemas.GetProperty(schema).GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToList();
            Assert.Equal(declared, required);
        }

        await Check("/api/v1/news", "NewsItem", true);
        await Check("/api/v1/topics", "Topic", true);
        await Check("/api/v1/sources", "Source", true);
        await Check("/api/v1/glossary", "GlossaryTerm", true);
        await Check("/api/v1/tools", "Tool", true);
        await Check("/api/v1/stats", "Stats", false);
        await Check("/api/v1/status", "Status", false);
        await Check("/api/v1/search?q=agents", "SearchResults", false);
        await Check("/api/v1/me", "Me", false, token);
    }

    [Fact]
    public void OpenApi_marks_nullable_fields_and_token_only_operations()
    {
        var doc = ApiCatalog.OpenApi("https://x.test");
        var news = doc["components"]!["schemas"]!["NewsItem"]!["properties"]!;
        Assert.True((bool)news["author"]!["nullable"]!);
        Assert.Null(news["title"]!["nullable"]);
        Assert.Equal("date-time", (string)news["publishedAt"]!["format"]!);
        Assert.Equal("array", (string)news["tags"]!["type"]!);

        var me = doc["paths"]!["/me"]!["get"]!;
        Assert.Single(me["security"]!.AsArray());
        Assert.NotNull(me["responses"]!["401"]);
        var newsOp = doc["paths"]!["/news"]!["get"]!;
        Assert.Equal(2, newsOp["security"]!.AsArray().Count); // anonymous or bearer
        Assert.NotNull(newsOp["responses"]!["304"]);
        Assert.NotNull(newsOp["responses"]!["429"]);
        Assert.NotNull(doc["paths"]!["/news/{id}"]!["get"]!["responses"]!["404"]);
    }

    [Fact]
    public void Catalog_operation_ids_and_paths_are_unique_and_documented()
    {
        Assert.Equal(ApiCatalog.Endpoints.Count, ApiCatalog.Endpoints.Select(e => e.OperationId).Distinct().Count());
        Assert.Equal(ApiCatalog.Endpoints.Count, ApiCatalog.Endpoints.Select(e => e.Path).Distinct().Count());
        Assert.All(ApiCatalog.Endpoints, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Summary));
            Assert.False(string.IsNullOrWhiteSpace(e.Description));
            Assert.StartsWith(e.Path == "/" ? "/api/v1" : "/api/v1" + e.Path.Split('{')[0], e.Example);
            foreach (var segment in e.Path.Split('/').Where(s => s.StartsWith('{')))
                Assert.Contains(e.Parameters, p => p.In == "path" && "{" + p.Name + "}" == segment);
        });
        Assert.All(ApiCatalog.Endpoints.Where(e => e.Path.StartsWith("/me")), e => Assert.True(e.RequiresToken));
        Assert.All(ApiCatalog.Endpoints.Where(e => !e.Path.StartsWith("/me")), e => Assert.False(e.RequiresToken));
    }
}
