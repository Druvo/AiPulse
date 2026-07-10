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

- **📡 Live news feed** from ~39 sources, with search and filters (✨ New / Unread / Today / News / Research / Tools / Community), plus richer **content-type** (News/Tutorial/Release/Paper/Discussion/Video), **level** (Beginner/Intermediate/Advanced) and **topic tag** filters, and a **List/Grid** view toggle. Items newer than your last visit are badged **NEW**. Resilient parser with a lenient fallback for awkward feeds.
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
- **👥 Multi-user with roles** — cookie auth backed by a real Users table. Self-registration (`/register`) creates a **Pending** account that an **Admin** approves at `/users`; roles are **Admin** (manages Sources/Users/Backup) and **User** (everything else). Each account gets its own bookmarks, watchlist, learning progress, and Playground chat history.
- **📥 OPML import/export** — bulk-import feeds from any RSS reader's export, or export AiPulse's sources to use elsewhere. Admin-only, from the Sources page.
- **🌗 Light/Dark theme**, remembered in the browser across every navigation.
- **🔎 Global search** (`Ctrl`/`Cmd`+`K`) — instantly search across the Glossary, Tools & Tips, and Learning Hub from anywhere in the app.

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

### Watchlist & Obsidian export
Set these in the in-app **Settings** page (stored in `App_Data/`, not source control):
- **Keyword watchlist** — topics to highlight & notify on.
- **Obsidian export folder** — where **Export to Obsidian** writes `AiPulse Reading List.md` (leave blank to use `App_Data/exports`).

---

## 🔐 Security

- All pages require sign-in except `/login` and `/register`.
- Registration never auto-approves — new accounts are **Pending** until an Admin approves them at `/users`, so a public-facing instance can't be self-signed-up-into by strangers.
- Sources, Users, and Backup/Restore are **Admin-only**; everything else (News, Learning Hub, Bookmarks, Playground, etc.) is open to any approved account.
- For a real bootstrap password, generate a hash (Development only):
  `http://localhost:5257/auth/hash?password=YOUR-PASSWORD` → paste the result into `Auth:PasswordHash` and clear `Auth:Password`. Accounts created via `/register` or `/users` are always hashed automatically.
- Keep it on `localhost` unless you need otherwise. If you expose it, run behind HTTPS.

---

## 🏗️ How it works

```
Data/                 Curated JSON (glossary + tools + tips + learning + benchmarks + model directory);
                      sources.json is a one-time DB seed only, see above
App_Data/             aipulse.db (sources), reading-state.json, feed-history.json — auto-created, git-ignored
Models/               FeedItem, GlossaryTerm, ToolEntry, LearningModule, BookmarkItem, SourceRecord, Alert…
Services/
  FeedAggregatorService   Fetches + normalizes all feeds, auto-tags items from the glossary (HTTP/XML only — no AI)
  FeedWatcherService      Background poller that raises release/watchlist alerts + feeds FeedHistoryService
  FeedHistoryService      Deduped, rolling (90-day) history of items, backing the activity heatmap
  NotificationService     In-memory alert hub the UI bell subscribes to
  KnowledgeBaseService    Loads Data/*.json + the DB-backed, dynamically managed Sources list
  AiPulseDbContext        EF Core / SQLite context for Sources (App_Data/aipulse.db)
  HuggingFaceService      Trending models/datasets from the public HF Hub API (Explore page)
  GitHubTrendingService   Recently-popular AI repos via GitHub's Search API (Explore page)
  ReadingStateService     Bookmarks / watchlist / read / last-visit / learning-module-progress persistence
  ObsidianExportService   Writes the reading list to a Markdown note (tags included as #hashtags)
  AuthService             Validates login (PBKDF2 or plaintext)
  ISummarizer             Optional AI hook (NullSummarizer = off)
Components/Pages/     Home, News, Explore, Learn, Glossary, Tools, Bookmarks, Settings, Sources, Login
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
