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
- Fixed the notification-persistence gap above: `FeedWatcherService` now snapshots which links
  `FeedHistoryService` already knew about (persisted to disk, survives restarts) instead of an in-memory-only
  set that forgot everything on every restart. A restart now resumes and alerts on anything genuinely new
  since shutdown; only a true first-ever install (empty history) still seeds silently. Verified live: after
  a restart it logged "resuming with 5600 previously-known item(s)" and correctly raised 15 alerts for items
  that had arrived while the app was down.
- Proper user/password management: users can change their own password in Settings (requires the current
  password); Admins can rename another user's username and/or reset their password from the Users page
  (inline edit, matching the Sources page's pattern). Renaming migrates the user's `App_Data/users/{name}/`
  folder and their Playground chat history so nothing looks lost, and clears their Fever API key (it's
  `MD5(username:password)`, silently invalid under the new name with no way to recompute it without the
  plaintext password). Admins can't rename/reset their own account this way - a stale cookie claim would
  make it confusing - so their own row instead points to Settings.
- Fixed a real light-mode contrast bug: `--ap-accent-soft` (the icon-chip/badge tint used by the logo,
  info badges, "Release" tags, active tag-chips) was Tailwind's ultra-pale "-50" tier in every light-mode
  accent preset (e.g. `#ecfeff`) - nearly indistinguishable from the white surfaces it sits on, which is
  why the logo's chip (and similar badges) looked like they'd vanished in light mode while looking fine in
  dark mode, where the same token is a translucent overlay that pops against a dark background regardless.
  `--ap-success-soft`/`--ap-warning-soft`/`--ap-danger-soft` were already correctly toned at the "-100" tier
  (e.g. `#dcfce7`) - `--ap-accent-soft` was the one inconsistency. Bumped it (and all 4 accent presets) to
  match. Also darkened `--ap-muted` from a 2.96:1 to a 4.84:1 contrast ratio against white (WCAG AA for
  normal text is 4.5:1) - it was borderline unreadable for timestamps/secondary labels in light mode.
- Found and fixed the root cause of the light-mode logo/sidebar visibility complaint: the sidebar was
  hardcoded to a permanently-dark background (`--ap-sidebar-bg`, `-text`, `-text-active` never varied with
  `[data-theme]`), so it stayed dark even in light mode - clashing with the rest of the now-light UI, and
  several colors (`.navbar-brand`'s `color: #fff`, hover-state `rgba(255,255,255,...)` overlays, the mobile
  hamburger icon) were hardcoded assuming that permanent darkness. Made the sidebar theme-aware: light
  background + dark text in light mode, unchanged dark background + light text in dark mode, via new
  `--ap-sidebar-hover`/`-hover-strong` tokens replacing the hardcoded white overlays. Verified live in both
  themes via computed styles (a live-toggle check briefly looked wrong for the nav-link color specifically -
  traced to a getComputedStyle/tooling staleness artifact, not a real bug, since a fresh page load in either
  theme resolves correctly).
- News Feed: added a Source filter dropdown (34 sources), a sort dropdown (Newest/Oldest/Source A-Z/Title
  A-Z), and pagination (`Pager`, 20/page default) - previously showed every filtered item on one unbounded
  page. Also moved the Calendar from an inline card (appeared centered in the main content column) to a
  fixed slide-out panel anchored to the right edge of the viewport, with a dismiss-on-backdrop-click overlay,
  matching how a calendar drawer usually behaves rather than pushing the day's news down the page.
- Added 5 more Reddit sources - r/artificial, r/singularity, r/OpenAI, r/ClaudeAI, r/LangChain - alongside
  the existing r/LocalLLaMA and r/MachineLearning. See the Reddit reality check below before adding more.
- Added 6 more sources rounding out open-source/repo/local-model coverage: r/ObsidianMD (Reddit); ASP.NET
  Core releases and .NET Blog (official Microsoft/.NET Core feeds); llama.cpp releases (the core local-
  inference engine most other local-model tools build on); and two scraped sources, GitHub Trending and
  GitHub Trending · C# (`github.com/trending` has no RSS feed at all, so these use the XPath scraper -
  `ScrapeItemXPath="//article[contains(@class,'Box-row')]"`, `ScrapeLinkXPath=".//h2//a"` - the explicit
  link XPath matters here since the default `.//a` would grab a Sponsor/Star button link before the actual
  repo link). Verified live: both Trending sources return correctly-titled/linked repo items (e.g.
  `dotnet/aspnetcore`, `bitwarden/server`) on the very first fetch.
