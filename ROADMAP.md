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
- Nine fixes/changes from a round of live use:
  - Explore's GitHub Repos and Trending Devs tabs merged into one **GitHub Trending** section, two columns
    side by side (mirrors how github.com itself presents it), sharing a Today/This week/This month filter -
    `GitHubTrendingService`'s two scrape methods now take a `since` parameter, cached per value.
  - **Dashboard and Weekly Digest merged** - the Digest page is gone (redirects `/digest` → `/`, nav entry
    removed) and folded into the Dashboard behind a Today/This Week/This Month range toggle: the range-scoped
    stat cards, trending panel, mini-timeline, biggest-stories list, and day-by-day table (hidden for
    "Today", nothing to break down over one day) all react together. `TrendingPanel`/`MiniTimeline` gained
    `DaysWindow`/`MaxDays` parameters instead of a hardcoded 7/90 days so the same components serve both
    the old Dashboard and the folded-in Digest content. The Dashboard's trending panel also now surfaces the
    top 5 GitHub repos and top 5 developers (was top 3 repos only).
  - News Feed's Calendar is now a permanent right-hand column instead of a click-to-open slide-out - no
    more toggle button/backdrop.
  - News Feed's source filter is now a multi-select chip row (matching how topic tags already look) instead
    of a single-select dropdown, so you can filter to several sources at once.
  - Feed cards show a byline (`Author`, from Atom/RSS `<author>` or the common non-standard `<dc:creator>`)
    and a computed "~N min read" estimate when there's enough text to bother, plus a faint background tint
    (in addition to the existing top accent stripe) from the same per-source color for a stronger sense of
    per-source identity at a glance. Investigated a claim that scraping each source's actual page design was
    possible - not attempted at that scope (arbitrary per-site scrapers for video-duration/upvote-style
    fields aren't reliably obtainable from RSS/Atom and would mean bespoke per-source code, working against
    the standalone/self-hosted model everywhere else in the app).
  - **Notification flood fixed**: every new Release-type item used to fire its own alert with no cap per
    source - a source rotating in several "new" items in one poll (the GitHub Trending scrapes especially)
    could flood the bell. `FeedWatcherService` now groups new Release items by source per poll and raises
    at most one alert per source per hour (`Alert` gained `Details`/`Count`); the alert shows the source as
    its title and the newest item's own title/version as a detail line with a "(+N more)" suffix when more
    than one new item arrived, both in the bell panel and in outbound webhook text. Watchlist alerts are
    untouched (per-item, no throttling - a deliberate keyword hit is a different signal than a release
    flood).
  - Investigated a report that Settings' self-service password change didn't work - reproduced the flow
    live (typed current/new/confirm, clicked Change password) and it worked correctly both times tested; no
    code change made since the underlying feature wasn't actually broken. If this recurs, the likely real
    gap is that there's no recovery path for a forgotten admin password today (admins can't reset their own
    account via the Users page by design, to avoid stale-cookie confusion) - flagged, not built speculatively.
- Fixed Explore and Dashboard both appearing to hang on load when Hugging Face or GitHub responded slowly
  (traced live via server logs to a real `HttpClient.Timeout of 15 seconds elapsing` against
  `huggingface.co`) - both pages used to await every data source together before rendering anything, so
  one slow third party held up the entire page behind a single spinner. Each panel now loads and renders
  independently (own loading state per source) - confirmed live: with Hugging Face genuinely slow, GitHub
  Trending's column still rendered immediately and was fully usable while Hugging Face's panel kept
  showing its own "Loading…" beside it, and the Dashboard's stat cards/digest/trending panel/biggest
  stories (all sourced from already-persisted history, no network wait needed) rendered instantly with
  only the small "Trending models & repos" panel waiting on the network.
