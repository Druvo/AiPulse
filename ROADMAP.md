# AiPulse Roadmap

A running backlog of ideas, most of them sourced from comparing AiPulse against real competitors (Glance, Miniflux, FreshRSS, Horizon, auto-news, RSSbrew, ai-news-aggregator) — see the analysis this list grew out of for citations. Grouped by whether it fits AiPulse's core positioning: **standalone, self-hosted, zero AI required**.

Status tags: ✅ done · 🟢 fits philosophy, no AI needed · 🟡 needs a design call · 🔴 conflicts with positioning (listed for completeness, not recommended)

---

## ✅ Shipped

- OPML import/export (bulk source management), with a reachability check on every newly imported URL
- Cross-source deduplication (title-similarity merge, "+N more sources")
- Weekly Digest page (7-day rollup, biggest multi-source stories)
- Multi-user accounts: registration, admin approval, Admin/User roles, per-user bookmarks/watchlist/chat history
- Desktop notifications (releases + watchlist), keyword watchlist
- Regex/boolean exclude filters (the inverse of the watchlist — hide matching items entirely)
- Full keyboard navigation on the News feed (j/k/o/Enter/m/b/?)
- Source health/uptime history (rolling 30-day % per source on the Sources page)
- Tracking-param stripping from feed links (utm_*, fbclid, gclid, etc.)
- XPath-based scraping for sites with no RSS/Atom feed at all
- Full-text fetching for summary-only feeds (readability-style extraction, cached per link)
- WebSub/PubSubHubbub push - subscribes to any feed that declares a hub so it pushes updates to us
  instead of us polling. Opt-in via `WebSub:PublicBaseUrl`; inert (and untested-in-the-wild as of this
  writing - see note below) otherwise.
- Learning Hub, Glossary, Tools & Tips, Explore (HF/GitHub trending), Playground (local Ollama chat)
- Backup/restore, Obsidian export, global search, light/dark theme
- Outbound webhooks — Slack/Discord (auto-detected) or generic JSON POST for release + watchlist alerts,
  per-user URL, with a "send test" button
- Mobile client API compatibility — a Fever API endpoint (`/fever/`) so AiPulse can be read from Reeder,
  ReadKit, Fiery Feeds and other Fever-compatible RSS apps, auth'd via a separate per-user API password
- Theme presets (5 accent colors) + an advanced custom-CSS box for full visual control, both remembered
  per browser
- General-purpose Dashboard widgets — embed an iframe or raw HTML snippet of your own (status page,
  another dashboard, a personal note), managed from Settings
- In-app Help page (`/help`) covering what AiPulse is, a typical daily workflow, and a full page reference,
  plus a dismissible first-visit welcome banner on the Dashboard
- Fixed feed items showing a literal "(untitled)" placeholder — titles now fall back to a summary excerpt,
  then the source name, instead of a broken-looking placeholder. A one-time startup repair also re-titles
  anything already recorded with the old placeholder (feed history and saved bookmarks alike), so the fix
  covers existing data, not just newly-fetched items.
- Dashboard now also surfaces a compact "Trending models & repos" panel (top Hugging Face models + GitHub
  repos, same live data as Explore) alongside the existing tag-trending panel, plus a "new since last visit"
  stat card and a standalone "biggest story this week" highlight (same ranking as the Weekly Digest)
- Nav reorganized around actual usage: Reading List moved up next to News/Digest (Overview), Explore moved
  next to Learning Hub/Glossary/Tools under a renamed "Learn & Explore" section (it's model/repo discovery,
  not daily triage), Playground/Sources/Users stay under Admin
- Fixed a real bug in the News feed's "New" filter/badges: Blazor Server prerenders every full page load by
  default, which runs `OnInitializedAsync` twice against two separate scoped-service instances - since
  marking "last visited" was a mutate-on-read, the second (interactive) pass silently read back the
  timestamp the first (prerender) pass had just written, collapsing "New" to ~0 items on effectively every
  load. Fixed by peeking the cutoff in `OnInitializedAsync` and only marking the visit in
  `OnAfterRenderAsync(firstRender)`, which Blazor guarantees runs exactly once in the real circuit.
- Search, sort, and pagination added to every real data table (Sources, Users, Weekly Digest's day-by-day
  breakdown) via two new reusable components, `Pager` and `SortableHeader`. (Help's page-reference table
  is static documentation, not data, so it was left alone.) Caught a subtle Blazor gotcha along the way:
  for a **string-typed** component parameter, `Param="_field"` (no `@`) compiles as a literal string, not a
  reference to `_field` - non-string parameter types don't have this ambiguity, which is exactly why it
  silently passed the literal text `"_sortColumn"` into the sort-arrow display while the actual sort *logic*
  (driven by the page's own field, not the parameter) worked fine. Needs `Param="@_field"` whenever the
  parameter type is `string`.
- News Feed date filtering upgraded from a single-day pick to a real range, plus an Obsidian-style month
  calendar (`MonthCalendar` component): click a day to filter to it, shift-click another day to select a
  range, with per-day item-count shading reusing the Dashboard's heatmap color scale. `news?date=` links
  (Dashboard's activity heatmap, etc.) still work unchanged; the calendar/range adds `dateFrom`/`dateTo`
  query params alongside it.

> **WebSub reality check:** none of AiPulse's ~40 default sources currently declare a hub (checked YouTube,
> WordPress.com, Feedburner, Blogger - all previously reliable examples, none do anymore). The subscribe/
> verify/HMAC-signature/renew mechanics are built and directly verified (simulated hub requests against
> the callback endpoints), but real end-to-end delivery depends on a source that still runs a hub, which
> is now rare. It'll activate automatically the day one of your sources adds one - nothing to configure
> beyond `WebSub:PublicBaseUrl`.

---

## 🟡 Valuable, needs a design decision

| Idea | Source | The decision |
|---|---|---|
| **Read-it-later integrations** (Pocket/Instapaper-style export) | NewsBlur, FreshRSS | Obsidian export already covers one destination; decide if others are worth the integration surface. |
| **Persist notification "seen" state across restarts** | — | Right now `FeedWatcherService` re-seeds its dedup set on every restart, so nothing alerts in the first cycle after a restart even if it's genuinely new. Needs a small persisted "last seen" store. |

## 🔴 Conflicts with "no AI" positioning (not recommended, listed for completeness)

- AI-based story scoring/ranking or summarization (Horizon, auto-news, RSSbrew)
- Translation (ai-news-aggregator)
- AI-generated community-comment digests (Horizon)

---

## How to pick what's next

Ask: does it need an LLM? (if yes, skip per current positioning) → does it need new infrastructure (public callback endpoint, external API compat layer)? (if yes, it's a bigger lift, sequence it later) → otherwise it's fair game for the next session.