- Redesigned feed item cards so every post carries its source's visual identity instead of looking like a
  flat text link, consistently across News, Bookmarks, Digest, and Home (a new shared `FeedItemCard`
  component replaces four near-duplicate inline card implementations):
  - **Thumbnail images** - extracted from data the feed already carries (media:thumbnail/media:content,
    RSS enclosures, or the first `<img>` in the raw summary HTML) - zero extra network calls. This covers
    YouTube sources for free (their feed format always includes `media:thumbnail`) plus many blogs/Reddit
    link posts. Falls back to a subtle gradient placeholder, never a broken-image icon, when a source has
    no image (e.g. the GitHub Trending scrape sources).
  - **Per-source identity avatar** - a small favicon next to the source name, fetched and cached by AiPulse
    itself (new `FaviconService` + `/favicon-proxy/{host}` endpoint, same in-memory caching pattern as the
    full-text extractor) rather than an external favicon CDN, which would otherwise reveal your whole
    source list to a third party on every page load. Falls back to a deterministic colored-initial circle
    (hash the source name to a hue) if a site has no favicon or the fetch fails.
  - **Calmer "new since last visit" indicator** - replaced the thick red left-border + red "NEW" badge with
    a small accent-colored dot and semibold title text (the same language YouTube/Feedly use for unread
    items), since the app's own red already means "excluded/error" elsewhere - reusing it for "new" was a
    mismatch. The "New" filter tab's count badge switched from red to the existing accent-tinted badge for
    the same reason.
  - Bookmarks now snapshot the original item's summary/thumbnail at save time (`BookmarkItem.Summary`/
    `ImageUrl`, both nullable for bookmarks saved before this change) so the Reading List renders the same
    rich card as the News Feed instead of a bare title.
- Explore's "GitHub Repos" tab now mirrors github.com/trending exactly instead of approximating it via the
  Search API - language dot (GitHub's own per-language hex color), stars, forks, "N stars today", and a
  "Built by" contributor-avatar row, plus a new **Trending Developers** section (github.com/trending/developers)
  with avatar/handle/popular-repo. `GitHubTrendingService` now scrapes both pages directly (see the reality
  check below); keyword search still uses the official Search API since trending pages aren't searchable,
  and a new `GetRepoStatsAsync` does single-repo star lookups via the official REST API (not scraping) for
  the Tools & Tips popularity badges below. Pagination (`Pager`) added to all five Explore lists (Models,
  Datasets, Repos, Trending Devs, GGUF models).
- Feed item cards now carry a per-source accent: AiPulse fetches and caches each source's declared
  `<meta name="theme-color">` (same self-hosted, no-third-party pattern as the favicon) and uses it as a
  top-edge stripe on every card from that source, falling back to the existing generated color when a site
  declares none or the fetch fails/rate-limits - verified live against `dev.to` (declares `#ffffff`) vs.
  `techcrunch.com` (declares nothing, falls back cleanly). Posts with no thumbnail image now show the
  source's favicon centered over the placeholder instead of a bare gradient.
- Tools & Tips' "Right tool for the right task" list is no longer a single static scroll: added search
  (name/one-liner/best-for), a category filter, a sort (Category/Popularity/Name), and pagination. The
  "Popularity" sort and its "★ 12.3k" badges come from `GetRepoStatsAsync` for any tool whose URL resolves
  to a github.com repo - proprietary tools (Claude Code, GitHub Copilot, etc.) simply sort last with no
  badge, same as before.

> **GitHub Trending scrape reality check:** `GitHubTrendingService` scrapes `github.com/trending` and
> `github.com/trending/developers` directly for the repo/developer views above - GitHub has no API for
> either, and "stars today" and contributor avatars only exist on those unofficial pages, so there's no
> ToS-safe way to get exact parity with the real page. This is a deliberate trade-off made with explicit
> sign-off: scraping GitHub's website (not their API - keyword search and star lookups still use the
> official REST/Search APIs) is against the letter of their ToS, and the markup could change without
> notice and break the selectors. Verified against the live page at time of writing - same repos, same
> star/fork counts, same "Built by" avatars.

> **Reddit reality check:** Reddit's unauthenticated `.rss` endpoint rate-limits hard and per-IP, not
> per-subreddit - fetching several Reddit sources in the same poll cycle (AiPulse fetches all sources
> roughly together) reliably exhausts the shared budget after just 1-2 successful requests, so most of them
> come back `429` on any given poll. Verified directly: manually spacing requests ~15-30s apart, all 7
> current Reddit sources return real content individually, but a live force-refresh through the app got 5 of
> 6 new ones rate-limited at once. This isn't a config problem - it self-heals over multiple poll cycles
> (the two original Reddit sources already have 152 and 32 items respectively in the history despite
> frequent failures) - but it does mean uptime % on the Sources page will look worse for Reddit than for
> everything else, and it gets worse the more Reddit sources you add. A real fix would mean staggering
> Reddit-domain fetches with a delay inside `FeedAggregatorService` instead of fetching every source
> together; not done here since it wasn't asked for.

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

## 🔴 Conflicts with "no AI" positioning (not recommended, listed for completeness)

- AI-based story scoring/ranking or summarization (Horizon, auto-news, RSSbrew)
- Translation (ai-news-aggregator)
- AI-generated community-comment digests (Horizon)

---

## How to pick what's next

Ask: does it need an LLM? (if yes, skip per current positioning) → does it need new infrastructure (public callback endpoint, external API compat layer)? (if yes, it's a bigger lift, sequence it later) → otherwise it's fair game for the next session.
