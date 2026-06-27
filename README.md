# ⚡ AiPulse

**A standalone, self-hosted dashboard to keep developers current on the AI world** — news, jargon, the right tool for the right task, token-optimization tips, and a structured learning roadmap.

It aggregates ~30 RSS/Atom sources (blogs, news outlets, arXiv, Hacker News, Reddit, and GitHub release feeds) and pairs them with a curated, editable knowledge base. **It runs with zero AI and no API keys** — the optional AI layer is stubbed behind an interface for when you want it.

> Built with ASP.NET Core + Blazor (Server interactivity), targeting .NET 8. MIT licensed.

---

## ✨ Features

- **📡 Live news feed** from ~30 sources, with search and filters (✨ New / Today / News / Research / Tools / Community). Items newer than your last visit are badged **NEW**. Resilient parser with a lenient fallback for awkward feeds.
- **🔔 Desktop notifications** for big tool/model releases and your keyword watchlist (a background watcher polls feeds and raises browser notifications).
- **⭐ Keyword watchlist** — flag topics you care about (e.g. `MCP`, `Blazor`, `RAG`); matching items get highlighted and notify you.
- **🎓 Learning Hub** — a 10-module roadmap from tokens → prompting → context engineering → RAG → tool use → agents → MCP → local models → evals → tool selection. Each module: *why it matters* + concrete steps + key terms.
- **📖 Glossary** — 28 developer-focused definitions, searchable, with aliases so mis-heard terms still resolve (RAG, MCP, agents, agentic loops, headroom, caveman prompting, quantization…).
- **🧰 Tools & Tips** — a "right tool for the right task" matrix (Claude Code vs Copilot vs Codex vs Cline/Aider/Ollama…) plus token-optimization tips.
- **🔖 Reading List** — bookmark articles; persists to disk; one-click **export to an Obsidian-ready Markdown note**.
- **🔐 Login** — cookie auth with a single configured user (plaintext for quick start, or PBKDF2 hash for real security).
- **🌗 Light/Dark theme**, remembered in the browser.

Everything except the news fetch is driven by **editable JSON files in `Data/`** — no code needed to grow it.

---

## 🚀 Quick start

```bash
git clone https://github.com/Druvo/AiPulse.git
cd AiPulse
dotnet run
```

Open the printed URL (default **http://localhost:5257**) and sign in:

- Username: `admin`
- Password: `changeme`  ← **change this** (see [Security](#-security))

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

### Customize the content — just edit JSON in `Data/`
| File | Adds to |
|------|---------|
| `sources.json` | the news feeds (any RSS/Atom URL) |
| `glossary.json` | glossary terms |
| `tools.json` | the tools matrix |
| `practices.json` | best-practice / token tips |
| `learning.json` | Learning Hub modules |

Handy feed URL patterns: GitHub releases `https://github.com/<owner>/<repo>/releases.atom`, subreddit `https://www.reddit.com/r/<name>/.rss`, most blogs `/feed` or `/rss.xml`, Substack `https://<name>.substack.com/feed`.

### Watchlist & Obsidian export
Set these in the in-app **Settings** page (stored in `App_Data/`, not source control):
- **Keyword watchlist** — topics to highlight & notify on.
- **Obsidian export folder** — where **Export to Obsidian** writes `AiPulse Reading List.md` (leave blank to use `App_Data/exports`).

---

## 🔐 Security

- All pages require sign-in except `/login`.
- For a real password, generate a hash (Development only):
  `http://localhost:5257/auth/hash?password=YOUR-PASSWORD` → paste the result into `Auth:PasswordHash` and clear `Auth:Password`.
- Keep it on `localhost` unless you need otherwise. If you expose it, run behind HTTPS and use the hashed password.

---

## 🏗️ How it works

```
Data/                 Curated JSON (feeds + glossary + tools + tips + learning)
App_Data/             reading-state.json (bookmarks, watchlist, last visit) — auto-created, git-ignored
Models/               FeedItem, GlossaryTerm, ToolEntry, LearningModule, BookmarkItem, Alert…
Services/
  FeedAggregatorService   Fetches + normalizes all feeds (HTTP/XML only — no AI)
  FeedWatcherService      Background poller that raises release/watchlist alerts
  NotificationService     In-memory alert hub the UI bell subscribes to
  KnowledgeBaseService    Loads the Data/*.json files
  ReadingStateService     Bookmarks / watchlist / read / last-visit persistence
  ObsidianExportService   Writes the reading list to a Markdown note
  AuthService             Validates login (PBKDF2 or plaintext)
  ISummarizer             Optional AI hook (NullSummarizer = off)
Components/Pages/     Home, News, Learn, Glossary, Tools, Bookmarks, Settings, Sources, Login
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