- Hugging Face and GitHub Trending fixed properly this time: both services now persist their last
  successful fetch to disk (`App_Data/huggingface-cache.json`, `App_Data/github-trending-cache.json`), so a
  fetch failure - or a cold start right after Hugging Face happened to be unreachable - falls back to the
  last good data (with a visible "updated <time>" label) instead of an empty "couldn't reach" panel, even
  across restarts. Every trending panel across Explore, the Dashboard, and Tools & Tips (Hugging Face
  models/datasets/GGUF, GitHub repos/developers on all three `since` windows) got its own manual Refresh
  button that force-bypasses the cache. Root cause of the original "why isn't this loading" report:
  Hugging Face is genuinely, intermittently unreachable from this network (confirmed directly via `curl` -
  one attempt timed out completely, the next succeeded in under a second) - not an app bug, so the fix is
  resilience (cache + manual refresh) rather than chasing a network issue outside AiPulse's control.
- Added 100 Reddit sources in one batch (AI/agents, coding assistants, dev tools/homelab, and a long tail of
  general-interest subreddits) via the existing OPML importer - no new code needed, `OpmlService` already
  dedupes by URL and does a best-effort reachability check. All 100 were new (0 skipped as duplicates of
  the existing 7 Reddit sources); see the reality-check note below for what actually happens when this many
  Reddit sources get polled together.
- OPML import now reports live progress ("Checking N of M — sourcename") instead of a blank wait followed
  by a full-page redirect - the Sources page's file input now uploads through `OpmlService` directly (an
  in-process Blazor call, not the old plain `<form>` POST) with a progress callback threaded through the
  per-URL reachability check, the part that's actually slow for a large import. The `/opml/import` HTTP
  endpoint stays as-is for non-interactive/scripted imports.
- New **Source Health** page (`/source-health`, Admin) - a dedicated dashboard for all sources' uptime,
  average response time, last-attempt time, and last error message, with summary counts
  (healthy/degraded/broken) and a "problems only" filter. Required extending `SourceHealthService` to track
  response time (a simple exponential moving average, not a full time series) and the most recent error per
  source, and `FeedAggregatorService` to time each fetch attempt and pass both through. The persisted
  `App_Data/source-health.json` format changed shape (day-buckets only -> day-buckets + last-attempt/error/
  response-time fields) - old data doesn't migrate, same "corrupt file -> start fresh" fallback as everywhere
  else in this app treats its own cache files, since a few days of lost health history isn't worth writing
  a one-time migration for.
- Sources page: added a "grouped by category" view (default) - each of the 4 categories is a collapsible
  `<details>` section (native HTML, no JS) showing source count/enabled count/currently-failing count,
  auto-expanded only when small (<=15 sources) so News/Tools/Community (107 Reddit sources landed in
  Community) start collapsed and Research doesn't. A "Flat list" toggle switches back to the original
  sortable/searchable/paginated table (unchanged) for full inline editing - the grouped view's Edit button
  switches to flat mode with that row already open for edit, since the inline edit form only exists there.
- News Feed: added `r` to refresh (alongside the existing j/k/o/Enter/m/b/?/Esc), both in the Razor
  key-handling switch and the client-side key allow-list in `wwwroot/js/keyboard.js` (a duplicate list -
  easy to miss updating just one of them).
- **Found and fixed a real regression caused by the 100-source Reddit batch above:** News Feed's
  `OnInitializedAsync` awaited `Feeds.GetAsync()` directly, which on a cold cache runs a full fetch of every
  enabled source before returning anything - fine with a handful of Reddit sources, but with 107 sharing one
  rate-limited host and each retrying with backoff, a first-ever load after any restart now took 10-15+
  minutes, and since this also blocks Blazor Server's SSR prerender, the browser saw nothing at all (not
  even a spinner) until it finished - directly observed live, a `navigate` call to `/news` right after a
  fresh restart timed out completely. Fixed with the same progressive-loading pattern already used for
  Explore/Dashboard this session: `OnInitializedAsync` -> synchronous `OnInitialized()` firing `Load()`
  fire-and-forget, with `StateHasChanged()` added to `Load()`'s `finally` block (needed now that it's not
  directly awaited by the lifecycle method). Verified live: a fresh restart's `/news` visit now renders the
  "Fetching the latest…" state and the calendar sidebar immediately instead of hanging.
