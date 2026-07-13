# ⚡ AiPulse

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download)
[![Self-hosted](https://img.shields.io/badge/self--hosted-%E2%9C%93-brightgreen)](#-quick-start)
[![No API keys required](https://img.shields.io/badge/API%20keys-none%20required-informational)](#-security)

**A standalone, self-hosted dashboard to keep developers current on the AI world** — news, jargon, the right tool for the right task, token-optimization tips, and a structured learning roadmap.

It aggregates ~39 RSS/Atom sources (blogs, news outlets, arXiv, Hacker News, Reddit, YouTube channels, dev.to, Product Hunt, Mastodon, and GitHub release feeds) and pairs them with a curated, editable knowledge base. **It runs with zero AI and no API keys** — the optional AI layer is stubbed behind an interface for when you want it.

> Built with ASP.NET Core + Blazor (Server interactivity), targeting .NET 8. MIT licensed.

<!--
  Screenshots: drop 2-3 PNGs into docs/screenshots/ (dark mode looks best) and reference them here, e.g.:
  ![Dashboard](docs/screenshots/dashboard.png)
  ![News Feed - Grid view](docs/screenshots/news-grid.png)
  ![Timeline heatmap](docs/screenshots/timeline.png)
  A repo with real screenshots converts far better on GitHub/socials than one without.
-->

---

## ✨ Features

- **📡 Live news feed** from ~39 sources, with search and filters (✨ New / Unread / Today / News / Research / Tools / Community), plus richer **content-type** (News/Tutorial/Release/Paper/Discussion/Video), **level** (Beginner/Intermediate/Advanced) and **topic tag** filters, and a **List/Grid** view toggle. Items newer than your last visit are badged **NEW**. Resilient parser with a lenient fallback for awkward feeds. Links have tracking params (`utm_*`, `fbclid`, etc.) stripped automatically.
- **⌨️ Full keyboard navigation** on the News feed — `j`/`k` move focus, `o`/`Enter` opens the focused item, `m` toggles read, `b` toggles bookmark, `?` shows the cheat sheet (inactive while typing in a text field).
- **🚫 Exclude filters** — the inverse of the watchlist: hide items matching a plain-text or regex pattern, so a noisy topic disappears from the feed entirely instead of just being unhighlighted.
- **🔁 Cross-source dedup** — when multiple sources cover the same story, they're merged into one item badged "+N more sources" (title-similarity matching within a time window — no AI).
- **📅 Weekly Digest** — a 7-day rollup: item/source counts, top category, "biggest stories" ranked by how many sources covered them, and a day-by-day breakdown.
- **🏷️ Auto-tagging** — every item is tagged against the curated glossary (RAG, MCP, agents…) by matching its title/summary against each term and its aliases, so tags stay consistent with the Glossary page for free.
- **🔥 Trending panel & 📅 Activity heatmap** — the Dashboard shows top tags/sources over the last 7 days and a GitHub-style contribution heatmap backed by a small persisted history log (`App_Data/feed-history.json`) that grows the longer AiPulse runs. Click any tag or day to jump into a pre-filtered News Feed.
- **🔭 Explore** — trending models & datasets (live from the Hugging Face Hub API), curated benchmark/leaderboard links, recently-popular AI repos (via GitHub's Search API), and a "try a model" directory with Ollama/Hugging Face deep-links.
- **🔔 Desktop notifications** for big tool/model releases (`ContentType: Release`) and your keyword watchlist (a background watcher polls feeds and raises browser notifications).
- **⭐ Keyword watchlist** — flag topics you care about (e.g. `MCP`, `Blazor`, `RAG`); matching items get highlighted and notify you.
- **🎓 Learning Hub** — a 10-module roadmap from tokens → prompting → context engineering → RAG → tool use → agents → MCP → local models → evals → tool selection, **plus a live "Fresh tutorials" feed** pulled from your News sources so it never goes stale. Each module: *why it matters* + concrete steps + key terms.
- **📖 Glossary** — 28 developer-focused definitions, searchable, with aliases so mis-heard terms still resolve (RAG, MCP, agents, agentic loops, headroom, caveman prompting, quantization…), each showing **recent live mentions** pulled straight from the News feed.
- **🧰 Tools & Tips** — a "right tool for the right task" matrix (Claude Code vs Copilot vs Codex vs Cline/Aider/Ollama…) plus token-optimization tips.
- **🔖 Reading List** — bookmark articles (filterable by content-type/level/tag, same as News); persists to disk; one-click **export to an Obsidian-ready Markdown note** with tags carried through as `#hashtags`.
- **👥 Multi-user with roles** — cookie auth backed by a real Users table. Self-registration (`/register`) creates a **Pending** account that an **Admin** approves at `/users`; roles are **Admin** (manages Sources/Users/Backup/Playground) and **User** (everything else). Each account gets its own bookmarks, watchlist, and learning progress.
- **📥 OPML import/export** — bulk-import feeds from any RSS reader's export, or export AiPulse's sources to use elsewhere. Every newly imported URL gets a quick reachability check so dead links are flagged immediately instead of silently failing later. Admin-only, from the Sources page.
- **🕸️ Scrape sites with no RSS feed** — for pages that don't publish RSS/Atom at all, configure XPath selectors (item container, link, title, date) and AiPulse scrapes the HTML directly instead.
- **📄 Full-text fetching** — for feeds that only publish a teaser, flip on "Fetch full article text" and AiPulse pulls the whole article (readability-style extraction, cached per link) so you can read it inline via a "Read full text" toggle instead of clicking out.
- **⚡ WebSub push** (optional) — for the rare feed that still declares a hub, AiPulse subscribes so updates get pushed to it the moment they're published instead of waiting for the next poll. Off unless you set `WebSub:PublicBaseUrl` to your public URL (a hub can't call back to `localhost`); the Sources page shows a **Push** column with live status per source.
- **📈 Source health history** — a rolling 30-day uptime % per source on the Sources page, so "flaky feed" is something you can see, not just guess at.
- **🌗 Light/Dark theme + accent colors + custom CSS**, remembered in the browser across every navigation. Pick from 5 accent presets or drop in your own CSS for full control.
- **🔎 Global search** (`Ctrl`/`Cmd`+`K`) — instantly search across the Glossary, Tools & Tips, and Learning Hub from anywhere in the app.
- **🪝 Outbound webhooks** — send release/watchlist alerts to Slack, Discord, or any generic JSON endpoint, so you're notified even when no tab is open.
- **📱 Mobile client support (Fever API)** — read AiPulse from Reeder, ReadKit, Fiery Feeds, or any other Fever-compatible RSS app, using a separate per-user API password set in Settings.
- **🧩 Custom Dashboard widgets** — embed your own iframe or raw HTML snippet (a status page, another dashboard, a note) right on the Dashboard.
- **❓ In-app Help page** (`/help`) — what AiPulse is, a typical daily workflow, and a full page-by-page reference, plus a dismissible welcome banner for first-time visitors.

Sources are managed dynamically from the **Sources page** (add/edit/remove feeds, no restart needed) — see [below](#customize-the-content). Glossary, Tools, Practices, and Learning Hub content stays as **editable JSON files in `Data/`** — no code needed to grow it.

---

## 🚀 Quick start

```bash
git clone https://github.com/Druvo/AiPulse.git
cd AiPulse
dotnet run
```

Open the printed URL (default **http://localhost:5257**) and sign in with the bootstrapped Admin account:

- Username: `admin`
- Password: `changeme`  ← **change this** (see [Security](#-security))

That account is seeded once, on first run, from the `Auth` section of `appsettings.json`. Anyone else can create an account at `/register`, but new accounts stay **Pending** until you (the Admin) approve them at `/users`.

> Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer.

---

## ⚙️ Configuration (`appsettings.json`)

```jsonc
{
  "Auth": {
    "Username": "admin",
    "Password": "changeme",   // plaintext (quick start)
    "PasswordHash": ""         // PBKDF2 hash (preferred) — takes precedence over Password
  },
  "Notifications": {
    "PollMinutes": 15,         // how often the watcher checks feeds
    "NotifyReleases": true,    // alert on new items in the Tools (GitHub releases) category
    "NotifyWatchlist": true,   // alert on watchlist keyword hits
    "MaxPerPoll": 15
  },
  "WebSub": {
    "PublicBaseUrl": "",       // e.g. https://your-domain.example — empty = WebSub fully disabled
    "RequestedLeaseSeconds": 432000
  }
}
```

Keep personal overrides (your password hash, etc.) out of source control by putting them in `appsettings.Development.json` (git-ignored).

### Customize the content
**Sources are dynamic** — add, edit, or disable feeds from the **Sources page** in the app; changes apply immediately, no restart needed. They're stored in SQLite (`App_Data/aipulse.db`). `Data/sources.json` is only read once, to **seed** the database the first time you run AiPulse — after that it's not read again, so editing it later has no effect (edit through the UI instead).

Everything else is still just editable JSON in `Data/`:

| File | Adds to |
|------|---------|
| `glossary.json` | glossary terms |
| `tools.json` | the tools matrix |
| `practices.json` | best-practice / token tips |
| `learning.json` | Learning Hub modules |
| `benchmarks.json` | Explore page's benchmark/leaderboard links |
| `model-directory.json` | Explore page's "Try a model" directory |

Handy feed URL patterns (for adding sources via the Sources page): GitHub releases `https://github.com/<owner>/<repo>/releases.atom`, subreddit `https://www.reddit.com/r/<name>/.rss`, most blogs `/feed` or `/rss.xml`, Substack `https://<name>.substack.com/feed`, YouTube channel `https://www.youtube.com/feeds/videos.xml?channel_id=<id>`, dev.to tag `https://dev.to/feed/tag/<tag>`, Mastodon tag `https://<instance>/tags/<tag>.rss`.

Each source also carries `ContentType` (News/Tutorial/Release/Paper/Discussion/Video), `Level` (Beginner/Intermediate/Advanced), and base `Tags` — shown on the Sources page and inherited by every item it produces (further enriched per-item by matching against the glossary).

### Watchlist, webhooks, mobile access & Obsidian export
Set these in the in-app **Settings** page (stored per-user in `App_Data/users/{username}/reading-state.json`, not source control):
- **Keyword watchlist** — topics to highlight & notify on.
- **Outbound webhook** — a Slack/Discord/generic URL for release + watchlist alerts; auto-detected by URL, with a "send test" button.
- **Fever API password** — a separate password (not your login) for mobile RSS apps; the app then shows the server URL, username, and this password to paste into your client.
- **Dashboard widgets** — add/remove iframe or HTML embeds shown on the Dashboard.
- **Obsidian export folder** — where **Export to Obsidian** writes `AiPulse Reading List.md` (leave blank to use `App_Data/exports`).

---

## 🔐 Security

- All pages require sign-in except `/login` and `/register`.
- Registration never auto-approves — new accounts are **Pending** until an Admin approves them at `/users`, so a public-facing instance can't be self-signed-up-into by strangers.
- Sources, Users, Backup/Restore, and Playground (talks to the host's local Ollama instance — a shared machine resource, not per-user) are **Admin-only**; everything else (News, Learning Hub, Bookmarks, etc.) is open to any approved account.
- `/websub/callback/*` is intentionally unauthenticated (external hubs are anonymous servers) — it's instead secured by matching the hub's topic against what was actually subscribed to, and requiring a valid per-subscription HMAC signature on every content push. Fully inert unless you set `WebSub:PublicBaseUrl`.
- `/fever/` is likewise unauthenticated at the ASP.NET Core level (mobile RSS clients don't send AiPulse's login cookie) — it's secured instead by the Fever protocol's own `api_key`, checked per-user inside `FeverApiService`. No-op until a user sets a Fever API password in Settings.
- The **Dashboard widgets "HTML" type renders raw markup unsanitized** (`MarkupString`) — it's opt-in, per-user, and runs as your own logged-in session, so only use it with content you trust (same trust model as the custom-CSS box).
- For a real bootstrap password, generate a hash (Development only):
  `http://localhost:5257/auth/hash?password=YOUR-PASSWORD` → paste the result into `Auth:PasswordHash` and clear `Auth:Password`. Accounts created via `/register` or `/users` are always hashed automatically.
- Keep it on `localhost` unless you need otherwise. If you expose it, run behind HTTPS.

---

## 🏗️ How it works

```
Data/                 Curated JSON (glossary + tools + tips + learning + benchmarks + model directory);
                      sources.json is a one-time DB seed only, see above
App_Data/             aipulse.db (sources, users, chat history), users/{username}/reading-state.json,
                      feed-history.json, source-health.json — all auto-created, git-ignored
Models/               FeedItem, GlossaryTerm, ToolEntry, LearningModule, BookmarkItem, SourceRecord,
                      AppUser, ExcludeFilter, Alert…
Services/
  FeedAggregatorService   Fetches + normalizes all feeds, dedupes, auto-tags (HTTP/XML only — no AI)
  ContentExtractorService Full-text article extraction (readability heuristic) + XPath HTML scraping support
  WebSubService           Subscribe/verify/renew WebSub (PubSubHubbub) push, HMAC-signed content pushes
  WebhookService          Posts release/watchlist alerts to Slack/Discord/generic JSON endpoints
  FeverApiService         Fever API (mobile RSS client compatibility) - groups/feeds/items/mark
  OpmlService             Bulk import/export of sources, with reachability checks on import
  SourceHealthService     Rolling 30-day per-source success/fail history
  FeedWatcherService      Background poller that raises release/watchlist alerts + feeds FeedHistoryService
  FeedHistoryService      Deduped, rolling (90-day) history of items, backing the activity heatmap
  NotificationService     In-memory alert hub the UI bell subscribes to
  KnowledgeBaseService    Loads Data/*.json + the DB-backed, dynamically managed Sources list
  AiPulseDbContext        EF Core / SQLite context (Sources, Users, ChatSessions)
  HuggingFaceService      Trending models/datasets from the public HF Hub API (Explore page)
  GitHubTrendingService   Recently-popular AI repos via GitHub's Search API (Explore page)
  OllamaService           Talks to a local Ollama instance for the Playground
  ReadingStateService     Per-user bookmarks/watchlist/exclude-filters/read/progress (Scoped per circuit)
  ChatHistoryService      Per-user Playground chat history (Scoped per circuit)
  ObsidianExportService   Writes the reading list to a Markdown note (tags included as #hashtags)
  UserService             Registration, admin approval, roles, login validation
  PasswordHasher          PBKDF2 hashing shared by UserService and the bootstrap Admin account
  BackupService           Zips/restores all of App_Data
  ISummarizer             Optional AI hook (NullSummarizer = off)
Components/Pages/     Home, News, Digest, Explore, Learn, Glossary, Tools, Bookmarks, Settings, Sources,
                      Users, Playground, Help, Login, Register, AccessDenied
Components/Shared/    Icon, GlobalSearch, ActivityHeatmap, TrendingPanel, MiniTimeline
```

### Enabling the optional AI layer
The app is standalone. To add AI digests / "Explain with AI":
1. Implement `Services/ISummarizer.cs` (e.g. call the Claude API).
2. In `Program.cs`, replace `AddSingleton<ISummarizer, NullSummarizer>()` with your implementation.

The Glossary and Dashboard already reveal AI buttons when `ISummarizer.Enabled` is `true`.

---

## 🤝 Contributing

PRs welcome — especially **new sources** and **glossary terms**. The lowest-friction contribution is editing a file in `Data/`. Please keep JSON valid and entries concise and developer-focused.

## 📄 License

[MIT](LICENSE).
