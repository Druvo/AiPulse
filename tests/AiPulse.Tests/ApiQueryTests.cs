using System.Net;
using System.Text.Json;
using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace AiPulse.Tests;

public class ApiQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static IQueryCollection Query(params (string Key, string Value)[] pairs) =>
        new QueryCollection(pairs.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => new StringValues(g.Select(p => p.Value).ToArray())));

    [Fact]
    public void Paging_defaults_and_bounds()
    {
        var errors = new ApiQueryErrors();
        Assert.Equal(new Paging(1, 20), ApiQuery.ParsePaging(Query(), errors));
        Assert.Equal(new Paging(3, 100), ApiQuery.ParsePaging(Query(("page", "3"), ("pageSize", "100")), errors));
        Assert.False(errors.Any);
    }

    [Theory]
    [InlineData("page", "0")]
    [InlineData("page", "-1")]
    [InlineData("page", "abc")]
    [InlineData("page", "10001")]
    [InlineData("pageSize", "0")]
    [InlineData("pageSize", "101")]
    [InlineData("pageSize", "2.5")]
    public void Out_of_range_paging_is_an_error_naming_the_parameter(string key, string value)
    {
        var errors = new ApiQueryErrors();
        ApiQuery.ParsePaging(Query((key, value)), errors);
        Assert.True(errors.Any);
        Assert.True(errors.ToDictionary().ContainsKey(key));
    }

    [Theory]
    [InlineData("24h", 24 * 60)]
    [InlineData("90m", 90)]
    [InlineData("7d", 7 * 24 * 60)]
    [InlineData("2w", 14 * 24 * 60)]
    [InlineData("1D", 24 * 60)]
    public void Relative_instants_count_back_from_now(string raw, int minutes) =>
        Assert.Equal(Now.AddMinutes(-minutes), ApiQuery.ParseInstant(raw, Now));

    [Fact]
    public void Absolute_instants_without_offset_are_utc()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), ApiQuery.ParseInstant("2026-11-01", Now));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero), ApiQuery.ParseInstant("2026-11-01T08:30:00", Now));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), ApiQuery.ParseInstant("2026-11-01T08:30:00+02:00", Now));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("5y")]
    [InlineData("0h")]
    [InlineData("-3d")]
    [InlineData("h")]
    public void Unparseable_instants_are_null(string raw) => Assert.Null(ApiQuery.ParseInstant(raw, Now));

    [Fact]
    public void News_filter_reports_bad_dates_sort_and_inverted_range()
    {
        var errors = new ApiQueryErrors();
        ApiQuery.ParseNewsFilter(Query(("since", "soon"), ("sort", "random")), Now, errors);
        var d = errors.ToDictionary();
        Assert.Contains("since", d.Keys);
        Assert.Contains("sort", d.Keys);

        var errors2 = new ApiQueryErrors();
        ApiQuery.ParseNewsFilter(Query(("since", "2026-11-04"), ("until", "2026-11-01")), Now, errors2);
        Assert.Contains("since", errors2.ToDictionary().Keys);
    }

    [Fact]
    public void Overlong_text_is_rejected()
    {
        var errors = new ApiQueryErrors();
        Assert.Null(ApiQuery.ParseText(Query(("q", new string('x', 201))), "q", errors));
        Assert.True(errors.Any);
    }

    [Fact]
    public void Sources_accept_repeated_and_comma_separated_values()
    {
        var list = ApiQuery.ParseList(Query(("source", "A, B"), ("source", "c"), ("source", "a")), "source");
        Assert.Equal(new[] { "A", "B", "c" }, list);
    }

    private static List<FeedItem> Items() => FakeApiData.Sample(Now).Items.ToList();

    [Fact]
    public void Tag_filter_is_case_insensitive_and_exact()
    {
        var f = NewsFilter.None with { Tag = "AGENTS" };
        var titles = ApiQuery.Apply(Items(), f).Select(i => i.Title).ToList();
        Assert.Contains("Agents get memory", titles);
        Assert.Contains("New agent framework", titles);
        Assert.DoesNotContain("RAG pipelines in practice", titles);
    }

    [Fact]
    public void Text_query_needs_every_word_somewhere_in_the_item()
    {
        var hits = ApiQuery.Apply(Items(), NewsFilter.None with { Q = "mixture routing" }).ToList();
        Assert.Single(hits);
        Assert.Equal("Mixture of experts explained", hits[0].Title);
        Assert.Empty(ApiQuery.Apply(Items(), NewsFilter.None with { Q = "mixture banana" }));
    }

    [Fact]
    public void Source_category_type_level_and_dates_combine()
    {
        var items = Items();
        Assert.All(ApiQuery.Apply(items, NewsFilter.None with { Sources = new[] { "research weekly" } }), i => Assert.Equal("Research Weekly", i.SourceName));
        Assert.Single(ApiQuery.Apply(items, NewsFilter.None with { Category = "research" }));
        Assert.Single(ApiQuery.Apply(items, NewsFilter.None with { Type = "tutorial" }));
        Assert.Single(ApiQuery.Apply(items, NewsFilter.None with { Level = "beginner" }));

        var lastDay = ApiQuery.Apply(items, NewsFilter.None with { Since = Now.AddHours(-24), Until = Now }).Select(i => i.Title).ToList();
        Assert.Equal(new[] { "Agents get memory", "transformers v5.0.0", "RAG pipelines in practice" }, lastDay);
    }

    [Fact]
    public void Ordering_is_newest_first_by_default_and_oldest_first_on_request_with_stable_ties()
    {
        var same = Now.AddHours(-1);
        var items = new[] { FakeApiData.Item("b", same), FakeApiData.Item("a", same), FakeApiData.Item("c", Now) };
        Assert.Equal(new[] { "c", "a", "b" }, ApiQuery.Apply(items, NewsFilter.None).Select(i => i.Title));
        Assert.Equal(new[] { "a", "b", "c" }, ApiQuery.Apply(items, NewsFilter.None with { OldestFirst = true }).Select(i => i.Title));
    }

    [Fact]
    public void Paginate_slices_and_counts()
    {
        var page = ApiQuery.Paginate(Enumerable.Range(1, 45).ToList(), new Paging(3, 20));
        Assert.Equal(new[] { 41, 42, 43, 44, 45 }, page.Items);
        Assert.Equal(45, page.Total);
        Assert.Equal(3, page.TotalPages);

        var beyond = ApiQuery.Paginate(Enumerable.Range(1, 5).ToList(), new Paging(9, 20));
        Assert.Empty(beyond.Items);
        Assert.Equal(1, beyond.TotalPages);
        Assert.Equal(0, ApiQuery.Paginate(new List<int>(), new Paging(1, 20)).TotalPages);
    }

    [Fact]
    public void Search_spans_every_section_and_ranks_title_hits_first()
    {
        var data = FakeApiData.Sample(Now);
        data.ItemList.Add(FakeApiData.Item("Unrelated title", Now.AddMinutes(-1), summary: "mentions agents in passing"));
        var r = ApiQuery.Search("agents", 10, data.Items, data.Glossary, data.Tools, Now, "https://x.test");

        Assert.Equal("agents", r.Query);
        Assert.NotEmpty(r.News);
        var titles = r.News.Select(n => n.Title).ToList();
        Assert.Equal("Agents get memory", titles[0]);
        // A summary-only hit ranks below every title hit even though it is the newest item.
        Assert.True(titles.IndexOf("Unrelated title") > titles.IndexOf("Old news about agents"));
        Assert.DoesNotContain(r.News, n => n.Type == "Release");
        Assert.Contains(r.Topics, t => t.Tag.Equals("agents", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("https://x.test/api/v1/topics/agents", r.Topics[0].Url);

        var moe = ApiQuery.Search("MoE", 5, data.Items, data.Glossary, data.Tools, Now, "https://x.test");
        Assert.Equal("Mixture of Experts", moe.Glossary.First().Term); // alias match
        var tools = ApiQuery.Search("terminal", 5, data.Items, data.Glossary, data.Tools, Now, "https://x.test");
        Assert.Equal("Claude Code", tools.Tools.Single().Name);
    }

    [Fact]
    public void Search_finds_releases_separately_but_never_future_ones()
    {
        var data = FakeApiData.Sample(Now);
        var r = ApiQuery.Search("release", 10, data.Items, data.Glossary, data.Tools, Now, "https://x.test");
        Assert.DoesNotContain(r.Releases, x => x.Title == "Future release");
        var t = ApiQuery.Search("transformers", 10, data.Items, data.Glossary, data.Tools, Now, "https://x.test");
        Assert.Equal("transformers v5.0.0", t.Releases.Single().Title);
        Assert.Empty(t.News);
    }

    [Fact]
    public void Search_respects_the_limit()
    {
        var data = FakeApiData.Sample(Now);
        Assert.Single(ApiQuery.Search("agent", 1, data.Items, data.Glossary, data.Tools, Now, "o").News);
    }
}

public class ApiMapperTests
{
    [Theory]
    [InlineData("Retrieval-Augmented Generation (RAG)", "retrieval-augmented-generation-rag")]
    [InlineData("  Claude Code ", "claude-code")]
    [InlineData("C#/.NET", "c-net")]
    [InlineData("---", "")]
    public void Slugs(string text, string slug) => Assert.Equal(slug, ApiMapper.Slug(text));

    [Fact]
    public void News_mapping_uses_a_stable_link_hash_id_utc_time_and_a_plain_text_summary()
    {
        var item = FakeApiData.Item("Hello", new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.FromHours(2)), summary: "<p>Hi <b>there</b></p>") with { Author = " ", ImageUrl = "" };
        var dto = ApiMapper.News(item);
        Assert.Equal(40, dto.Id.Length);
        Assert.Equal(dto.Id, ApiMapper.ItemId(item.Link));
        Assert.Equal(PublicFeeds.EntryId(item.Link), "urn:sha1:" + dto.Id);
        Assert.Equal(TimeSpan.Zero, dto.PublishedAt.Offset);
        Assert.Equal(12, dto.PublishedAt.Hour);
        Assert.Equal("Hi there", dto.Summary);
        Assert.Null(dto.Author);
        Assert.Null(dto.ImageUrl);
        Assert.Equal(item.Link, dto.Url);
    }

    [Fact]
    public void Source_mapping_exposes_the_site_never_the_feed_address()
    {
        var s = ApiMapper.Source(new FeedSource { Name = "n", Url = "https://blog.example.com/feeds/secret-path.xml", Category = "News" });
        Assert.Equal("https://blog.example.com", s.Site);
        Assert.Null(ApiMapper.Source(new FeedSource { Name = "n", Url = "file:///etc/passwd", Category = "News" }).Site);
    }

    [Fact]
    public void Serialized_field_names_are_camel_case_and_nulls_are_kept()
    {
        var dto = ApiMapper.News(FakeApiData.Item("x", DateTimeOffset.UnixEpoch));
        var json = JsonSerializer.Serialize(dto, ApiHttp.Json);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "id", "title", "url", "summary", "publishedAt", "source", "category", "type", "level", "tags", "alsoSeenOn", "author", "imageUrl", "readingMinutes" }, names);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("author").ValueKind);
    }
}