- News Feed: saved searches ("smart folders") - name and save the current search text/tag/content-type/
  level/sources/sort combination, per-user (same `App_Data/users/{name}/reading-state.json` this app
  already uses for bookmarks/watchlist). A saved search's date range, if any, is only captured when it
  trails up to today (e.g. "last 7 days") and stored as a day-count rather than fixed dates, so re-applying
  it later always means "the last N days from now" instead of a frozen historical range that'd go stale.
- Share buttons on every feed card (News, Bookmarks, Digest, Home) - Share on X/LinkedIn/submit to Hacker
  News, via plain share-intent URLs (no API keys, no new external dependency). Deliberately generic share
  icon rather than brand logos, matching the rest of the icon set (self-contained inline SVG, no external
  icon font).
- New **Reading Stats** page (`/reading-stats`) - read today/this week/this month/all-time, an estimated
  reading-time total (from each article's existing word-count estimate, not real measured time - AiPulse
  doesn't instrument actual time-on-page), top sources read, and a 14-day breakdown. Needed a real change
  underneath: the old `ReadLinks` was just a set (member or not), so `ToggleRead` now also appends to a new
  append-only `ReadHistory` log (link/source/reading-minutes/timestamp) whenever something transitions to
  read - the only way to answer "when" instead of just "is it". `ToggleRead`'s signature changed from
  `(string link)` to `(FeedItem item)` to have the source/reading-time on hand to record; its one call site
  (News.razor) was updated. Scoped to the interactive mark-read path only - the Fever API's separate
  `MarkReadForUserKey` (used by mobile RSS readers) doesn't feed this log, a deliberate limitation rather
  than instrumenting a second, less-central code path for the same purpose.
- Per-keyword webhook routes, additional to the existing single catch-all webhook URL - route specific
  topics (e.g. "MCP") to a different Slack/Discord/generic URL than everything else, configured in Settings.
  `FeedWatcherService.FanOutWebhooksAsync` now matches each alert's title/details/source against every
  route's keywords (a route with no keywords is a catch-all, which is exactly how the old single
  `WebhookUrl` is now represented internally via `GetAllWebhookRoutes` - no behavior change for anyone who
  hasn't added a keyword-scoped route). Verified live: added a route, confirmed it round-tripped to
  `App_Data/users/{name}/reading-state.json` correctly, removed it.
- ContentExtractorService fix, grounded in directly fetching both sites for real rather than guessing:
  arXiv's own RSS `<description>` already carries the complete abstract (confirmed - a real item's
  description ran several hundred words, ending in a full sentence, not truncated), so nothing was actually
  broken there; Medium, on the other hand, serves a bot-detection/captcha shell with no article content at
  all to a plain server-side fetch (confirmed - the response was a ~150KB app shell containing "captcha"/
  "sign up", not the post) - no amount of smarter HTML parsing fixes that, since there's no content to
  parse. The real, useful fix for both: `ApplyFullTextAsync` was unconditionally re-fetching every item's
  page even when the feed's own `<description>`/`<content:encoded>` already had substantial content (over
  500 chars, well past a teaser) - now that content is used directly as `FullText` at parse time, and
  `ApplyFullTextAsync` skips those items. This is exactly what makes *whatever* a Medium publication's own
  feed happens to include show up, since a live fetch of the page itself never will; arXiv sources don't
  need `FullTextFetch` enabled at all given the finding above, but if enabled, now behaves correctly instead
  of wastefully re-fetching a page that only repeats what the feed already said. Also added a direct
  `//blockquote[@class~='abstract']` selector to the page-scrape path specifically for arxiv.org URLs (no
  `<article>`/`<main>` exists on that page at all, so the generic density heuristic would happily grab the
  metadata sidebar instead) - a defensive improvement for any future source that links to arXiv abs pages
  without arXiv's own complete-abstract RSS.
