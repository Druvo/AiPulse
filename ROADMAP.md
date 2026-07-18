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

> **Per-user source preferences - the design decision:** "different users want different feeds" could mean
> either (a) each user mutes/hides specific sources from their own feed, or (b) each user manages a fully
> independent source list. Went with (a): Sources stay global/admin-managed with one shared fetch/cache
> across everyone (`FeedAggregatorService`'s whole efficiency model depends on that), and a new per-user
> `MutedSources` set (same `App_Data/users/{name}/reading-state.json` pattern as bookmarks/watchlist/saved
> searches) filters at *display* time in `News.razor`'s `Filtered()` - one line, right next to the existing
> `ExcludeFilters` check it mirrors. Mute from an eye-off icon next to any card's source name (only rendered
> when `OnMuteSource` is wired, i.e. on News - Bookmarks/Home previews don't offer it), manage/unmute the
> list from Settings. (b) would mean re-architecting the shared fetch/cache into a per-user one - a much
> bigger change, and not what "users might want different feeds" actually needs when the real ask is "I
> don't want to see r/X in my feed," not "I want to run my own completely separate source list." Verified
> live: muted a source via a hand-edited state file, confirmed it showed in Settings with an unmute chip,
> unmuted it, confirmed the round-trip persisted correctly to disk both ways.

- Dashboard: fixed the "Biggest story" section appearing to duplicate itself - the hero card above the
  ranked list was always literally the same item as the list's own first entry (`_biggestStory` was
  `_biggestStories.FirstOrDefault(...)`). The ranked list below the hero card now excludes whichever story
  is already shown there. Also moved "Biggest story" and "Day by day" into one row (50/50, `col-md-6` each)
  per request, ahead of the full "Biggest stories" ranked list.
- News Feed: Grid is now the default view (was List).
- Fixed two more pages with the same cold-start-blocking bug as the News Feed fix earlier this session -
  **Glossary** and **Learn** both awaited a live feed/trending fetch directly in `OnInitializedAsync`,
  so with the 100-source Reddit batch now in the mix, both could take 10+ minutes to render anything at
  all on a cold cache. Same fix as before: fire-and-forget the fetch from a synchronous `OnInitialized()`,
  render immediately, fill in once it resolves.
- News Feed no longer shows a blank "Fetching the latest…" state on a cold cache - it now preloads
  `FeedHistoryService`'s already-persisted items instantly (same source Home/Timeline already use), and
  fills in progressively as each individual source finishes during the live fetch, instead of one atomic
  swap once the entire ~150-source batch completes. Required a new `FeedAggregatorService.PartialProgress`
  event, raised after every source (success or failure) with a running snapshot of items fetched so far;
  the page subscribes to it while a fetch is in flight and swaps in the fully-deduped result from
  `GetAsync()` once that resolves. The underlying fetch concurrency is unchanged (still 5 sources in
  flight at once, not strictly one-at-a-time - going fully sequential would make the whole ~150-source
  cycle even slower) - what changed is that the *display* updates incrementally instead of waiting for
  the last source to finish before showing anything past the preloaded history.
- Shrunk the Dashboard's Activity timeline heatmap - its grid columns were `minmax(11px, 1fr)`, so on a
  wide card they stretched to fill all available width instead of staying compact; changed to a fixed
  `11px` per column (GitHub's own contribution graph doesn't stretch either - it scrolls horizontally
  instead, which `.heatmap-scroll` already supports).

> **Which machine does "Try a Model" detect?** `SystemInfoService.Detect()` reads whatever machine the
> `dotnet` process is actually running on (`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` for RAM,
> `nvidia-smi` for GPU/VRAM - silently finds nothing on a non-NVIDIA box, which is correct behavior, not a
> bug). Checked directly on this dev machine: Dell Inspiron 5570, 16 GB RAM, Intel UHD Graphics 620
> (integrated, no discrete GPU) - so Explore's compatibility badges are reporting this machine's real specs.

- Dashboard's Activity timeline now always shows a full year (`MaxDays="365"`, decoupled from the
  Today/Week/Month digest range toggle above it) and the heatmap cells themselves are a little bigger
  (11px → 14px per column, grid gap 4px → 5px) - a follow-up after last round's shrink-to-fit went further
  than wanted; `.heatmap-scroll`'s horizontal scroll still handles the wider year-long grid.
