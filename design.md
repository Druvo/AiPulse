# Design — AiPulse

A locked design system for this app. Every subsequent redesign pass should read this
file before touching CSS — extend or amend it when the system needs to grow, rather
than reinventing tokens per page.

## Genre
modern-minimal

## Macrostructure family
AiPulse is ~entirely an app product, not a marketing site, so there are three families:

- **App pages** (Dashboard, News Feed, Explore, Learning Hub, Glossary, Tools & Tips,
  Reading Stats, Free AI APIs, Settings, Sources, Users, Source Health, Playground):
  **Workbench** — the existing sidebar + content shell, refined density/voice, not
  rebuilt. These pages vary only in internal card/table/chart layout, never in
  macrostructure.
- **Entry pages** (Login, Register, Forgot/Reset password): a **standard centered auth
  card** — logo, wordmark, form, signature. Deliberately boring: no eyebrow tag, no code
  demo, no ⌘K (those are Cobalt's marketing-page signature moves, not a sign-in page's
  job). A user's muscle memory for "sign in" is a centered card with a name and a
  form — that's exactly what this is, not a hero.
- **Public page** (Landing, at `/`): the one page in the app that's allowed to be a
  marketing page, since it's the actual public front door and the only page search
  engines should index. **Bento Grid** macrostructure — N1b nav (frost-on-scroll,
  transparent over the hero) + a fixed-height typographic hero (no imagery) + an
  irregular feature/stat grid mixing real feature tiles with real numbers (202 sources,
  36 glossary terms, 17 learning modules — pulled from the shipped `Data/*.json`
  baseline, never invented) + a one-line closing statement + a single-button CTA strip +
  Ft1 mast-headed footer. No testimonials, no logo wall, no pricing table — none of
  those exist for this project and inventing them would violate the same "no fabricated
  content" rule as everywhere else in this system. Lives in its own layout
  (`LandingLayout.razor`, no sidebar, no auth card) and its own stylesheet
  (`wwwroot/landing.css`) referencing the same `--ap-*` tokens as the rest of the app,
  rather than a per-page token fork.

## Theme
Custom — anchored on AiPulse's existing cyan/teal brand color (not a catalog swap),
borrowing Cobalt's structural DNA (cool paper, hairlines over shadow, tight radii, one
signal accent) at AiPulse's own hue instead of Cobalt's indigo-blue.

- Paper band: cool light, `oklch(98% .006 200)` — engineered near-white, not `#fff`.
- Display style: grotesk-sans (Space Grotesk 500/600).
- Accent hue: cyan-blue, `oklch(58% .14 200)` — same register as the app's original
  default accent, re-derived in OKLCH for consistent contrast across light/dark.

### Tokens (see `wwwroot/app.css` `:root` / `[data-theme="dark"]` / `[data-accent]` for
the full, authoritative values — this is a summary, not a duplicate source of truth)

- `--ap-bg`, `--ap-surface`, `--ap-surface-2`, `--ap-text`, `--ap-text-secondary`,
  `--ap-muted`, `--ap-border`, `--ap-border-strong` — neutral scale, OKLCH, hue ~200-220.
- `--ap-accent`, `--ap-accent-hover`, `--ap-accent-2`, `--ap-accent-soft`,
  `--ap-accent-soft-text` — the one signal color. **5 user-selectable presets**
  (cyan default, violet, rose, amber, emerald) via `[data-accent]` on `<html>` — this is
  a real Settings feature (Appearance page), not decoration; preserve it.
- `--ap-success` / `--ap-warning` / `--ap-danger` (+ soft variants) — semantic colors,
  same OKLCH discipline.
- `--ap-shadow-sm` / `--ap-shadow-md` — barely-there lifts. Hairline borders carry
  structure, not blur.
- `--ap-radius-sm` (6px) / `--ap-radius-md` (8px) / `--ap-radius-lg` (12px) — tight,
  "ruler-drawn" radii, not soft pills.
- `--ap-sidebar-*` — sidebar-specific neutral tones (own bg/border/text/hover set).

Light and dark both defined via `[data-theme="dark"]`; every accent preset has its own
dark-mode variant. Never introduce a new color as a literal hex/rgb — add a named token.

