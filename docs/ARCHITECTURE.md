# AiPulse architecture

A short map of how the app is put together, for anyone changing it. AiPulse is a single ASP.NET Core 8 process:
Blazor (static server rendering plus Interactive Server components over SignalR), a SQLite database and some JSON
files under `App_Data/`, and two background jobs. No external database, queue or cache.

## Request flow

```
browser ── nginx (TLS, WebSockets) ── Kestrel :5257
   │
   ├─ UseForwardedHeaders     X-Forwarded-For/Proto from the proxy (Program.cs explains why the proxy list is open)
   ├─ UseExceptionHandler/HSTS (outside Development)
   ├─ UseHttpsRedirection
   ├─ UseStaticFiles          wwwroot, Cache-Control: no-cache (revalidate every time; 304 when unchanged)
   ├─ UseAntiforgery          every Blazor form post (e.g. /login) must carry its token
   ├─ UseAuthentication       cookie "AiPulse.Auth" (30 days, sliding); security stamp re-checked once a minute
   ├─ UseAuthorization        [Authorize] / [Authorize(Roles = "Admin")] on pages and endpoints
   └─ endpoints
        ├─ minimal APIs in Program.cs: /health, /auth/*, /sitemap.xml, /robots.txt, /feed.xml, /backup/*,
        │  /opml/*, /favicon-proxy/{host}, /websub/callback/{id}, /fever/
        ├─ MapOpsEndpoints(): /ops/status.json, /ops/audit.csv, /ops/backups/{name}  (Admin)
        └─ MapRazorComponents<App>(): every page in Components/Pages
```

Pages are rendered on the server. A page with `@rendermode InteractiveServer` is prerendered to HTML and then
attached to a SignalR circuit, which is why nginx must pass WebSocket upgrades and keep long timeouts. A page
without a render mode (e.g. `Login.razor`) is plain server-rendered HTML with a normal form post.

Who may open what:

- Public pages and machine endpoints are listed once in `Services/PublicSite.cs`. The public menus, `sitemap.xml`
  and `robots.txt` are all built from that list. A test fails if a page needs no sign-in and is missing from it.
- Everything else carries `[Authorize]`. Admin screens use `[Authorize(Roles = "Admin")]`. The roles are `Admin`,
  `Curator` and `User` (`Models/UserModels.cs`).
- Anonymous visitors to a protected page are redirected to `/login?ReturnUrl=…`. A signed-in user without the
  role is redirected to `/access-denied`.

## Services (Services/)

| Area | Services | Notes |
|---|---|---|
| Feeds | `FeedAggregatorService`, `FeedXmlParser`, `FeedScraper`, `FeedDeduplicator`, `FeedPollOrder`, `FeedDomainThrottle`, `ContentExtractorService` | Fetches enabled sources (5 at a time, per-host cooldowns, 429 back-off), parses RSS/Atom or scrapes with XPath, tags items from the glossary, then dedupes. The result is cached for 15 min. Reddit-hosted sources are fetched 4 per poll in a rotating window (`FeedPollOrder.RedditBatchSize`). |
| History & health | `FeedHistoryService`, `SourceHealthService`, `SiteStatsService` | Rolling 90-day item history, and a 30-day per-source success record with response times. |
| Push | `WebSubService`, `WebhookService`, `NotificationService` | WebSub subscriptions (only when `WebSub:PublicBaseUrl` is set), outbound Slack/Discord/JSON webhooks, and the in-memory alert bell (last 100). |
| Knowledge base | `KnowledgeBaseService` | Curated `Data/*.json` (glossary, tools, learning, benchmarks, models, courses) plus DB-backed sources and free APIs. |
| Users & auth | `UserService`, `AuthService`, `ExternalAuthService`, `OAuthSettingsService`, `ClerkAuthService`, `PasswordHasher` | Local accounts (PBKDF2), lockout, approval, roles, reset tokens and the audit log. Google, GitHub and Clerk sign-in are configured at runtime and stored in the DB. |
| Per-user state | `ReadingStateService` (partial classes), `ChatHistoryService`, `ProfileService`, `FollowService`, `CommentService` | `ReadingStateService` is scoped per circuit and resolves the current user. |
| Explore | `HuggingFaceService`, `GitHubTrendingService`, `TrendingRepoHistoryService`, `FreeApiDiscoveryService`, `SystemInfoService`, `OllamaService`, `ModelUsageService` | Live lookups, cached to App_Data. |
| AI (`Services/Ai`) | `AddAiAssistant()`: `AiSettingsService` (switches and the `IAiGate`), `LlmClient` (providers from `Llm:Providers`: Ollama or OpenAI-compatible, tried by priority), `KnowledgeCorpus`/`Bm25Index` (RAG), `RagAgent` (Ask AiPulse), `LlmSummarizer`, `SummaryStore` | Every model call asks `IAiGate` first. `Ai:Enabled=false` in config is a hard off switch. |
| Ops (`Services/Ops`) | `AddOpsConsole()`: `PollStatusTracker`, `OpsStatusService`, `OpsBackupStore`, `AuditLogQueryService` | Read-only status for `/ops` and `/ops/status.json`. The only write is backup creation. |
| Data portability | `BackupService`, `OpmlService`, `ObsidianExportService`, `FeverApiService` | Zip or restore App_Data, OPML import and export, Markdown export, and the Fever API for mobile RSS readers. |

