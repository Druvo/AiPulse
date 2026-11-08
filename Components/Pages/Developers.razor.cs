using AiPulse.Services.Api;
using Microsoft.AspNetCore.Components;

namespace AiPulse.Components.Pages;

public partial class Developers
{

    private PublicApiOptions _options = new();

    protected override void OnInitialized() => Config.GetSection("PublicApi").Bind(_options);

    private string Origin => Nav.BaseUri.TrimEnd('/');
    private static string ApiPath => PublicApiEndpoints.BasePath;
    private string ApiBase => Origin + ApiPath;
    private string WindowText => _options.WindowSeconds == 60 ? "minute" : $"{_options.WindowSeconds} seconds";

    private static string ParamMeta(ApiParameterDoc p)
    {
        var bits = new List<string> { p.In == "path" ? "path" : p.Type };
        if (p.Required || p.In == "path") bits.Add("required");
        if (p.Minimum is { } min && p.Maximum is { } max) bits.Add($"{min}–{max}");
        if (p.Default is not null) bits.Add("default " + p.Default);
        if (p.Enum is not null) bits.Add(string.Join(" | ", p.Enum));
        return string.Join(" · ", bits);
    }

    private static readonly (string Code, int Status, string Text)[] ErrorCodes =
    {
        ("invalid-parameter", 400, "A query parameter is malformed or out of range. Fix what \"errors\" lists."),
        ("unauthorized", 401, "This endpoint needs a personal token and none was sent."),
        ("invalid-token", 401, "A token was sent but is unknown, expired, revoked, or its account is inactive. Create a new one in Settings."),
        ("insufficient-scope", 403, "The token lacks the scope the endpoint needs."),
        ("not-found", 404, "No such endpoint, or no item with that id, tag or slug."),
        ("method-not-allowed", 405, "The API is read-only; only GET, HEAD and OPTIONS are accepted."),
        ("rate-limited", 429, "Too many requests. Wait for Retry-After seconds."),
        ("server-error", 500, "Something failed on AiPulse's side. Retry shortly."),
    };

    private string EnvelopeExample => $$"""
        {
          "data": [
            {
              "id": "3f2a…",
              "title": "A new open-weights agent model",
              "url": "https://example.com/post",
              "summary": "Plain-text teaser…",
              "publishedAt": "2026-11-05T09:12:00+00:00",
              "source": "Example Blog",
              "category": "News",
              "type": "News",
              "level": "Intermediate",
              "tags": ["agents", "open-source"],
              "alsoSeenOn": [],
              "author": null,
              "imageUrl": null,
              "readingMinutes": 4
            }
          ],
          "meta": { "page": 1, "pageSize": 20, "total": 312, "totalPages": 16 },
          "links": {
            "self": "{{ApiBase}}/news",
            "first": "{{ApiBase}}/news?page=1",
            "prev": null,
            "next": "{{ApiBase}}/news?page=2",
            "last": "{{ApiBase}}/news?page=16"
          }
        }
        """;

    private string ProblemExample => $$"""
        {
          "type": "{{Origin}}/developers#error-invalid-parameter",
          "title": "Invalid query parameter",
          "status": 400,
          "detail": "One or more query parameters are not valid. See \"errors\".",
          "instance": "{{ApiPath}}/news?pageSize=500",
          "errors": { "pageSize": ["Must be a whole number from 1 to {{ApiQuery.MaxPageSize}}."] }
        }
        """;

    private string CurlExample => $$"""
        # Public: search everything
        curl -s "{{ApiBase}}/search?q=mixture%20of%20experts&limit=3"

        # Conditional request: repeat with the ETag you got; 304 means nothing changed
        curl -si "{{ApiBase}}/topics" -H 'If-None-Match: W/"…"'

        # Your bookmarks (token from Settings > API tokens)
        curl -s "{{ApiBase}}/me/bookmarks" -H "Authorization: Bearer $AIPULSE_TOKEN"
        """;

    private string JsExample => $$"""
        const base = "{{ApiBase}}";

        // Public data: no key, CORS-enabled
        const res = await fetch(`${base}/news?tag=agents&pageSize=10`);
        if (res.status === 429) {
          const wait = Number(res.headers.get("Retry-After") ?? 60);
          throw new Error(`Rate limited, retry in ${wait}s`);
        }
        const { data, links } = await res.json();
        for (const item of data) console.log(item.publishedAt, item.title, item.url);
        // links.next is the next page, or null on the last one

        // Your own data (server-side only: keep the token secret)
        const me = await fetch(`${base}/me/unread?since=24h`, {
          headers: { Authorization: `Bearer ${process.env.AIPULSE_TOKEN}` },
        }).then((r) => r.json());
        """;

    private string PythonExample => $$"""
        import json, os, urllib.request

        BASE = "{{ApiBase}}"

        def get(path, token=None):
            req = urllib.request.Request(BASE + path, headers={"Accept": "application/json"})
            if token:
                req.add_header("Authorization", f"Bearer {token}")
            with urllib.request.urlopen(req) as resp:
                return json.load(resp)

        # Every headline from the last day, following the page links
        url = "/news?since=24h&pageSize=100"
        while url:
            page = get(url)
            for item in page["data"]:
                print(item["publishedAt"], item["title"])
            nxt = page["links"]["next"]
            url = nxt[len(BASE):] if nxt else None

        # Your bookmarks
        print(get("/me/bookmarks", os.environ["AIPULSE_TOKEN"])["meta"]["total"])
        """;
}