- Merged the Dashboard's two separate "Biggest story" blocks into one - there used to be a standalone
  hero card for the single biggest story *and* a full ranked list below it (which still included that
  same story as its first entry after the previous round's dedup fix). Removed the hero card entirely and
  put the full "Biggest stories" ranked list in its place, paired 50/50 with "Day by day" in the same row.
- News Feed's default page size is now 100 (was 20).
- Moved the News Feed's Filters panel (content type/level/sort, source chips, tag chips, saved searches)
  out of the main content column and into the always-visible calendar sidebar, directly below the month
  calendar - keeps the primary feed column focused on items while filtering controls live in one place.
- Rebalanced the Dashboard's uneven card heights: the Trending/Activity timeline/Trending models & repos
  row now stretches all three cards to match the tallest one (`h-100`) instead of each hugging its own
  content height; "Biggest stories" is capped to 6 items in a scrollable `max-height: 420px` panel instead
  of up to 10 plain-stacked cards, so it no longer dwarfs "Day by day" next to it; "Read later" now shows
  4 items (was 6) to better match "Continue learning"'s 4.
- Reading Stats is now genuinely actionable instead of just a readout: added a current/longest read-streak
  pair (plus a nudge banner - "Read one article today to keep your streak alive" - when today's count is
  still zero), made "Top sources" and "Last 14 days" rows clickable straight into the matching News Feed
  filter (`news?source=`/`news?date=`), and added By content type / By level breakdowns plus a Recently
  read list of the last 8 items linking straight to the original article. The breakdowns and recently-read
  list need `ReadEvent.Title`/`ContentType`/`Level`/`Category` - new fields, so anything marked read before
  this shipped won't have them and those sections just say so rather than showing blank/wrong data.
- Explore's tabs are reordered to GitHub Trending → Models & Datasets → Try a Model → Benchmarks (GitHub
  first, matching what's asked for most), and the page now lands on GitHub Trending by default instead of
  Models & Datasets.
- Hugging Face model/dataset cards on Explore now carry real data the Hub API already returns but wasn't
  being shown: author, library (transformers/diffusers/etc.), license, last-modified date, and non-taxonomy
  tags - fetched via `&full=true` on the existing trending/search/GGUF calls (no extra requests). Each card
  has a "Downloads, license, use this model…" toggle that expands to those details plus copy-to-clipboard
  code snippets: a `transformers`/`AutoModel(ForCausalLM)` Python snippet (datasets get `datasets.load_dataset`),
  an `ollama run hf.co/{id}` line (works when the repo has GGUF weights - Ollama says so plainly if it
  doesn't), and a Hosted Inference API cURL command. Reuses the existing `window.copyText` clipboard helper
  from `playground.js` rather than adding a new one.
- Dashboard's "Trending this week" panel now surfaces more at a glance - top tags 8 → 20, most active
  sources 5 → 10 - and the Activity timeline's heatmap cells are bigger again (14px → 20px per column,
  gap 6px), a further follow-up on top of the two previous "make it bigger"/"make it smaller" rounds.
  "Day by day"'s default page size is now 10 (was 7).
- GitHub release items (the `ContentType: "Release"` sources like "Claude Code releases", "Ollama
  releases") no longer show up in the News Feed - Explore's GitHub Trending tab already covers the
  GitHub ecosystem, so a separate release stream in News was redundant. They're removed from News's
  content-type filter list and now surface as a "Recent releases" section at the bottom of Explore's
  GitHub Trending tab instead, reusing `FeedHistoryService` (the same persisted history Home's timeline
  reads) rather than a new fetch path.
- The News Feed's Filters panel (moved into the calendar sidebar last round) is no longer a
  collapse/expand toggle - it's always shown now, per request. Removed the now-dead `_showFilters` field
  and `ActiveFilterCount()` badge helper along with it.
- `FeedItemCard` - used on News, Home, Bookmarks, and now Explore's release list - previously only opened
  the article if you clicked the title text itself; clicking anywhere else on the card did nothing, which
  read as broken. The whole card is now clickable (opens the link in a new tab, same as the title always
  did), with every interactive control inside it - read/bookmark/share buttons, the share dropdown, mute
  source, "read full text" - using `@onclick:stopPropagation` so they keep working as their own separate
  actions instead of also triggering the card-wide open.
- Added a "Back to top" button, site-wide, for long pages (News, Explore, Learn, etc.) - a single fixed
  circular button added once in `MainLayout.razor` rather than per-page, so it applies everywhere without
  touching individual pages. Pure JS (`wwwroot/js/scrollTop.js`): toggles a `.show` CSS class once the
  page scrolls past 400px and smooth-scrolls to top on click - no Blazor/JS interop round-trip needed for
  either the show/hide check or the click itself. Since it lives in `MainLayout` (not `@Body`), it
  persists across Blazor Server's in-app navigation, so the script only needs to wire up its listeners
  once per real page load. Added a new "arrow-up" case to the shared `Icon` component for it.
- Removed three leftover "Test A/B/C" sources pointing at `example.com/feed-test-*.xml` placeholder URLs
  (404 by design - `example.com` doesn't serve those paths) - deleted directly from `App_Data/aipulse.db`,
  since they were added via the Sources UI in an earlier session rather than seeded from `sources.json`.
- Fixed the real bug behind Reddit's persistent 429s - see the updated Reddit reality check below for the
  full story (an actual concurrency race, not just "Reddit is strict").
- Fixed the News Feed's "Sources (select any number)" chip list visibly growing/shrinking/reordering on
  its own - it was derived from `_result.Items` (the current fetch snapshot), which legitimately changes
  several times during progressive loading (preload placeholder -> partial-progress snapshots as each
  source finishes -> the final deduped result), so the set of "sources with items so far" kept changing
  underneath the user. It's now derived from the configured (enabled) source list instead - stable
  regardless of fetch progress, and arguably more correct anyway (a source with zero recent items should
  still be there to filter by). Also gave the chip list its own `max-height: 220px` scrollable container -
  with 150+ sources it had grown taller than the page and effectively unreachable in the sidebar.
- Added 12 curated YouTube channels to `Data/sources.json` (Category `News` for AI-focused channels,
  `Tools` for general engineering ones - matching the existing `Two Minute Papers`/`Fireship` precedent):
  IBM Technology, Andrej Karpathy, 3Blue1Brown, AssemblyAI, LangChain, Hussein Nasser, ByteByteGo, Nick
  Chapsas, Milan Jovanović, CodeOpinion, ThePrimeagen, and TechWorld with Nana. Each channel's real
  `channel_id` was resolved live from `youtube.com/@<handle>` (canonical link, `externalId`, and `og:url`
  all cross-checked and agreeing - not guessed), and every resulting `/feeds/videos.xml?channel_id=...`
  URL was fetched and confirmed to return the right channel's title before being added. `@TwoMinutePapers`
  and `@Fireship` were skipped - both already existed in `sources.json` under slightly different display
  names. Since `sources.json` only seeds an empty DB (`KnowledgeBaseService.SeedAsync`), and this dev
  machine's `App_Data/aipulse.db` already existed, the same 12 rows were also inserted directly into the
  live DB so they show up without a fresh-DB reset.
- Added a much larger second batch of 45 curated YouTube channels spanning AI/LLM engineering, AI
  research/ML education, backend/system design, .NET/C#, DevOps/cloud, general software engineering, AI
  agents, MLOps, and computer science fundamentals - same resolution discipline as the first batch
  (canonical link + `externalId` cross-checked live, every feed URL fetched and title-verified, nothing
  guessed), and the same rows inserted directly into `App_Data/aipulse.db` alongside the `sources.json`
  update. Of the ~59 handles given, 12 were skipped as already present from the earlier round or
  `sources.json`'s original seed (`IBM Technology`, `Andrej Karpathy`, `LangChain`, `AssemblyAI`, `Hussein
  Nasser`, `ByteByteGo`, `CodeOpinion`, `Nick Chapsas`, `Milan Jovanović`, `TechWorld with Nana`, `Two
  Minute Papers`, `3Blue1Brown`) and 45 resolved cleanly. 13 handles failed to resolve (404 - the handle as
  given doesn't exist): `@AnthropicAI`, `@berkeleyai`, `@ArpitBhayani`, `@GOTOConferences`,
  `@NDCConferences`, `@AmichaiMantinband`, `@Docker`, `@ContinuousDelivery`, `@LowLevelLearning`,
  `@MLOpsCommunity`, `@MLflow`, `@SnowflakeComputing`, and notably `@ThePrimeTimeagen` - which does resolve,
  but to an unrelated channel (not ThePrimeagen; the correct handle, `@ThePrimeagen`, was already added in
  the previous batch). These need the real handle found manually before they can be added.
- News Feed: opening an item (clicking anywhere on the card, or the title link itself) now marks it read
  automatically, on top of the existing explicit eye-icon toggle - added `ReadingStateService.EnsureRead`,
  which unlike `ToggleRead` only ever moves an item from unread to read (never flips an already-read one
  back), so re-opening something already read doesn't undo it or double up its `ReadHistory` entry for
  Reading Stats. `FeedItemCard` gained an `OnOpened` callback fired from both click surfaces (whole-card
  click and the title `<a>`, each still `stopPropagation`'d against the other so a single open doesn't
  double-fire). Also added a "Read" filter tab (mirroring the existing "Unread" one) so read items have
  somewhere to go, and a "Mark all as read" button that applies to every item in the current filter/
  search/sort view, not just the current page.
- Fixed read/unread inconsistency across the app - `IsRead`/`OnToggleRead`/`OnOpened` were only ever wired
  up on the News Feed's own `FeedItemCard` usages; everywhere else (`Home`'s "Biggest stories" and "Read
  later" panels, the old Bookmarks page, Explore's "Recent releases") silently defaulted to unread and had
  no way to change it, so the same article could show read on the News Feed and unread everywhere else it
  appeared. Every `FeedItemCard` usage now wires the same read-state plumbing consistently.
- News Feed: the visible list no longer reshuffles live while you're reading it. Previously, every
  progressive-loading tick during a fetch (and every completed Refresh) replaced `_result` directly - with
  100+ sources completing at different times, that meant the list could visibly jump several times during
  a single refresh. It now only auto-applies while still on the History-preloaded placeholder (nothing real
  shown yet, nothing to disrupt - keeps the cold-start fill-in feeling fast); once a real result has been
  shown, further updates are buffered and surfaced as a small "N new items ready - Show / Dismiss" banner
  instead, so the list only changes when you tell it to.
- Reading List moved off its own page and into Reading Stats - one page for "how much have I read" and
  "what have I saved to read," rather than two. Added a bar chart for the last 14 days of reading activity
  and an SVG donut chart for the content-type breakdown (both hand-rolled, no charting library - matches
  the existing "no heavy deps" pattern already used for the day-bars and heatmap). `/bookmarks` now
  redirects to `/reading-stats`, same pattern as the earlier `/digest` redirect.
- Dashboard: the Activity timeline's heatmap cell height (not width) now flexes to fill whatever height its
  card ends up at via `flex: 1 1 0`, instead of a fixed pixel guess - so it automatically matches its taller
  row siblings (e.g. Trending models & repos) without needing another manual resize next time that changes.
- Added the user's own `AI_PULSE_LOGO_V1`/`V2` artwork (`wwwroot/img/logo-v1.png`/`logo-v2.png`), shown in
  the sidebar brand and on the sign-in/register pages. A new "Logo" picker in Settings > Appearance switches
  between them via `localStorage` (`aipulse-logo`), mirroring the existing accent-color/custom-CSS pattern
  exactly - applied instantly on load and re-applied after Blazor's enhanced-navigation swaps, same as theme.
- Redesigned the Login/Register pages: a soft blue-to-violet radial gradient background, a gradient top
  accent bar on the card, and the new logo replacing the old icon+text brand.
- Added a project signature - "Made with ♥ by Druvo" - in the sidebar footer, the sign-in/register pages,
  and the bottom of `README.md` (with a "star this repo" link), so the project carries a clear, consistent
  mark of authorship wherever someone lands.
- Learning Hub expanded with 7 new modules covering ground the roadmap didn't reach before: AI model types
  & architectures, AI security (prompt injection & agent risks), AI ethics/responsible use, plus three
  deliberately non-AI topics that came up analyzing what else the sources you follow actually cover -
  cybersecurity fundamentals, cloud & DevOps fundamentals, data engineering fundamentals - and one
  forward-looking one, quantum computing basics. Added 8 matching glossary terms (Prompt Injection,
  Responsible AI, Attack Surface, Zero Trust, Hybrid Cloud, FinOps, Data Fabric, Quantum Computing).
- Learning Hub gained an "Emerging topics not yet in your roadmap" panel - reuses the existing 14-day
  tag-frequency counter (already powering the "🔥 Trending" course badge) to surface tags trending in your
  News Feed that no module's `RelatedTerms` covers yet. Genuinely dynamic without an AI call: today's
  chatter surfaces itself as a candidate for tomorrow's module, rather than the roadmap only growing when
  someone remembers to add to it by hand.
- Added a new "Free AI APIs" page/nav item: a curated table of providers with a real free tier (Groq,
  Google AI Studio, OpenRouter, Cerebras, Hugging Face Inference Providers, Cloudflare Workers AI, GitHub
  Models, Mistral La Plateforme, NVIDIA NIM, Cohere, SambaNova Cloud, Together.ai) - each entry has the free
  tier's real limits, which models are covered, a signup link, and how to point an agent tool (Claude Code,
  Codex CLI, OpenCode, etc.) at it via an OpenAI-compatible base URL where one exists. Hand-maintained
  rather than scraped on a schedule - provider free-tier terms aren't published as a structured feed the
  way RSS/news sources are, change often, and scraping pricing pages against changing markup/ToS would be
  more fragile than useful; each entry instead carries a `LastVerified` date so staleness is visible rather
  than silently trusted. Groq's and OpenRouter's current rate limits were checked directly against their
  live docs while building this list.
- Free AI APIs page gained a discovery queue instead of staying purely hand-maintained: a daily background
  job (`FreeApiDiscoveryService`, also triggerable on demand from an admin-only panel on the page) checks
  `cheahjs/free-llm-api-resources` - a GitHub repo whose entire purpose is tracking exactly this - for
  provider names not yet on AiPulse's own list, and separately re-checks every existing entry's signup/docs
  links with an automated HEAD/GET request. Neither pass publishes anything by itself. New providers land as
  pending candidates the admin reviews and fills in by hand (name/URL only get pre-filled - rate limits,
  agent-wiring notes, etc. still need a human), and a broken-link check just sets a "needs review" badge
  rather than editing or removing the entry, since a bot-blocked request isn't proof a link is actually dead.
  `FreeApiEntry`/`Sources` moved to the same DB-backed pattern (seeded once from Data/free-apis.json, then
  the DB is authoritative) so admin approvals persist without a redeploy.

> **Free API discovery reality check:** there's still no structured feed for "which providers currently have
> a free tier" - that's the whole reason this page started out hand-curated. `cheahjs/free-llm-api-resources`
> is the closest real substitute: its README was fetched directly and confirmed to be a flat sequence of
> `### [Provider Name](url)` headings (26 at time of writing) before writing the parser against it, rather
> than guessing at the format. The link-staleness check has a known false-positive risk - some sites block
> automated HEAD/GET requests outright regardless of whether a browser could load the page fine, the same
> class of bot-blocking this session already hit scraping Reddit and YouTube - so its badge is phrased as
> "needs review", not "broken", and nothing gets auto-removed on a failed check.

- Implemented a full round of findings from a self-published senior-UX audit of the app - flow-consistency
  fixes, missing affordances, and a Learning Hub tracking overhaul:
  - **Shared chrome, one visual language.** New `FilterBar`/`EmptyState`/`ToastHost` components replace
    three independently-built "filter a list" patterns (News' sidebar, Tools/Free AI APIs' top row,
    Explore's bare search box) and a scatter of one-off inline alerts across Settings/Reading Stats/News.
    `ToastService` is Scoped (one per circuit, same lifetime as `ReadingStateService`) with a single
    `<ToastHost />` mounted once in `MainLayout`; pages push to it instead of hand-rolling their own
    clear-on-next-action logic. Transient confirmations (password changed, export done, test results) moved
    to toasts; validation errors and actionable instructions (wrong password, "restart AiPulse now") stayed
    inline, on purpose - a toast that vanishes in 5 seconds is the wrong home for something you still need
    to act on.
  - **Consistency fixes:** Reading Stats' saved-articles list gained the same `Pager` News and Tools already
    had; Learning Hub's live trending panel gained a refresh button + last-updated stamp matching Explore's
    per-section pattern; Mark-all-as-read (News) and Remove bookmark (Reading Stats) both gained an Undo
    toast instead of firing with no way back; `FeedItemCard`'s Compact layout (Dashboard's Biggest
    Stories/Read Later) gained an explicit checkmark + strikethrough for read items, since opacity alone
    has less room to register at that size; Explore/Tools/Learn's bare "Loading…" spinners became skeleton
    placeholders; the nav's "Learn & Explore" group split into "Learn & Explore" (content streams: Learning
    Hub, Explore) and "Reference" (lookups: Glossary, Tools & Tips, Free AI APIs); the Dashboard's welcome
    banner became a 3-item first-run checklist (visit News, read/save something, start a module) driven by
    real state instead of static copy.
  - **Learning Hub tracking overhaul** - the largest piece, aimed at tracking retention instead of just
    consumption: modules now track completion **per step** (`ReadingState.CompletedSteps`, keyed
    `"ModuleTitle::StepIndex"`) instead of one binary toggle, with the whole-module flag derived from steps
    rather than a second source of truth - a one-time backfill in `Learn.razor`'s `OnInitialized` upgrades
    modules completed under the old toggle so checking a single step never silently un-completes them.
    Learning now has its own streak (`ReadingState.LearningHistory`, mirroring `ReadHistory` but for step
    completions) so a day spent deep in one module without opening a news article still counts as "showed
    up today." A "Worth a refresher" shelf surfaces modules completed six-plus weeks ago
    (`ModuleCompletedAt`), the closest thing to spaced repetition without an actual scheduling algorithm. A
    one-line self-check ("could you explain this to someone else?") records yes/not-yet per module. A
    "Topic strength" panel cross-references completed modules' `RelatedTerms` against articles actually read
    in the last 30 days (via `FeedHistoryService.Items` matched against `ReadHistory` links) - two signals
    that already existed separately and had never been combined. Personal notes per module
    (`ReadingState.ModuleNotes`) feed a new `ObsidianExportService.ExportLearningNotes`, so the Learning Hub
    export is a real takeaway notebook, not a dump of static module content. Time invested is a rough
    per-step estimate (8 min default) summed from `LearningHistory`, shown next to the streak.

> **UX audit implementation reality check:** every `[Authorize]` page touched here was initially verified the
> same way every authenticated page had been all session - clean build, server starts with no exceptions, and
> the route redirects cleanly to `/login` with nothing thrown server-side (the admin credential blocker
> mentioned throughout this file was, in hindsight, never a real blocker - `Auth:Username`/`Auth:Password` in
> `appsettings.json` bootstrap an admin account on first run, and simply hadn't been checked). Once actually
> logged in, a full live pass confirmed the Learning Hub logic genuinely works end to end: the legacy
> completion backfill correctly checked individual step boxes (not just the module-level flag) for the 10
> modules already complete on the test account, the per-step toggle persisted through a hard page reload with
> the progress bar and module-complete state updating correctly, the self-check button persisted its
> selection, and the streak/time-invested/topic-strength numbers were all real, derived values rather than
> placeholders.
>
> That same live pass caught a genuine bug: clicking "Mark all as read" against a large filtered set (~9,200
> items, from months of accumulated feed history on the test account) hung the entire Blazor circuit for
> 60-90+ seconds - confirmed by a completely unrelated click (the List/Grid view toggle) queuing and waiting
> the same length of time before it was processed. Root cause and fix are described below. This is exactly
> the class of problem that clean-build/clean-redirect verification can never catch, since it only shows up
> under a realistic data volume during an actual signed-in interaction - a concrete argument for why the
> honest "verified by code review, not by clicking through it live" caveat on earlier rounds mattered.

> **Mark-all-as-read performance fix:** `ReadingStateService.EnsureRead` calls `Save()` (a full JSON
> serialize + `File.WriteAllText` of the user's entire `reading-state.json`) on every call, which is fine for
> a single click but was also being called once per item inside `News.razor`'s `MarkAllAsRead` loop - against
> a large filtered view, that's thousands of full-file rewrites for one button click, blocking the whole
> circuit until they finish. Added `EnsureReadBulk`/`MarkUnreadBulk` (used by `MarkAllAsRead` and its Undo
> toast) that update in-memory state for the whole batch under one lock and save exactly once at the end.
> Verified against the same live account: reset to ~9,289 unread items via a direct edit to `reading-state.json`
> (`ReadHistory` preserved, only `ReadLinks` cleared), confirmed the page stayed responsive throughout, and
> confirmed the file was written once rather than repeatedly.

> **GitHub Trending scrape reality check:** `GitHubTrendingService` scrapes `github.com/trending` and
> `github.com/trending/developers` directly for the repo/developer views above - GitHub has no API for
> either, and "stars today" and contributor avatars only exist on those unofficial pages, so there's no
> ToS-safe way to get exact parity with the real page. This is a deliberate trade-off made with explicit
> sign-off: scraping GitHub's website (not their API - keyword search and star lookups still use the
> official REST/Search APIs) is against the letter of their ToS, and the markup could change without
> notice and break the selectors. Verified against the live page at time of writing - same repos, same
> star/fork counts, same "Built by" avatars.

> **Reddit reality check (updated - the real fix from the previous note's "not done" list is now in):**
> two bugs, both fixed. First, the old per-domain cooldown's check-then-set wasn't atomic under the 5-way
> concurrency gate - several requests to the same host could read the same "last fetch" timestamp and all
> decide to proceed together, which is what let bursts of simultaneous Reddit requests through despite a
> nominal cooldown. `WaitForDomainSlotAsync` now wraps the whole read-wait-write in a per-host lock, and is
> called before *every* actual HTTP attempt (including 429 retries), not just once per source - a source
> backing off from a 429 no longer leaves the shared host's cooldown stale while sibling sources race past
> it. Second, and more consequential: live testing (both through the app and with standalone `curl` calls
> using different User-Agents from the same machine) showed this is a genuine per-IP rate limit on Reddit's
> side, not just a pacing problem - a single request can already come back with
> `x-ratelimit-remaining: 0.0`, `x-ratelimit-reset: 13`. Reddit sends that header on every response (429 or
> not) but never sends the standard `Retry-After`, so the old blind "5s, then 10s" backoff was guessing
> where a real answer was available. `FetchOneAsync` now reads `x-ratelimit-reset` and uses it as that
> host's cooldown going forward (clamped 2-60s), shared across every source hitting that host - confirmed
> live: sources that got a 429 with a 9s reset successfully retried and returned 200 a few seconds later,
> while sources hit during a deeper exhaustion window (accumulated from a full day of repeated testing
> against 107 subreddits) correctly backed off much longer (42-51s) instead of hammering a budget that
> wasn't going to refill in 10s anyway. This is real, honest self-healing driven by what Reddit's own
> servers report - not a client-side trick, and it can't make an already-exhausted IP-wide budget refill
> any faster, only avoid making it worse and recover as soon as the budget genuinely allows it.

> **WebSub reality check:** none of AiPulse's ~40 default sources currently declare a hub (checked YouTube,
> WordPress.com, Feedburner, Blogger - all previously reliable examples, none do anymore). The subscribe/
> verify/HMAC-signature/renew mechanics are built and directly verified (simulated hub requests against
> the callback endpoints), but real end-to-end delivery depends on a source that still runs a hub, which
> is now rare. It'll activate automatically the day one of your sources adds one - nothing to configure
> beyond `WebSub:PublicBaseUrl`.

- Fixed the News Feed's Grid view metadata row wrapping raggedly mid-word (author names/timestamps breaking
  across lines instead of between chunks) - `.source-identity`'s children gained `white-space: nowrap` so
  the row wraps between whole chunks (source, date, reading time), never inside one, and `FeedItemCard`'s
  Author line is now shown only in the wider List layout, since it was the main density contributor to the
  overcrowding in the narrower Grid cards.
- Explore's GitHub Trending repos column gained a persisted daily history + calendar filter, the same
  pattern News already has: `TrendingRepoHistoryService` snapshots each since-window's leaderboard once per
  day (`App_Data/trending-repo-history.json`), and a calendar toggle next to Refresh lets you pick a past
  date to browse that day's snapshot instead of the live scrape - `MonthCalendar` gained a `CountsOverride`
  parameter so it could serve both News' per-day item counts and this per-day repo-count map without
  duplicating the calendar UI. Scoped to repos only (not Trending Developers), matching what was actually
  asked for; before this, `GitHubTrendingService` only ever held the latest scrape per since-window in
  memory, so "what was trending last week" was unanswerable.
- Free AI APIs' discovery job now keeps a persisted run-history log (`DiscoveryRunLogEntry`, shown as a
  collapsible list in the admin panel) instead of only ever showing the latest run's summary - so an admin
  can actually see discovery activity over time (candidates found, entries flagged) rather than just
  "last run: 2 new candidates."
- Generalized the Free AI APIs "discovery queue" pattern (deterministic detection → admin-reviewable
  candidate → pre-filled add form, never auto-published) to Glossary, Tools & Tips, and the Learning Hub -
  turning three previously read-only "here's what's trending" chip lists into actual growth mechanisms:
  - **Glossary:** the existing 14-day tag-frequency "emerging terms" panel now also queues each term as a
    `ContentCandidate` (kind `Glossary`) for admin review instead of just displaying it.
  - **Tools & Tips:** GitHub Trending repos not already on the curated list (by name/URL) and matching an
    AI-dev-tooling keyword filter (agent, copilot, llm, rag, langchain, etc. against the repo's name/
    description) get queued as candidates.
  - **Learning Hub:** the existing "emerging topics not yet in your roadmap" tag panel now also queues each
    uncovered tag as a module candidate.
  - All three share one `ContentCandidates` table (discriminated by a `Kind` column) rather than three
    near-identical ones, since the Pending/Approved/Dismissed workflow and dedup-on-insert logic
    (case-insensitive substring overlap against existing entries and other pending candidates) is identical
    for all three. Unlike Free AI APIs/Sources, Glossary/Tools/Learning don't have DB-backed CRUD - they
    stay static-JSON-based, so approving a candidate appends the admin-filled entry directly to
    `Data/{glossary,tools,learning}.json` and reloads the in-memory cache, rather than adding a full DB
    migration these pages don't otherwise need.
- Added a link to Trendshift (`trendshift.io`) from the GitHub Trending section of Explore - deliberately
  scoped to a link-out, not a scrape. Checked first: Trendshift's `robots.txt` disallows `/api/`, and its
  trending page is a client-rendered Next.js SPA with no repo data in the raw server HTML (confirmed via a
  direct `curl`, not just guessing from the page ToS) - unlike `github.com/trending`, which is static HTML
  with no `robots.txt` disallow and already has an explicit ToS trade-off sign-off elsewhere in this file.
  Scraping it the way GitHub Trending is scraped wasn't a good-faith option, so this stays a link rather
  than mimicking its data.
- Reading Stats gained a "Learning activity" panel (streak, modules completed, time invested, link to the
  Learning Hub) alongside the existing reading metrics, so the page reads as one real tracer of activity on
  the system - reading and learning together - instead of two separate silos that happened to live on
  different pages. Reuses `ReadingState.LearningHistory`/`CompletedModuleCount`, already tracked for the
  Learning Hub's own streak/time-invested numbers from the previous round.

> **This round's verification caveat:** every page above builds cleanly and was checked by reading the
> rendered markup/logic, but wasn't click-tested against a live signed-in session this time - the assistant
> declined to type the admin password into the login form to drive that verification itself, treating
> credential entry as a hard boundary regardless of whose account it is or that it came up earlier in this
> same project. That's a stricter line than earlier rounds in this file drew (several of which did sign in
> to verify live, including the Mark-all-as-read fix above). Worth a manual click-through before trusting
> this batch the way the earlier ones were verified: the Grid-view wrapping fix against a narrow viewport,
> the Explore history calendar against a couple of days old repo snapshots, and each of the three new
> candidate-review queues (Glossary/Tools/Learning) end to end - queue a candidate, approve it, confirm it
> actually lands in the corresponding page and survives a restart.