public class ApiHttpTests
{
    [Fact]
    public void ETag_is_weak_and_depends_only_on_the_body()
    {
        var a = ApiHttp.ComputeETag("{\"a\":1}"u8);
        Assert.StartsWith("W/\"", a);
        Assert.EndsWith("\"", a);
        Assert.Equal(a, ApiHttp.ComputeETag("{\"a\":1}"u8));
        Assert.NotEqual(a, ApiHttp.ComputeETag("{\"a\":2}"u8));
    }

    [Theory]
    [InlineData("W/\"abc\"", true)]
    [InlineData("\"abc\"", true)]
    [InlineData("\"zzz\", W/\"abc\"", true)]
    [InlineData("*", true)]
    [InlineData("\"zzz\"", false)]
    [InlineData("", false)]
    public void If_none_match_uses_weak_comparison(string header, bool hit) =>
        Assert.Equal(hit, ApiHttp.IfNoneMatchHits(header, "W/\"abc\""));

    [Theory]
    [InlineData("gzip, deflate, br", "br")]
    [InlineData("gzip", "gzip")]
    [InlineData("br;q=0, gzip;q=0.5", "gzip")]
    [InlineData("gzip;q=0", null)]
    [InlineData("identity", null)]
    [InlineData("*", "br")]
    [InlineData("br;q=0.2, gzip;q=0.8", "gzip")]
    [InlineData("", null)]
    public void Encoding_negotiation(string header, string? expected) => Assert.Equal(expected, ApiHttp.ChooseEncoding(header));

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public void Compression_round_trips(string encoding)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"title\":\"agents\"},", 200)));
        var packed = ApiHttp.Compress(body, encoding);
        Assert.True(packed.Length < body.Length / 4);
        using var input = new MemoryStream(packed);
        using Stream unzip = encoding == "br"
            ? new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress)
            : new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        unzip.CopyTo(output);
        Assert.Equal(body, output.ToArray());
    }

    [Theory]
    [InlineData("203.0.113.9", "ip:203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "ip:203.0.113.9")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "ip6:2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "ip6:2001:db8:1:2::/64")]
    public void Anonymous_partitions_are_per_ipv4_address_and_per_ipv6_64(string ip, string expected) =>
        Assert.Equal(expected, PublicApiEndpoints.ClientPartition(IPAddress.Parse(ip)));

    [Fact]
    public void Unknown_address_has_its_own_partition() => Assert.Equal("ip:unknown", PublicApiEndpoints.ClientPartition(null));

    [Theory]
    [InlineData("/topics/{tag}", "/topics/agents", true)]
    [InlineData("/topics/{tag}", "/TOPICS/x", true)]
    [InlineData("/topics/{tag}", "/topics", false)]
    [InlineData("/news", "/news/", true)]
    [InlineData("/me/bookmarks", "/me/unread", false)]
    public void Template_matching(string template, string path, bool matches) =>
        Assert.Equal(matches, PublicApiEndpoints.TemplateMatches(template, path));
}