## Typography
- Display: **Space Grotesk** 500/600, self-hosted (`wwwroot/fonts/space-grotesk-variable.woff2`).
  Headings only (`h1`–`h6`, `.stat-num`, wordmarks).
- Body: **IBM Plex Sans** 400/500/600, self-hosted (`wwwroot/fonts/ibm-plex-sans-variable.woff2`).
  Everything else.
- Mono/outlier: **JetBrains Mono**, self-hosted (`wwwroot/fonts/jetbrains-mono-variable.woff2`).
  Nav section labels, kbd hints, code/snippet blocks — machine-readout register only, not a
  third body face. (An eyebrow tag on Login/Register used this face too, but was removed as
  a decorative anti-pattern with no ordinal function — see the audit note below.)
- All three fonts are self-hosted (not a Google Fonts CDN link) — loading a third-party
  font CDN on every page view would leak every visitor's IP to Google on every load,
  which conflicts with the self-hosted ethos `FaviconService` already established for
  favicons. Fetch once, serve locally.
- `Inter` (the previous body face) is retired — banned as an AI-default font by the
  design system this app now follows. `wwwroot/fonts/InterVariable.woff2` is left in
  place (harmless) but no longer referenced.

## Spacing
4-point named scale (`--ap-space-3xs` through `--ap-space-3xl` in `app.css` — `2xl`/`3xl`
added for the Landing page's section-level rhythm, which needs bigger gaps than any App
page ever did). New custom CSS should reference these tokens; Bootstrap's own utility
classes (`g-2`, `mb-4`, etc.) remain in use throughout existing App-page markup — not
worth replacing wholesale there.

## Motion
- Easings: `--ap-ease-out: cubic-bezier(.16,1,.3,1)`, `--ap-ease-in-out: cubic-bezier(.65,0,.35,1)`.
- Durations: `--ap-dur-fast: 120ms`, `--ap-dur-base: 180ms`.
- Reveal pattern: none — AiPulse is a daily-use dashboard, not a marketing scroll story.
  Motion is hover/press feedback only (card lift, button color shift, nav active-state).
- Respect `prefers-reduced-motion` on anything animated (skeleton shine, spinners).

## Microinteractions stance
- Silent success over celebratory toasts (already the pattern via `ToastService`).
- No new modals for reversible actions — Undo toasts already cover Mark-all-as-read /
  Remove bookmark.

## CTA voice
- Primary: solid `--ap-accent` fill, `--ap-radius-sm` (6px), no pill shapes.
- Secondary: outline in `--ap-border-strong` / text in `--ap-text-secondary`.
- No gradient fills, no gradient text anywhere (this replaced two previous gradient
  treatments: the stat-card numbers and the login wordmark — both are flat-color now).

## Per-page allowances
- App pages: function carries the page. No enrichment, no imagery beyond what's
  already data-driven (favicons, thumbnails, avatars).
- Entry pages (Login/Register): logo + wordmark is the full extent of "enrichment" — no
  eyebrow tag, no illustration, no code demo.

## What pages MUST share
- The token set above (colors, fonts, radii, shadows, motion) — every page pulls from
  `wwwroot/app.css` (the landing page also pulls from its own `wwwroot/landing.css`,
  which references the same tokens rather than forking them), no per-page inline
  overrides.
- The sidebar nav (N3 side-rail) and its active-state left accent bar — App pages only;
  Entry pages use no nav, the Landing page uses its own N1b top nav (see above).
- Button/card/badge voice (radius, border treatment, hover lift).
- The 5-accent-preset + light/dark system — a real user-facing Settings feature. The
  Landing page inherits this automatically (no extra wiring needed): `App.razor`'s
  `applyTheme()` script and `app.css`'s tokens load on every page including `/`, so a
  returning visitor's stored theme/accent preference still applies before they've ever
  signed in.

## What pages MAY differ on
- Internal layout of app pages (grid vs. list, chart types, table density) — content-
  specific, not a macrostructure choice.
- Each family's macrostructure: Workbench (App) vs. standard centered auth card (Entry)
  vs. Bento Grid (Public/Landing) — three genuinely different page shapes, not
  colour-swaps of one template.

## Exports

### tokens.css
See `wwwroot/app.css` `:root` block — it *is* the tokens file for this project; a
separate copy would drift. Reference it directly.