Most services are singletons. `ReadingStateService`, `ToastService`, `ObsidianExportService` and
`ChatHistoryService` are scoped. DB access always goes through `IDbContextFactory<AiPulseDbContext>`, which gives
short-lived contexts that singletons can use safely.

## Background jobs

| Job | Schedule | What it does |
|---|---|---|
| `FeedWatcherService` | 5 s after start, then every `Notifications:PollMinutes` (default 15; `appsettings.json` ships 60) | Forces a full poll (`FeedAggregatorService.GetAsync(force: true)`), records new items into history, renews expiring WebSub leases, raises alerts (watchlist hits, grouped releases, tag spikes) and sends webhooks. The first poll of a brand-new install seeds history without alerting. |
| `FreeApiDiscoveryService` | 2 min after start, then every 24 h | Looks for new free AI APIs and checks the listed links. |

`PollStatusTracker` watches the aggregator's `PartialProgress` and `CacheUpdated` events, so `/ops` can show how
the last poll went without changing the aggregator.

## Data stores

Everything that changes at runtime lives in `App_Data/` next to the binaries. That folder is the unit of
backup and restore.

| Path | Contents |
|---|---|
| `App_Data/aipulse.db` (+ `-wal`/`-shm`) | SQLite tables: Sources, Users, AuditLog, PasswordResetTokens, OAuthProviderSettings, ExternalLogins, ChatSessions/ChatMessages, ModelUsage, WebSubSubscriptions, FreeApis/FreeApiCandidates/DiscoveryRunLog, ContentCandidates, Follows, Comments, UserProfiles. The schema is made with `EnsureCreated`, and tables or columns added later are created by hand at startup (see `UserService.EnsureSchemaAsync` and `KnowledgeBaseService.EnsureSourceColumnsAsync`). There are no EF migrations. |
| `App_Data/dp-keys/` | ASP.NET Data Protection keys. They sign auth cookies and encrypt stored OAuth secrets. If you lose them, everyone is signed out and OAuth secrets must be re-entered. |
| `App_Data/feed-history.json` | Deduped item history (90 days) |
| `App_Data/source-health.json` | Per-source daily success, last error and response times (30 days) |
| `App_Data/users/<name>/reading-state.json` | Bookmarks, read state, watchlist, filters, webhooks and settings for each user |
| `App_Data/ai-settings.json`, `summaries.jsonl` | AI switches and saved item summaries |
| `App_Data/github-trending-cache.json`, `huggingface-cache.json`, `fever-item-ids.json`, `exports/` | Caches, Fever id map, Obsidian exports |
| `Data/*.json` (shipped with the app) | The curated knowledge base. `sources.json` and `free-apis.json` only seed an empty DB. **Approving a Glossary, Tools or Learning candidate writes back into `Data/*.json`.** |
| `backups/` (or `Ops:BackupDirectory`) | Zips made by "Create backup now" on `/ops`. It sits outside App_Data on purpose. |

## Configuration

`appsettings.json` holds the defaults. Each environment adds `appsettings.{Environment}.json` (git-ignored) and
environment variables (`Section__Key`). The main sections are `Auth` (bootstrap admin; `RequireApproval`),
`Notifications` (poll interval and alert kinds), `WebSub`, `Smtp` (password-reset email; without it, reset links
go to the log), `Ai`, `Llm:Providers`, `Ollama:BaseUrl` and `Ops:BackupDirectory`.

## Where to extend

- **A new page:** add `Components/Pages/X.razor` and give it `[Authorize]` (or list it in `PublicSite`). Put its
  CSS in a scoped `X.razor.css` and use the `--ap-*` tokens from `wwwroot/app.css`. The end-to-end suite picks up
  the new route automatically and checks its access rules.
- **A new endpoint:** write a `MapXxx()` extension in a new file and call it from Program.cs, as
  `OpsConsoleExtensions.MapOpsEndpoints` does. Put `.RequireAuthorization(...)` on anything that is not public.
- **A new background job:** add a `BackgroundService` and `AddHostedService` it. The end-to-end factory removes
  the app's hosted services, so tests stay deterministic.
- **A new feed behaviour:** add pure helpers next to `FeedPollOrder` and `FeedXmlParser`, with unit tests, and keep
  `FeedAggregatorService` as orchestration only.
- **A new AI feature:** ask `IAiGate` before you call `ILlmClient`, and add a switch to `AiSettings` if admins
  should be able to turn it off.

## Tests

`tests/AiPulse.Tests` (xUnit) is outside `AiPulse.sln` on purpose. Run
`dotnet test tests/AiPulse.Tests --filter "FullyQualifiedName!~LiveGemini"`. The `E2E/` folder hosts the real app
with `WebApplicationFactory` in a temporary content root, with outbound HTTP faked, so it never touches the
network. CI runs the same command on ubuntu-latest (`.github/workflows/ci.yml`).