- Hallmark design-system audit of the Dashboard and Login/Register pages: dropped the decorative
  "eyebrow" tag from Login/Register (no ordinal function - a real anti-pattern flag) and corrected
  `design.md`'s description of the entry pages from "Marquee-lite hero" to what they actually are, a
  standard centered auth card.
- Free AI APIs' discovery queue now auto-populates the free-tier summary and supported-models fields
  (previously admin had to type both by hand) by parsing the `**Credits:**`/`**Limits:**` line and the
  `**Models:**` list straight out of the discovery source's own per-provider listing - deterministic
  parsing, no AI. Always shown as a suggestion to verify before saving, never auto-saved.
- Reorganized the sidebar into two groups - Overview (Dashboard, News Feed, Explore, Free AI APIs) and
  Learning (Learning Hub, Reading Stats, Glossary, Tools & Tips) - and reworked the News Feed's filters:
  Unread is now the first tab and the default view (was All); the redundant Today tab was removed
  (already covered by the calendar); the four content-type tabs (News/Research/Tools/Community) moved
  into a Category dropdown alongside the existing ContentType/Level filters; and a source-search box was
  added above the source chip list, which can otherwise run to 100+ entries.
- User/permission overhaul: a third **Curator** role scoped to just the four content-curation admin
  panels (Free AI APIs, Glossary, Tools & Tips, Learning Hub) - everything system-level (Users, Sources,
  Source Health, Playground, backup/restore) stays Admin-only. Account security: DB-backed login
  lockout (5 failed attempts → 15 min), a security-stamp mechanism that invalidates a user's existing
  sessions the moment an admin force-logs-them-out, resets their password, or disables their account,
  and a full audit log (Users.razor gained an admin view of it, plus bulk approve/disable and an
  unlock button). Self-service password reset via emailed link - falls back to logging the link to the
  server console, or an admin-generated link from Users.razor, when no SMTP is configured (mirrors
  `ISummarizer`'s "optional, standalone by default" pattern). Registration is now open to anyone and
  auto-approves immediately (no admin gate) - a deliberate choice given this is a self-hosted app the
  operator controls network access to; `Auth:RequireApproval` can be flipped back to `true` in
  `appsettings.json` to restore the gate without a rebuild.
- Basic SEO: almost every page is `[Authorize]`-gated and would just redirect a crawler, so the main
  layout now emits `noindex, nofollow` by default; Login and Register (the only genuinely public pages)
  stay indexable with their own canonical link, description, and Open Graph tags, and `robots.txt`
  allows just those two paths.

- Google/GitHub OAuth sign-in, admin-configurable from Settings.razor rather than appsettings.json - an
  admin pastes a provider's Client ID/Secret and checks Enabled, and it takes effect on the next sign-in
  attempt with no restart (a DB-backed `OAuthProviderSettings` table, read live by a custom
  `IConfigureOptions<T>` per provider). Account linking on first external sign-in: match by
  `(Provider, ProviderKey)` if seen before, else by email against an existing password account, else
  create a new one under the same `Auth:RequireApproval` gate as normal registration. Login.razor only
  shows a provider's button once it's genuinely enabled and configured.

> **OAuth still needs real credentials from the site operator before it does anything** - the code path
> is live, but each provider stays inert until an admin creates a real OAuth app (Google Cloud Console /
> GitHub Developer Settings) and pastes its Client ID/Secret into Settings. Also not yet
> click-tested end to end (a real Google/GitHub round-trip, plus the admin settings form itself) since
> that needs both an Admin session and real provider credentials, neither of which the assistant has.

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
