# AiPulse Roadmap

A running backlog of ideas, most of them sourced from comparing AiPulse against real competitors (Glance, Miniflux, FreshRSS, Horizon, auto-news, RSSbrew, ai-news-aggregator) — see the analysis this list grew out of for citations. Grouped by whether it fits AiPulse's core positioning: **standalone, self-hosted, zero AI required**.

Status tags: ✅ done · 🟢 fits philosophy, no AI needed · 🟡 needs a design call · 🔴 conflicts with positioning (listed for completeness, not recommended)

---

## ✅ Shipped

- OPML import/export (bulk source management)
- Cross-source deduplication (title-similarity merge, "+N more sources")
- Weekly Digest page (7-day rollup, biggest multi-source stories)
- Multi-user accounts: registration, admin approval, Admin/User roles, per-user bookmarks/watchlist/chat history
- Desktop notifications (releases + watchlist), keyword watchlist
- Learning Hub, Glossary, Tools & Tips, Explore (HF/GitHub trending), Playground (local Ollama chat)
- Backup/restore, Obsidian export, global search, light/dark theme

---

## 🟢 High-value, matches "no AI needed" philosophy

| Idea | Source | Notes |
|---|---|---|
| **Boolean/regex source filters** | RSSbrew | Watchlist today is substring-match only. Real include/exclude rules (AND/OR/NOT on title/link/description) would let you mute a noisy source or topic without disabling the whole feed. |
| **Web scraping for non-RSS sites (XPath)** | FreshRSS | The #1 reason a source can't be added today is "no RSS feed." An XPath-based scraper closes that gap. |
| **Full-text fetching for summary-only feeds** | Miniflux | Some feeds only ship a teaser; auto-fetching the full article means reading everything in AiPulse instead of clicking out. |
| **Strip tracking pixels/params from feed content** | Miniflux | Privacy hygiene — Miniflux treats this as core, not an afterthought. Cheap to add to the existing `CleanText` pipeline. |
| **Source health/uptime history** | ai-news-aggregator | The Sources page already shows a live "N failed" streak; persisting it (e.g. last 30 days of success/fail per source) turns it into a real health report. |
| **WebSub/PubSubHubbub push** | FreshRSS | Near-instant updates instead of waiting for the next poll, for publishers that support it. Bigger lift — needs a public callback endpoint. |
| **Full keyboard navigation** (j/k next/prev, `m` mark read, etc.) | Miniflux, FreshRSS | Power-user navigation for the News Feed and Reading List without touching the mouse. |
| **OPML round-trip test / source validation on import** | — | Ping each imported URL once and flag dead links immediately instead of waiting for the first poll. |

## 🟡 Valuable, needs a design decision

| Idea | Source | The decision |
|---|---|---|
| **Outbound webhooks** (Slack/Discord/generic POST) | Horizon, Miniflux, NewsBlur (IFTTT/Zapier) | Desktop notifications only fire while a tab is open. A webhook reaches you anywhere — but needs per-user webhook URL storage and a retry/failure policy. |
| **Mobile client API compatibility** (Google Reader / Fever API) | FreshRSS, Miniflux | Would let you read AiPulse in existing RSS apps (Reeder, FeedMe) — meaningful surface area to implement and maintain a compat API. |
| **Multiple visual themes + custom CSS** | Glance | Currently just light/dark. Decide: curated theme presets, or expose a raw custom-CSS box (more powerful, more support burden). |
| **General-purpose custom widgets** (iframe/HTML/custom-API) | Glance | Biggest architectural addition on this list — turns AiPulse into a general dashboard, not just an AI-news one. Worth asking "do we want that scope?" before building. |
| **Read-it-later integrations** (Pocket/Instapaper-style export) | NewsBlur, FreshRSS | Obsidian export already covers one destination; decide if others are worth the integration surface. |
| **Persist notification "seen" state across restarts** | — | Right now `FeedWatcherService` re-seeds its dedup set on every restart, so nothing alerts in the first cycle after a restart even if it's genuinely new. Needs a small persisted "last seen" store. |

## 🔴 Conflicts with "no AI" positioning (not recommended, listed for completeness)

- AI-based story scoring/ranking or summarization (Horizon, auto-news, RSSbrew)
- Translation (ai-news-aggregator)
- AI-generated community-comment digests (Horizon)

---

## How to pick what's next

Ask: does it need an LLM? (if yes, skip per current positioning) → does it need new infrastructure (public callback endpoint, external API compat layer)? (if yes, it's a bigger lift, sequence it later) → otherwise it's fair game for the next session.