- New **FeedDiscoveryService**: the Add Source form's URL field now has a "Find feed" button - paste a
  site's plain homepage instead of already knowing its feed URL. Tries, in order: is this URL already a
  feed; does the page declare `<link rel="alternate" type="rss|atom">` in its `<head>` (the standard way
  sites advertise this); a handful of conventional paths (`/feed`, `/rss.xml`, etc.) for platforms that
  don't bother declaring one. Verified live against a real site (simonwillison.net) - pasted the bare
  homepage, it correctly found and filled in the real Atom feed URL.
- Cross-source dedup now catches near-duplicates, not just exact ones: `Deduplicate()` already grouped
  items by an exact normalized-title key (significant words, sorted); it now also fuzzy-merges *different*
  keys whose word sets overlap heavily (e.g. "OpenAI releases GPT-5 with improved reasoning" vs "OpenAI's
  GPT-5 launches with better reasoning capabilities" - different wording, same story) and whose earliest
  items are within 3 days, same window as the exact-match path. Uses an overlap coefficient
  (intersection / smaller-set size), not Jaccard (intersection / union) - traced concrete header examples
  and Jaccard punishes headlines of different lengths too hard even when every word in the shorter one
  also appears in the longer one. Landed on an 0.5 threshold after tracing "same story reworded" (~0.5-0.67
  overlap) against "different stories sharing a couple of generic terms" (~0.3) examples by hand.
- New trending-topic spike alerts (`Kind = "Trend"`) - flags a tag whose item count today is at least 3x
  its own average over the last 7 days (and at least 8 items, so a rare tag going 1->4 doesn't count),
  computed from `FeedHistoryService`'s already-persisted items grouped by (tag, day) - no new tracking
  needed. Requires 3+ of the last 7 days to have had any activity at all, so a tag's very first day of
  existence doesn't read as an infinite spike against a zero baseline. Throttled to one alert per tag per
  calendar day, links to `/news?tag=X`, fans out through the same webhook routes (including per-keyword
  ones) as Release/Watchlist alerts. Toggle: `Notifications:NotifyTrends` (default on).

> **GitHub Trending scrape reality check:** `GitHubTrendingService` scrapes `github.com/trending` and
> `github.com/trending/developers` directly for the repo/developer views above - GitHub has no API for
> either, and "stars today" and contributor avatars only exist on those unofficial pages, so there's no
> ToS-safe way to get exact parity with the real page. This is a deliberate trade-off made with explicit
> sign-off: scraping GitHub's website (not their API - keyword search and star lookups still use the
> official REST/Search APIs) is against the letter of their ToS, and the markup could change without
> notice and break the selectors. Verified against the live page at time of writing - same repos, same
> star/fork counts, same "Built by" avatars.

> **Reddit reality check (updated - now 107 Reddit sources: the original 7 plus a 100-subreddit batch
> imported via OPML):** `FeedAggregatorService` since gained a per-domain cooldown (3s between requests to
> the same host) plus a per-source retry with backoff (5s, then 10s) on a `429` before giving up for that
> poll - a real improvement over the original "not done" gap this note used to describe. It still isn't
> enough for 107 sources sharing one host: the cooldown check-then-set isn't atomic under the 5-way
> concurrency gate, so several Reddit requests can slip past it in the same instant, and Reddit's actual
> anonymous-RSS budget is tighter than 3s/request anyway. Verified live via a full force-refresh right after
> the import: the large majority of the 100 new subreddits came back `429` on the first attempt, some
> recovered on the 5s/10s retry, most didn't and were left for the next poll cycle. Same conclusion as
> before, just at 15x the scale - this self-heals over multiple poll cycles rather than in one refresh (a
> handful of individually-tested subreddits, e.g. r/LocalLLaMA and r/AskReddit, return real content fine
> when requested on their own, confirming the sources themselves are correctly configured). A real fix would
> mean a dedicated slower stagger for the `reddit.com` host specifically (or a shared cross-poll rate
> budget) instead of the generic per-domain cooldown; not done here since it wasn't asked for, and 107
> same-host sources is an unusually large batch compared to what the rest of AiPulse's source list looks
> like.

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
