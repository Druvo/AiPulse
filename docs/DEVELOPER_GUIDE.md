# Developer guide

Everything a developer needs to run, configure, extend, test, ship and operate this project, in one place. Deeper references:
[ARCHITECTURE.md](ARCHITECTURE.md) (how the code is organised) and [OPERATIONS.md](OPERATIONS.md) (running it on a server).

- [1. What is in the box](#1-what-is-in-the-box)
- [2. Run it locally](#2-run-it-locally)
- [3. Configuration reference](#3-configuration-reference)
- [4. Sign-in: passwords, two-step, Google, GitHub, Facebook, Clerk](#4-sign-in)
- [5. Making someone an administrator](#5-making-someone-an-administrator)
- [6. Make it yours: site identity and content packs](#6-make-it-yours-site-identity-and-content-packs)
- [7. The mobile app](#7-the-mobile-app)
- [8. The public pages, install page and marketing](#8-the-public-pages-install-page-and-marketing)
- [9. APIs](#9-apis)
- [10. Testing and quality tools](#10-testing-and-quality-tools)
- [11. Deploying](#11-deploying)
- [12. Security model in one page](#12-security-model-in-one-page)
- [13. Troubleshooting](#13-troubleshooting)
- [14. Conventions and gotchas](#14-conventions-and-gotchas)

## 1. What is in the box

| Part | Where | What it is |
|---|---|---|
| Web app | this repo, `AiPulse.csproj` | ASP.NET Core 8, Blazor Server (interactive pages) plus static server-rendered pages, SQLite and JSON file stores |
| Mobile app | [`Druvo/AiPulseMobile`](https://github.com/Druvo/AiPulseMobile) (private) | Expo / React Native client for Android (and iOS from source) |
| Content packs | `Data/packs/*` and `Data/*.json` | Topic bundles: feeds, glossary, tools, tips, learning path |
| Tests | `tests/AiPulse.Tests` | xUnit unit + end-to-end tests (the real app hosted in-process, no internet) |
| Tools | `tools/qa`, `tools/packs` | Browser QA harness, pack generators and feed checker |

The product name, tagline and topic are settings (section 6), the code and URLs keep the internal name "AiPulse".

## 2. Run it locally

Prerequisites: .NET 8 SDK. (Python 3 and Microsoft Edge only for the QA tools; Node 20+ only for the mobile app.)

```bash
git clone https://github.com/Druvo/AiPulse.git
cd AiPulse
dotnet run --urls http://127.0.0.1:5257
```

- First start creates `App_Data/` (SQLite database, caches, keys) and the **bootstrap admin** from `Auth:Username` / `Auth:Password`
  (default `admin` / `changeme` in `appsettings.json`: change it before exposing the site; see below).
- Feeds are fetched in the background; give the first poll a minute. The news list fills in progressively.
- Use `http://127.0.0.1:5257` rather than `localhost` while developing if you also run other apps on `localhost`: stale cookies from
  another app break the anti-forgery check on the login form.
- To use your own admin password without editing tracked files, set an environment variable: `Auth__Password=...` (a double underscore
  stands for `:`). Or put a PBKDF2 hash in `Auth:PasswordHash` (`/auth/hash?password=...` prints one in Development only).
- Developer secrets (API keys for AI providers, etc.) go in `appsettings.Development.json` (git-ignored) or environment variables. Never commit them.

Everything under `App_Data/` is runtime state and is git-ignored. Delete the folder to start again from scratch.

## 3. Configuration reference

Configuration is the usual ASP.NET Core stack: `appsettings.json` < `appsettings.{Environment}.json` < environment variables
(`Section__Key`) < command line. `appsettings.Production.json` and `appsettings.Development.json` are git-ignored.

| Key | Default | Meaning |
|---|---|---|
| `AllowedHosts` | `*` | Set to your domain in production |
| `Auth:Username` / `Auth:Password` / `Auth:PasswordHash` | `admin` / `changeme` / empty | Bootstrap admin, used only when the Users table is empty. Prefer the hash |
| `Auth:RequireApproval` | `false` | New sign-ups wait for an admin to approve them |
| `Auth:AdminEmails` | empty | Addresses that become Admin when they sign in through a provider that verifies them (section 5) |
| `Site:Name`, `Site:Tagline`, `Site:Description`, `Site:Topic` | AiPulse / AI | Starting identity; an admin can change it in Settings (stored in `App_Data/site-profile.json`) |
| `Notifications:PollMinutes` | `60` (15 on the demo) | How often all feeds are polled. Reddit is polled in rotating batches of 4 |
| `Notifications:NotifyReleases`, `NotifyWatchlist`, `MaxPerPoll` | `true`, `true`, `15` | Desktop notification rules |
| `Smtp:Host`, `Port`, `Username`, `Password`, `From` | empty | Outgoing email (password reset, weekly digest). Empty means email is logged, not sent |
| `Jwt:Secret` | empty | Signing key for the mobile app's tokens. **Leave empty**: a random key is generated into `App_Data/jwt.key`. Set one only to share a key between servers (32+ characters) |
| `Jwt:Issuer`, `Jwt:ExpiryDays` | `AiPulse`, `30` | Mobile token issuer and lifetime |
| `Ollama:BaseUrl` | local | Address of an Ollama server for the Playground |
| `Ai:*` | see `AiOptions` | Ask, summaries and limits. Providers and switches are edited live in Settings > AI Settings |
| `Security:CspReportOnly` | `true` | Content-Security-Policy mode. Set `false` to enforce once reports are clean |
| `Security:RateLimitingEnabled` | `true` | Per-IP limits on sign-in, 2FA, downloads, etc. |
| `Security:FrameAncestors`, `HstsMaxAgeDays`, `TwoFactorExemptSignInPaths` | see code | Embedding, HSTS and which provider sign-ins skip the local 2FA step |
| `WebSub:PublicBaseUrl` | empty | Turn on WebSub push by giving the site's public address |
| `Ops:BackupDirectory` | `backups/` next to App_Data | Where the ops console writes backups |
| `PublicApi:*` | see `PublicApiOptions` | Public API rate limits |

Runtime switches that live in the **database**, edited in Settings, not in files: AI providers and feature toggles, OAuth client
credentials (Google, GitHub, Facebook), Clerk, content packs (`App_Data/packs.json`), site identity.

## 4. Sign-in

Local accounts always work (username + password, lockout after repeated failures, optional two-step codes from an authenticator app,
password reset by email). Providers are additions: turning one off never locks anyone out.

All provider credentials are entered by an administrator in **Settings > Admin > OAuth sign-in** (or Clerk's own card). They are stored
encrypted with the Data Protection keys in `App_Data/dp-keys`, apply immediately and need no restart. A provider's button appears on
the sign-in page only when it is enabled **and** both values are filled in.

The redirect (callback) address you register with a provider is always `https://YOUR-SITE/signin-<provider>`:

| Provider | Register this redirect address | Paste into Settings |
|---|---|---|
| Google | `https://YOUR-SITE/signin-google` | Client ID + Client secret |
| GitHub | `https://YOUR-SITE/signin-github` | Client ID + Client secret |
| Facebook | `https://YOUR-SITE/signin-facebook` | App ID (in the Client ID box) + App secret |

Settings shows these with your site's real address filled in.

### 4.1 Enable Google login

1. Open the [Google Cloud Console](https://console.cloud.google.com/), create or pick a project.
2. **APIs & Services > OAuth consent screen**: choose *External*, fill in the app name and support email, add your domain, save.
   While the app is in *Testing*, only the test users you list can sign in; click **Publish app** to open it to everyone (basic
   sign-in scopes need no Google review).
3. **Credentials > Create credentials > OAuth client ID > Web application**. Under **Authorized redirect URIs** add
   `https://YOUR-SITE/signin-google` (exactly, including `https`). Authorized JavaScript origins are not needed.
4. Copy the **Client ID** and **Client secret**.
5. In the site: **Settings > Admin > OAuth sign-in**, Google row: paste both, tick **Enabled**, **Save**.
6. Open the sign-in page in a private window: "Sign in with Google" is there. Sign in; a new account is created (or, if a local
   account has the same *verified* address, that account is used).

### 4.2 Enable GitHub login

1. GitHub **Settings > Developer settings > OAuth Apps > New OAuth App**.
2. Homepage URL: your site. **Authorization callback URL**: `https://YOUR-SITE/signin-github`.
3. Register, then **Generate a new client secret**. Copy the Client ID and the secret.
4. Paste into the GitHub row in Settings, tick Enabled, Save.

The app asks GitHub for the `user:email` scope so it can read the address.

### 4.3 Enable Facebook login

1. [Meta for Developers](https://developers.facebook.com/apps/) > **Create app** (use case: *Authenticate and request data from users with
   Facebook Login*).
2. Add the **Facebook Login** product. **Facebook Login > Settings > Valid OAuth Redirect URIs**: `https://YOUR-SITE/signin-facebook`.
3. **App settings > Basic**: copy **App ID** and **App secret**; set a **Privacy Policy URL** and category (needed to go live).
4. Paste App ID into the Client ID box and App secret into the secret box of the Facebook row, tick Enabled, Save.
5. The app starts in **Development mode**: only people with a role on the app (admins, developers, testers) can sign in. Add testers
   under **App roles**, or switch the app to **Live** when you are ready.

Facebook sign-ins never attach to an existing account by email and never grant admin rights, because Facebook does not promise the
address is verified. A Facebook sign-in always lands on its own account.

### 4.4 Clerk

[Clerk](https://clerk.com) can run sign-up, email verification and social logins for you. Create a Clerk application, paste its
**Publishable key** in **Settings > Admin > Sign in with Clerk**, add this site to Clerk's allowed origins and (recommended) customise
the session token with `email`, `email_verified` and `name` claims. No Clerk secret key is stored. Switching it off never locks anyone out.

### 4.5 Two-step sign-in

Each user turns it on at **Account security** (`/account/security`): scan the QR code, enter a code, store the recovery codes. It
applies to username/password sign-in on the website **and** the mobile app. Admins can reset a user's two-step setting from the Users page.

## 5. Making someone an administrator

Roles: **Admin** (everything), **Curator** (review queues only), **User**.

Pick the way that fits:

1. **From the Users page** (an existing admin): open `/users`, change the person's role. This is the normal route.
2. **Owner address, automatically.** Set the address(es) in configuration and sign in through Google (or Clerk) with that verified
   address; the account becomes Admin, even on first sign-in:
   ```
   Auth__AdminEmails__0=you@example.com
   Auth__AdminEmails__1=cofounder@example.com
   ```
   On a systemd host add `Environment=Auth__AdminEmails__0=...` to the service (or a drop-in), then `sudo systemctl restart aipulse`.
   Facebook and GitHub addresses never grant roles. Every promotion is recorded in the audit log (`Role.PromotedByConfig`).
3. **Directly in the database** (recovery when nobody can sign in):
   ```bash
   sudo systemctl stop aipulse
   sqlite3 /opt/aipulse/App_Data/aipulse.db "UPDATE Users SET Role='Admin', Status='Approved' WHERE Username='yourname';"
   sudo systemctl start aipulse
   ```
   Take a copy of `App_Data` first. Find the username on the Users page or with `SELECT Username, Email, Role FROM Users;`.

There is no separate "super admin": Admin is the top role. To keep the owner permanently in charge, list their address in
`Auth:AdminEmails` and keep the bootstrap admin password in a vault.

## 6. Make it yours: site identity and content packs

### Site identity

**Settings > Admin > Site identity**: name, topic (one or two words, used as in "{topic} news"), tagline and description. They appear in
page titles, the landing page, sharing previews, the install manifest and the Atom feeds. Reset restores AiPulse. Without the UI, set
`Site:*` in configuration for the starting values.

### Content packs

A pack is a folder `Data/packs/<id>/`:

```
pack.json        { "name": "...", "topic": "...", "description": "..." }
sources.json     feeds (same format as Data/sources.json)
glossary.json    terms        tools.json     tools matrix entries
practices.json   tips         learning.json  a learning path (3 levels recommended)
```

The original AI content (`Data/*.json`) is the built-in pack **ai**. Starter packs shipped: `web`, `dotnet`, `python`, `devops`,
`security`. An admin turns packs on and off in **Settings > Admin > Content packs**; turning one on adds its feeds to Sources (once),
shows its glossary/tools/tips/learning, and rebuilds tagging. Turning one off hides its knowledge and switches its feeds off; nothing is
deleted. AI-only pages (Free AI APIs, Playground, model Explore tabs) disappear while the AI pack is off.

Write a new pack:

1. Copy a pack folder, edit the files, or add a content file under `tools/packs/content/` and a list in `tools/packs/make_sources.py`
   and run `python tools/packs/make_sources.py && python tools/packs/make_knowledge.py`.
2. Check the feeds are alive: `python tools/packs/check_feeds.py Data/packs/<id>/sources.json --prune`.
3. `dotnet test` validates every pack (unique names and URLs, https links, related terms resolve, a three-level learning path).

## 7. The mobile app

Repository: `Druvo/AiPulseMobile` (Expo, TypeScript, Expo Router). It signs in to your server with the same accounts and reads the news
feed, article text and the reading list, which is shared with the website.

**Server side.** The app talks to `/api/mobile/v1` (login, news, item, sources, tags, mark read, bookmarks). Login returns a bearer
token (30 days by default). Two-step codes are required when the account has them. Tokens stop working immediately if the account is
disabled or signed out everywhere. The signing key is generated into `App_Data/jwt.key` (back it up with `App_Data`; deleting it signs
every phone out).

**Run it in development.** In the app repo: `npm install`, `npx expo start`, scan the QR code with Expo Go. On the setup screen enter your
server address (use the PC's LAN address, not `localhost`).

**Build the APK.** Two ways, same script (`scripts/build_android.py`):

- *GitHub Actions (recommended).* One-time: from the app repo run `python scripts/set_github_secrets.py` (uploads the signing key as
  repository secrets). Then **Actions > Build Android app > Run workflow** (or `gh workflow run android.yml`). Download the
  artifact: `AiPulse-<version>.apk` and `latest.json`.
- *Locally.* Needs JDK 17+, the Android SDK (`ANDROID_HOME`) and Node. `python scripts/build_android.py --server https://YOUR-SITE`.
  On Windows the C++ part can fail with "Filename longer than 260 characters" for deep folders; build on Linux/CI instead.

**Signing key.** `scripts/build_android.py` creates `release.keystore` and `release-signing.properties` on first run (git-ignored).
**Back them up.** Android only installs an update over an existing install when it is signed with the same key.

**Publish the APK on your site.** Copy both files into the server's `App_Data/downloads/` folder:

```bash
scp AiPulse-1.1.0.apk latest.json user@server:/opt/aipulse/App_Data/downloads/
```

The site verifies the SHA-256 in `latest.json` against the file, then `/app` shows the **Download the Android app** card and
`/download/android` serves it (resumable, rate-limited). Remove the files to take the download down. Bump `version` and `versionCode`
in `app.json` for every release (Android refuses a lower `versionCode`).

**iPhone.** There is no App Store build. iPhone users install the web app from `/app` (Share > Add to Home Screen). A native iOS build
needs an Apple developer account and Xcode/EAS from the app repo.

## 8. The public pages, install page and marketing

Public (no sign-in): landing `/`, `/news`, `/topics`, `/releases`, `/explore`, `/glossary`, `/tools`, `/help`, `/this-week`, `/coverage`,
`/developers`, `/app`, `/feed.xml`, `/sitemap.xml`, `/robots.txt`. The single list is `Services/PublicSite.cs`: add a page there or a test fails.

`/app` is the "Get the app" and marketing page: install button (web app), QR code to open it on a phone, Android download card (when an
APK is installed), features, screenshots and FAQ. Replace `wwwroot/img/screenshots/*` to refresh the pictures (also used in the
install manifest). The installable web app (PWA) is `wwwroot/manifest.webmanifest` (name follows the site identity), `wwwroot/sw.js`
(offline notice only, caches nothing) and `wwwroot/offline.html`.

## 9. APIs

| API | Base | Auth | Use |
|---|---|---|---|
| Public read-only | `/api/v1` | none; personal token for `/me*` | Scripts and integrations; docs at `/developers`, spec at `/api/v1/openapi.json` |
| Mobile | `/api/mobile/v1` | bearer token from `/auth/login` | The Android/iOS app |
| Feeds | `/feed.xml`, `/this-week.xml`, `?tag=` | none | Feed readers |
| Fever | `/fever/` | per-user key | Mobile RSS readers |
| Ops (admin) | `/ops`, `/ops/status.json`, `/ops/audit.csv` | admin session | Monitoring and audit |

Personal API tokens are created in **Settings > Integrations**. They are stored only as hashes and shown once.

## 10. Testing and quality tools

```bash
dotnet test tests/AiPulse.Tests --filter "FullyQualifiedName!~LiveGemini"      # ~1,000 tests, about a minute
```

- The suite includes an **end-to-end layer** (`tests/AiPulse.Tests/E2E`): the real app runs in-process against a temporary folder with all
  outbound HTTP faked, so it never touches the internet. Every public route, access rule, header and endpoint is exercised.
- Stop a running dev server first (the `.exe` is locked on Windows). The test project is not in `AiPulse.sln`, so publishing ignores it.
- `LiveGemini` tests call a real provider and are excluded; they need a key.
- **Browser QA** (Microsoft Edge, no extra installs): `python tools/qa/shot.py --base http://127.0.0.1:5257 --paths / /news --width 390
  --height 844 --theme dark` writes a screenshot per page and prints overflow, console errors and a WCAG-AA contrast audit as JSON.
  Add `--user admin --password <local test password>` for signed-in pages. In Git Bash set `MSYS_NO_PATHCONV=1`.
- **CI**: `.github/workflows/ci.yml` builds and tests on every push.

## 11. Deploying

The proven recipe (details in [OPERATIONS.md](OPERATIONS.md)):

```bash
dotnet publish -c Release -o publish
tar -czf aipulse.tgz --exclude=App_Data --exclude=appsettings.Development.json -C publish .
# upload, then on the server:
sudo systemctl stop aipulse
cp -a /opt/aipulse/App_Data /opt/aipulse/App_Data.bak.$(date +%Y%m%d-%H%M)
tar -xzf aipulse.tgz -C /opt/aipulse
sudo systemctl start aipulse
curl -s -H "Host: your.domain" http://127.0.0.1:5257/health      # Healthy
```

Never overwrite `App_Data/` or `appsettings.Production.json` from a build machine. Behind nginx/Cloudflare keep forwarded headers on
(`X-Forwarded-Proto`, `X-Forwarded-For`): the app trusts them for the real scheme and client address. WebSocket upgrade must be allowed
for `/_blazor`.

Environment variables for the service (systemd `Environment=` lines) are the right place for secrets such as `Auth__AdminEmails__0`,
`Smtp__Password` and `Jwt__Secret`.

## 12. Security model in one page

- Passwords: PBKDF2 hashes; account lockout; per-IP rate limits on sign-in, two-step, downloads and recovery pages.
- Sessions: HttpOnly, SameSite cookies tied to a per-account security stamp; "sign out everywhere" and disabling an account end sessions
  (including mobile tokens) at once.
- Two-step (TOTP) with recovery codes, replay protection and lockout; secrets encrypted with Data Protection.
- Providers: Google/GitHub/Facebook/Clerk accounts follow the same approval rule; only **verified** addresses link to existing accounts or
  grant admin via `Auth:AdminEmails`.
- Headers: HSTS, nosniff, referrer policy, permissions policy, frame-ancestors, and a Content-Security-Policy in report-only mode.
- Secrets never live in git: `Jwt` key in `App_Data/jwt.key`, OAuth secrets encrypted in the database, API tokens stored as hashes.
- Backups (`/backup/download`, `/ops`) are admin-only; they contain password hashes and keys.

## 13. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "Reconnection failed. Try reloading the page" (older builds) / a "Reconnecting..." banner | The live connection of an interactive page dropped (phone slept, network changed, server restarted). The page retries and reloads itself; static pages (landing, topics, releases, /app) never need the connection |
| Provider button missing on the sign-in page | The provider is not enabled, or the ID/secret is empty. Check Settings > Admin > OAuth sign-in |
| Google: `redirect_uri_mismatch` | The address registered in Google differs from `https://YOUR-SITE/signin-google` (scheme, host, path, trailing slash). Behind a proxy make sure `X-Forwarded-Proto: https` reaches the app |
| Facebook: "App not active" / "can't load URL" | The app is in Development mode and you are not a tester, or the redirect URI is not in *Valid OAuth Redirect URIs* |
| Sign in works but you are not admin | Provider address not verified or not in `Auth:AdminEmails`; promote from the Users page |
| Mobile: "Could not reach the server" | Wrong server address, phone not on the network, or a plain `http` address blocked by the OS; use https |
| Mobile login says a code is required | The account has two-step on: enter the authenticator or a recovery code |
| `/app` has no Android download | No `latest.json` + APK in `App_Data/downloads`, or the SHA-256 does not match the file |
| Login form fails with an anti-forgery error on localhost | Stale cookies from another app on `localhost`; use `127.0.0.1` or clear cookies |
| 429 Too Many Requests | The per-IP limit; wait a minute. Do not disable it in production |
| Tests fail with OutOfMemory on a busy machine | Stop dev servers, Gradle daemons and browsers; rerun |
| Feeds failing in Source Health | Reddit throttles anonymous clients: keep few subreddits (the seed enables 16). Check `tools/packs/check_feeds.py` for dead feeds |

## 14. Conventions and gotchas

- Razor: a variable named `page` clashes with `@page`; `@` inside single-quoted attributes needs `@@`. Shared inject: `@Site` (site
  identity) is available in every component through `_Imports.razor`.
- Page-specific styles go in a scoped `Component.razor.css` (needs a rebuild); global look is the `--ap-*` tokens in `wwwroot/app.css`
  (light in `:root`, dark in `[data-theme="dark"]`). Keep WCAG AA contrast in both.
- Adding a public page: register it in `PublicSite` (menu, entry or unlisted). A test enforces it.
- New table or column: this project uses hand-written schema bootstrap (see `UserService.EnsureSchemaAsync`, `KnowledgeBaseService`),
  not EF migrations. Add a guarded `CREATE TABLE`/`ALTER TABLE` so existing databases upgrade in place.
- Data the app writes to `Data/*.json` (admin-approved glossary, tools and learning entries) lives in the install folder, not
  `App_Data`. A deploy that overwrites `Data/` loses them; copy it back or exclude it from the upload.
- The mobile and public APIs share the prefix family `/api` but must not share routes: public is `/api/v1`, mobile `/api/mobile/v1`.
- Commit messages explain the why; keep tests green; never commit `App_Data`, keystores or provider secrets.