public class ApiRateLimiterTests
{
    private static readonly ApiRateLimitPolicy Three = new("test", 3, TimeSpan.FromSeconds(60));

    [Fact]
    public void Allows_up_to_the_limit_then_refuses_with_time_to_reset()
    {
        var time = new ManualTime();
        using var limiter = new ApiRateLimiter(time);
        var d1 = limiter.Acquire("ip:1", Three);
        Assert.True(d1.Allowed);
        Assert.Equal(3, d1.Limit);
        Assert.Equal(2, d1.Remaining);
        Assert.Equal(60, d1.ResetSeconds);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, limiter.Acquire("ip:1", Three).Remaining);
        Assert.Equal(0, limiter.Acquire("ip:1", Three).Remaining);

        var refused = limiter.Acquire("ip:1", Three);
        Assert.False(refused.Allowed);
        Assert.Equal(0, refused.Remaining);
        Assert.Equal(50, refused.ResetSeconds);
    }

    [Fact]
    public void The_window_resets_after_it_elapses()
    {
        var time = new ManualTime();
        using var limiter = new ApiRateLimiter(time);
        for (var i = 0; i < 3; i++) limiter.Acquire("k", Three);
        Assert.False(limiter.Acquire("k", Three).Allowed);

        time.Advance(TimeSpan.FromSeconds(60));
        var fresh = limiter.Acquire("k", Three);
        Assert.True(fresh.Allowed);
        Assert.Equal(2, fresh.Remaining);
        Assert.Equal(60, fresh.ResetSeconds);
    }

    [Fact]
    public void Partitions_and_policies_are_counted_separately()
    {
        var time = new ManualTime();
        using var limiter = new ApiRateLimiter(time);
        for (var i = 0; i < 3; i++) limiter.Acquire("ip:1", Three);
        Assert.False(limiter.Acquire("ip:1", Three).Allowed);
        Assert.True(limiter.Acquire("ip:2", Three).Allowed);
        Assert.True(limiter.Acquire("ip:1", Three with { Name = "other" }).Allowed);
    }

    [Fact]
    public void Reset_seconds_is_never_below_one()
    {
        var time = new ManualTime();
        using var limiter = new ApiRateLimiter(time);
        limiter.Acquire("k", Three);
        time.Advance(TimeSpan.FromSeconds(59.9));
        Assert.Equal(1, limiter.Acquire("k", Three).ResetSeconds);
    }

    [Fact]
    public void Idle_partitions_are_swept()
    {
        var time = new ManualTime();
        using var limiter = new ApiRateLimiter(time);
        for (var i = 0; i < 50; i++) limiter.Acquire("ip:" + i, Three);
        Assert.Equal(50, limiter.PartitionCount);

        time.Advance(TimeSpan.FromMinutes(5));
        limiter.Acquire("ip:new", Three);
        Assert.Equal(1, limiter.PartitionCount);
    }

    [Fact]
    public async Task Concurrent_callers_never_get_more_than_the_limit()
    {
        using var limiter = new ApiRateLimiter(new ManualTime());
        var policy = new ApiRateLimitPolicy("c", 100, TimeSpan.FromMinutes(1));
        var results = await Task.WhenAll(Enumerable.Range(0, 400).Select(_ => Task.Run(() => limiter.Acquire("same", policy))));
        Assert.Equal(100, results.Count(r => r.Allowed));
    }
}
