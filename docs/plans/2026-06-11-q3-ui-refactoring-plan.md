# Q3 UI Refactoring Plan — Public/Member Experience

**Date:** 2026-06-11
**Scope:** Public and logged-in member surfaces (~2k members + external public, ~50% mobile). Admin screens (~20 users) explicitly deprioritized.
**Timing:** Q3, after the event — refresh and set up for the long term.
**Source:** 7-dimension parallel UX audit (mobile, design system, IA/nav, journeys, forms/interaction, i18n/a11y, tech foundation) — ~736 files read, 90+ findings with file:line evidence. Condensed findings inventory in the appendix.

---

## Part 1 — Analysis

**The headline:** this is not a badly designed app. There is a genuine design system (the parchment/Renaissance token layer in `tokens.css`), several genuinely excellent components (the shifts AJAX toggle, the notification bell popup, the `_Table` partial), and recently-touched pages are thoughtful. The problem is that quality is **bimodal and unenforced**: every good pattern in the codebase exists alongside 3–5 ad-hoc reimplementations of the same job, and nothing prevents the next page from being built off-system. The experience a user gets depends entirely on which year their page was written in.

### 1. Mobile fails at the exact moments that matter most

- **The single most-used member CTA is off-screen.** The shift sign-up table is 8 columns with an inline `min-width:130px` (`_EventRotaRow.cshtml:41`); on a 375px phone the sign-up button sits beyond a horizontal scroll the user has no cue to perform.
- **Unwrapped tables don't scroll — they silently amputate.** `site.css` sets `html, body { overflow-x: hidden }`, so the ~10 member-facing tables without `table-responsive` wrappers (Team/Roster, Team detail role slots, Store/Order Lines *and* Payments, Profile/Outbox, UserCalendar, Calendar grid) clip content with no scrollbar. A camp lead on a phone cannot see their order's line totals.
- **46KB of custom CSS contains 6 media queries.** The entire mobile strategy is "Bootstrap stacks columns." That handles stacking but not density: the calendar's 7 fixed columns become 53px-wide cells, the Issues page traps users behind an 80vh list panel, filter rows wrap chaotically.
- **The hamburger menu is a raw dump** of 9+ items + bell + language chooser + avatar with zero mobile-specific CSS, and the toggler has no `aria-label`/`aria-controls`/`aria-expanded`.
- **No PWA affordances** — no manifest, no apple-touch-icon, no theme-color. Half the users would pin this to a home screen during the event.

### 2. Critical: the app dies without internet — at a field event

Every page hard-depends on **five external CDN origins** (Bootstrap, FontAwesome, flag-icons, Google Fonts ×2 — fonts are loaded *twice*, once via `tokens.css:6` `@import` and once via `_Layout` `<link>`). On degraded connectivity at the event site: no icons (every icon-only button goes blank), no fonts, no Bootstrap CSS/JS — the navbar collapse itself stops working. The map stack (maplibre, mapbox-gl-draw, turf from unpkg; SignalR from jsdelivr) additionally has **zero SRI hashes** — a supply-chain hole. Self-hosting is cheap precisely because there is no build pipeline to complicate it.

### 3. The design system leaks raw Bootstrap at the highest-visibility moments

- **ThingsToDo — the first card every member sees on login — renders vivid Bootstrap blue** (`#0d6efd`): `site.css` overrides `.bg-primary`'s background but never sets the `--bs-primary` CSS variable, so `text-primary`/`border-primary` fall through.
- **`btn-success`/`btn-danger` were never themed** — raw Bootstrap green/red appear precisely in the high-stakes flows: governance voting, team approve/reject, unsubscribe.
- **The onboarding stepper** (a new volunteer's first form, Profile/ShiftInfo) uses Bootstrap-blue focus/selected tokens that `tokens.css` explicitly left at defaults (`--h-focus-ring: #0d6efd`, `--h-selected-bg: #e7f1ff`).
- **TicketStub is a sealed design island**: 18+ hardcoded hexes, its own radius/shadow, an emoji as its icon. Palette changes never reach it.
- **The parchment palette itself fails WCAG AA in places**: gold-on-parchment ≈ 2.03:1, sepia-light-on-parchment ≈ 2.74:1, amber badge text ≈ 3.3:1. Worse than usual here — phones in bright sunlight.
- 53 `!important` rules fight the CDN Bootstrap build; 273 inline `style=` attributes are invisible to any stylesheet; `tokens.css` and `site.css` declare conflicting body typography (the tokens file is misleading documentation — editing its body sizing does nothing because `site.css` wins on load order).

### 4. IA: features exist that members cannot find

- **Expenses has zero inbound links from anywhere.** A coordinator who is owed money must be told the URL.
- **The Guide — public, anonymous-accessible participant content — is orphaned.** Not in nav, not on the dashboard.
- **Store** is reachable only via Camp Edit or Team Details. **The notifications inbox** only via bell popup → "See all".
- **About/Privacy are invisible to anonymous visitors** — the footer was removed and its links moved to the avatar dropdown, which logged-out users don't have. (Privacy being unreachable is also a GDPR-optics problem; the only anonymous path is the cookie banner link.)
- **There is no "my stuff" hub.** Personal items are scattered across dashboard cards, a 9-item avatar dropdown, and orphan sub-pages (Profile/Outbox has no inbound link either).
- **Labels are org-jargon and inconsistently translated**: the nav item for shifts is labeled "Volunteer"; "Budget" leads to a public transparency page; "City" in English is "Mapa" in Spanish — two different mental models of the same feature. Bare-state (onboarding) users see nav items that all bounce them back to onboarding — a loop.

### 5. Journeys: dead ends and missing guardrails at the worst spots

- **"Bail" (cancel shift) — the most consequential everyday action — has no confirmation**, while lesser actions (leave camp, delete order line) all do. One mis-tap on a phone silently drops a committed shift.
- **Magic-link failure is one undifferentiated error page for ≥5 distinct causes**, and the expiry time is shown in unlabeled Madrid time to an international audience.
- **Ticket purchase is a black hole**: hard redirect to the external shop, then the dashboard says "You don't have a ticket yet" until a background sync runs — no "check again", no expectation-setting. Transfer success dumps the user on Home instead of the transfer list. Stripe payment return shows no confirmation at all.
- **Applicants who finish everything enter a perpetual "Continue setup" banner limbo** — there is no "you're done, a coordinator will review" state.
- **Shifts closed/no-event pages are dead ends** with zero CTAs, while the nav keeps advertising the section. Dietary-blocked sign-up buttons explain themselves only via hover tooltips — which don't exist on touch.

### 6. Forms: six dialects of the same language

Six distinct error-display idioms across member forms; required-field marking exists on exactly one form (Survey); **no double-submit protection on any full-page POST** (slow mobile connections + Camp Register / governance applications = duplicates); **Camp Register — a 6-card, 10-minute form — has no field-level validation, no client validation, and no unsaved-changes guard** (Profile Edit has an exemplary guard; nobody else got it). A 78-entry raw `<select>` for languages, sorted by ISO code. A real bug found en route: Store/Order (and Budget/CategoryDetail, Expenses/Detail, Expenses/Edit) register duplicate `data-confirm` handlers on top of the global one in `site.js`, so members get two confirm dialogs.

### 7. i18n: half-finished for a Spanish organization

The infrastructure is good (culture-aware dates, DB-persisted language preference, 5 locale files) — but **the navbar itself shows hardcoded English** ("Calendar", "Legal", "Budget", the entire Events dropdown) on every page in every language; **entire sections have zero Localizer calls** (Store, Expenses, Search, Calendar, Guide); and **39 EN keys are missing from Spanish** — including the account-suspension page a consent-locked Spanish speaker sees. Plus a11y basics: no skip link, unlabeled hamburger, HumanSearch has no ARIA combobox pattern or arrow-key navigation, icon-only badges convey status by color alone.

### What must be preserved (the redesign's contract)

- `shifts.js` interaction pattern: optimistic AJAX toggle, button disabled immediately, spinner preserving label width, focus returned after row swap, localized error via `data-error-msg`.
- Notification bell popup: full keyboard support (Escape/arrows/focus return), `role=dialog`, and the mobile full-screen takeover at 575.98px — the one truly corrective breakpoint in the codebase.
- `_Table.cshtml` / `_Pager.cshtml`: typed, accessible (aria-sort), responsive-wrapped, client/server modes.
- The token architecture itself (primitive → semantic → component layering; zero `!important` in tokens.css).
- The 30-ViewComponent layer with stable `<vc:>` call sites — templates can be redesigned without touching callers.
- Anti-enumeration magic-link flow; culture-aware date formatting (`DateFormattingExtensions.IsDayFirst`); ThingsToDo as an onboarding mechanism; the `authorize-policy` tag helper; `admin-shell.css` scoping under `body.admin-shell`.

---

## Part 2 — The Plan

### Strategic call: evolve, don't rewrite

A full SPA/Blazor rewrite was on the table and is rejected:

1. **The audit shows the gap is consistency, not architecture.** ~75% of views already inherit correct theming purely by using Bootstrap classes that `site.css` remaps. A rewrite re-opens 383 views of proven behavior to re-introduce bugs, and spends the quarter rebuilding what already works.
2. **Server-rendered MVC is the right technology for this product** — content-centric, form-centric, ~2k users, single server, and it is what the hard rules and analyzer regime are built around. An SPA adds a build pipeline, a second state model, and an API layer the architecture doesn't want.
3. **The leverage is in the component layer, which already exists.** Stable ViewComponent call sites mean a rewrite-grade visual outcome at refactor-grade risk.

Instead: a **design-system hardening + mobile-first consolidation program**, structured so each phase ships independently.

### End state — what "amazing" means concretely

- **One UI kit, enforced.** Every table, badge, empty state, form page, confirm action, and search-select renders through a shared component. The kit is mobile-first: the data-table component collapses to cards below `md`, forms are single-column on phones with correct input types/inputmodes, touch targets ≥44px. Raw `<table class="table">`, new inline styles, and hardcoded user-facing English in member views are blocked by guardrails (analyzer/CI gate, fitting the existing baseline discipline) so the system cannot erode again.
- **A revised parchment palette that passes WCAG AA** — same Renaissance character, adjusted lightness — applied via Bootstrap 5.3's native CSS-variable theming (`--bs-primary` etc.), which eliminates most of the 53 `!important` overrides *and* the blue/green/red leaks in one mechanism.
- **An IA with a "My" hub**: top nav reduced to ~4 groups (e.g. *Participate* — shifts/calendar/events/guide; *Community* — camps/teams/map/search; *My Hub* — profile, tickets, orders, expenses, shifts, applications, notifications; *Org* — about/legal/privacy, restored in a footer for anonymous users). Every feature reachable; labels named in user language (org input on terminology, EN+ES); a purpose-built mobile nav treatment instead of the collapse dump.
- **Journeys with no dead ends**: every terminal/waiting state has an explanation and a next step (ticket-sync pending state, applicant "you're done" state, closed-shifts CTAs, Stripe return confirmation); every destructive action confirms; every long form guards unsaved work and double-submits.
- **100% i18n coverage** of member-facing surfaces with ES parity, verified by a key-coverage check.
- **Field-event resilient**: all assets self-hosted, PWA manifest + icons + service-worker shell caching, so the app opens and navigates on terrible connectivity.

### Transition — five phases

Calendar time ≈ 6–9 agent-days of build. The true critical path is the items that don't parallelize: palette sign-off, nav vocabulary (EN+ES, needs org humans), and PR review bandwidth. Decide palette and vocabulary first — everything downstream is cheap to re-run but annoying to re-litigate. Phases 1–2 are sequential; 3 and 4 can overlap once the kit covers their sections. Each phase is a shippable PR batch — no big-bang cutover.

**Phase 0 — Baseline & decisions.**
Screenshot the top 8 journeys at 375/768/1280px as the before-state (`test-site` browser harness). Settle the two org-gated decisions: nav vocabulary ("Budget"/"City"/"Volunteer" in EN+ES) and the AA-revised palette values. Nothing else needs a committee.

**Phase 1 — Foundation hardening.** *Invisible to users, unblocks everything.* ~1 agent-day, ~8 mostly-mechanical PRs, one lane.
Self-host Bootstrap/FontAwesome/flag-icons/fonts (kill the double font load and the `@import`); add SRI to the map stack; merge the tokens.css/site.css conflicts so tokens are the single source of truth; move theming onto Bootstrap CSS variables and delete the `!important` pile; theme `btn-success`/`btn-danger`/`--bs-primary`; apply the AA palette; PWA manifest + icons + minimal service worker; layout-level a11y (skip link, hamburger ARIA, nav landmark labels); fix the duplicate-confirm bug.
*Verify:* before/after screenshots identical-or-better; Lighthouse a11y + contrast pass on the layout.

**Phase 2 — The component kit.** *The heart of the program.* ~2–3 agent-days: kit components sequential (judgment), migrations fan out as swarm batches over file lists.
Build or bless seven components, then migrate section by section in traffic order — Shifts, Profile/Onboarding, Camps/Teams, Store/Tickets, Calendar, the rest:
1. **DataTable** — extend `_Table` with a card-collapse mobile mode; migrate the ~95 hand-rolled tables.
2. **FormPage scaffold** — one validation idiom (field-level + summary), required-marking, submit-guard, unsaved-changes guard as shared behavior.
3. **StatusBadge** — one semantic mapping (state → color + icon + *text*), replacing the 5 competing badge dialects and color-only patterns.
4. **EmptyState** — icon + message + CTA, replacing the 6 ad-hoc patterns.
5. **SearchSelect** — generalize HumanSearch with full ARIA combobox + arrow keys; reuse for the 78-language picker.
6. **ConfirmAction** — the existing `data-confirm`, made mandatory for destructive POSTs.
7. **TicketStub re-skin** onto tokens.
Land the guardrail checks in the same phase so migrated sections cannot regress.
*Verify:* per-section before/after on mobile; kit components covered by the section's tests.

**Phase 3 — IA & navigation relaunch.** ~1 agent-day of code, gated on Phase 0 vocabulary.
Grouped nav + My Hub + restored footer; purpose-built mobile menu (grouped, large targets, labeled); wire in the orphans (Expenses, Guide, Store, Notifications inbox); hide dead nav from Bare-state users; apply the vocabulary in EN+ES. Ships as one visible "the app feels new" moment, riding on Phase 2's components.

**Phase 4 — Journey repairs.** ~1–2 agent-days; the appendix below is the backlog, worked as issue batches.
Onboarding (differentiated magic-link errors, timezone-labeled expiry, applicant done-state, skip-step feedback), tickets (sync-pending state with re-check, transfer redirect-to-list, recipient-requirements hint), shifts (Bail confirm, dietary explanation visible on touch, closed-state CTAs), store (Stripe return state), plus full i18n of Store/Expenses/Search/Calendar/Guide and the 39 missing ES keys.

**Phase 5 — Verification & lock-in.** ~0.5 agent-day plus fixes.
Device-matrix pass over the top 8 journeys; contrast + screen-reader audit; performance check on a throttled connection; confirm guardrails are green; after-screenshots vs Phase 0 baseline as the program's receipts.

---

## Appendix — Findings inventory

Condensed from the audit (severity is end-user impact, not code quality). Evidence abbreviated to the primary location; severities: C=critical, H=high, M=medium, L=low.

### Mobile & responsive

- [H] Hamburger collapse is an unstructured dump of 9+ items + bell/language/avatar; no mobile nav CSS; toggler lacks all ARIA — `_Layout.cshtml:34,69`
- [H] Shift sign-up table: 8 columns, inline `min-width:130px`, CTA off-screen at 375px — `_EventRotaRow.cshtml:41`, `_EventRotaTable.cshtml:6-44`
- [H] Team/Roster, Team/_RosterSection, Profile/Outbox tables lack `table-responsive`; body `overflow-x:hidden` clips silently — `Team/Roster.cshtml:59`, `site.css:31-33`
- [H] Store/Order Lines + Payments tables unwrapped; Stripe IDs in `<code>` overflow cells — `Store/Order.cshtml:86,177`, `Store/Index.cshtml:58`
- [H] Calendar grid: fixed-layout 7-col table, ~53px cells at 375px, banners clipped (`white-space:nowrap`), no scroll wrapper — `Calendar/Index.cshtml:60`, `site.css:1582-1675`
- [H] Issues two-panel layout: 80vh list panel above detail on mobile; fixed-width 220px search input wraps filters — `Issues/Index.cshtml:84,99-101`
- [M] UserCalendar component table on profile unwrapped — `Components/UserCalendar/Default.cshtml:25`
- [M] Shifts progress bars inline `min-width:120–160px` squeeze card headers — `Shifts/Index.cshtml:360,463`
- [M] Profile/Edit contact rows stack as unlabeled inputs on mobile; missing `type=tel`/`type=url` — `Profile/Edit.cshtml:144-205`
- [M] Notification popup fixed 340px absolute overflows 376–576px viewports (full-screen takeover only <576px) — `site.css:1073-1083,1422-1450`
- [M] CityPlanning history offcanvas hardcoded 360px — `site.css:2076-2078`
- [M] Store/Order numeric inputs missing `inputmode=numeric/decimal` — `Store/Order.cshtml:164`
- [L] Outbox vs Shifts/Mine inconsistent table pattern — `Profile/Outbox.cshtml:40`
- [L] No PWA: no manifest, apple-touch-icon, theme-color; only favicon.svg — `_Layout.cshtml:27`
- [L] Shifts filter row `col-auto` wraps uncontrolled at 375px — `Shifts/Index.cshtml:136-174`
- [L] Only 5–6 @media blocks in ~2,100 lines of site.css; most sections rely on grid stacking alone — `site.css:821,922,998,1422,1661,1744`

### Design system

- [H] `btn-success`/`btn-danger` unthemed (raw Bootstrap green/red) in governance voting, team approve/reject, unsubscribe — `site.css:300-433` (absent), `Governance/BoardVoting/Detail.cshtml:160-164`
- [H] ThingsToDo bleeds `#0d6efd`: `--bs-primary` never overridden, only `.bg-primary` background — `Components/ThingsToDo/Default.cshtml:3-8`, `site.css:476-478`
- [H] TicketStub: self-contained `<style>` island, 18+ hardcoded hexes, emoji icon — `Components/TicketStub/Default.cshtml:14-35,61`
- [H] WCAG AA failures: sepia-light on parchment ≈2.74:1, gold-on-parchment ≈2.03:1, amber badge ≈3.33:1 — `tokens.css:10,16,19,30`
- [M] About/Staff inline `font-family` strings bypass `--h-font-display` — `About/Staff.cshtml:7,29`
- [M] ≥5 competing badge/pill patterns; same visual encodes different semantics — `_RoleBadge.cshtml:5`, `_RotaBadges.cshtml:17`
- [M] Onboarding stepper (ShiftInfo) uses Bootstrap-blue `--h-focus-ring`/`--h-selected-bg` — `tokens.css:62-64`, `Profile/ShiftInfo.cshtml:125`
- [M] Empty states ad hoc across sections; no shared component — `Expenses/Review.cshtml:31`, `Notifications/Index.cshtml` (best example)
- [M] Bootstrap 5.3 subtle utilities (`bg-info-subtle`, `bg-body-tertiary`) untokenized in Feedback/Expenses/Governance — `Expenses/Index.cshtml:23`, `Governance/Index.cshtml:161`
- [M] Icon-only buttons without labels (Team index search etc.) — `Team/Index.cshtml:16`
- [L] `.card` fadeInUp `fill-mode:both` creates stacking context trapping modals (workaround races) — `site.css:954-965`
- [L] Public About page `table-dark` thead off-palette — `About/Index.cshtml:124,453`
- [L] `fa-regular` vs `fa-solid` mixed without intent in Expenses/VolunteerTracking — `Expenses/Detail.cshtml:192`

### IA & navigation

- [H] 9+ top-level nav items for an active member; no grouping; no My hub — `_Layout.cshtml:69-140`
- [H] Expenses: zero inbound links anywhere — `ExpensesController.cs:19-34`
- [H] Guide: public content, absent from all navigation — `GuideController.cs:12-19`
- [H] About/Privacy invisible to anonymous users (footer removed → avatar dropdown only) — `_Layout.cshtml:174`, `_LoginPartial.cshtml:72-85`
- [M] Store reachable only via Camp Edit / Team Details — `Camp/Edit.cshtml:256-268`, `Team/Details.cshtml:308-313`
- [M] "Budget" hardcoded English, label-destination mismatch (public transparency page) — `_Layout.cshtml:134-136`
- [M] "City" (EN) vs "Mapa" (ES) — same feature, different mental models — `_Layout.cshtml:97-99`
- [M] No "My stuff" hub; personal items split across dashboard/dropdown/orphans; Outbox unreachable — `Dashboard.cshtml:359-408`, `_LoginPartial.cshtml:40-98`
- [M] Notifications inbox only reachable via bell popup → "See all" — `Components/NotificationBell/Default.cshtml`
- [M] Bare-state users see nav items that all redirect back to onboarding (loop) — `_Layout.cshtml:88-95`, `MembershipRequiredFilter.cs:21-33`
- [L] "Governance" dropdown item shown to all members; opaque to ~95% — `_LoginPartial.cshtml:50-54`
- [L] Nav_Shifts resolves to "Volunteer" — matches neither page title nor mental model — `SharedResource.resx:62`
- [L] Search is icon-only in nav; lone icon in mobile text list; shown to Bare users — `_Layout.cshtml:79-87`
- [L] Home/Index can serve anonymous marketing page to authenticated edge-case users — `HomeController.cs:22-30`

### Journeys

- [H] MagicLinkError: one page, ≥5 distinct failure modes, one CTA — `AccountController.cs:472,488`
- [H] Onboarding limbo: completed applicants stuck under perpetual "Continue setup" banner; no done-state — `ThingsToDoViewComponent.cs:76-93`, `OnboardingProgressBannerViewComponent.cs:28-49`
- [H] No in-app ticket purchase feedback: external redirect + silent sync window, no "check again" — `Dashboard.cshtml:199-225`
- [H] Shifts "Bail" has no confirmation — only major destructive action without one — `Shifts/Mine.cshtml:107-126`
- [H] Consent-suspended redirect loop when unsigned set is empty (self-heal race) — `OnboardingWidgetController.cs:190-198`, `MembershipRequiredFilter.cs:65-73`
- [M] Magic-link expiry shown in unlabeled Europe/Madrid time — `AccountController.cs:446-449`, `MagicLinkSent.cshtml:12-14`
- [M] Transfer success redirects to Home, not the transfer list — `TicketTransferController.cs:73-74`
- [M] Transfer recipient errors conflate no-match / self / unregistered; no "recipient must have an account" hint — `TicketTransferController.cs:46-59`
- [M] BrowsingClosed / NoActiveEvent are CTA-free dead ends — `Shifts/BrowsingClosed.cshtml`, `Shifts/NoActiveEvent.cshtml`
- [M] Dietary-blocked sign-up: tooltip-only explanation, invisible on touch — `_ShiftToggleButton.cshtml:9-16`
- [M] Store undiscoverable from nav (see IA) — `Team/Details.cshtml:308-313`
- [M] Stripe return: no success/cancel messaging on the order page — `StoreController.cs:139-157`
- [M] Camp join: "This does not join you to the camp" disclaimer contradicts the "Request to join" button — `Camp/Details.cshtml:318-339`
- [L] Onboarding Shifts step "Not right now" skips silently; mis-tap risk near filter pills — `OnboardingWidgetController.cs:172-177`
- [L] Past signups render raw enum status values — `Shifts/Mine.cshtml:195-209`
- [L] Team request withdrawal redirects to MyTeams instead of back to the team — `TeamController.cs:561-583`
- [L] OAuth failure shows one generic message for all causes — `AccountController.cs:55-65`

### Forms & interaction

- [H] No double-submit protection on any standard full-page POST — `Governance/Applications/Create.cshtml:105`, `Team/Join.cshtml:40`
- [H] Six distinct error-display idioms across member forms — `Profile/Edit.cshtml:18-23` vs `Camp/Register.cshtml:31` vs `Account/Login.cshtml:28` etc.
- [H] Camp Register: no field-level validation spans, no client validation, errors top-only on a 6-card form — `Camp/Register.cshtml:31-240`
- [M] Duplicate `data-confirm` handlers → double confirm dialogs (Store/Order, Budget/CategoryDetail, Expenses/Detail, Expenses/Edit) — `site.js:20-28` + `Store/Order.cshtml:310-316`
- [M] 78-entry raw language `<select>`, ISO-code sorted, no search — `LanguageCatalog.cs:12-94`, `Profile/Edit.cshtml:263-269`
- [M] 10 production console.log/error in Profile Edit Places autocomplete — `Profile/Edit.cshtml:599-649`
- [M] Unsaved-changes guard exists only on Profile Edit; Camp Register / Governance / Survey / Calendar have none — `Profile/Edit.cshtml:1061-1174`
- [M] Required-field marking only on Survey — `Survey/Page.cshtml:51-53`
- [L] Feedback/Help widgets show hardcoded English error toasts (shifts.js `data-error-msg` is the right pattern) — `Components/FeedbackWidget/Default.cshtml:82`
- [L] Profile Edit renders the validation summary twice — `Profile/Edit.cshtml:18-23,561-566`

### i18n & accessibility

- [H] Navbar hardcoded English: Calendar, Legal, Budget, Events + children — `_Layout.cshtml:95,103-107,128,135`
- [H] Zero Localizer coverage in Store, Expenses, Search, Calendar, Guide — `Store/Index.cshtml`, `Expenses/Index.cshtml`, `Search/Index.cshtml`
- [H] 39 EN keys missing from `SharedResource.es.resx`, incl. account-suspension page, LinkedAccounts, shift name-required flash — `SharedResource.resx:2464` vs es.resx
- [H] Hamburger toggler: no aria-label/aria-controls/aria-expanded; targets class not id — `_Layout.cshtml:69`
- [H] HumanSearch: no ARIA combobox pattern, no arrow-key navigation to results — `Components/HumanSearch/Default.cshtml`
- [M] Language preference cookie-only for anonymous; chooser is small text-only target — `LanguageController.cs:20-24`
- [M] Icon-only action buttons without aria-labels in Camp edit/members flows — `Camp/Edit.cshtml:61-63,325`
- [M] Ticket matched/unmatched: color+icon only, no text (Orders, WhoHasntBought are member-facing) — `Ticket/Orders.cshtml:90`, `Ticket/WhoHasntBought.cshtml:62,66`
- [M] No skip-navigation link; 10+ tab stops before content — `_Layout.cshtml:33-151`
- [L] Main `<nav>` landmark unnamed — `_Layout.cshtml:34`
- [L] `.card` animation stacking-context flash on modal open (reduced-motion handled correctly) — `site.css:954-1007`

### Tech foundation

- [C] Five CDN origins required for any styled render; no self-hosting; fonts double-loaded (`@import` + `<link>`); flag-icons + Google Fonts without SRI — `_Layout.cshtml:15-26`, `tokens.css:6`
- [H] Map stack (maplibre/mapbox-draw/turf from unpkg, SignalR from jsdelivr) has zero SRI — `CityPlanning/BarrioMap.cshtml:15-16,123-126`
- [H] jQuery+validate loaded on exactly 8 forms; rest is vanilla — two coexisting validation systems — `_ValidationScriptsPartial.cshtml`
- [H] 95 hand-rolled `<table>` implementations bypass the `_Table` partial (64 member-facing) — `_Table.cshtml:86-92` vs e.g. `Calendar/Index.cshtml`
- [H] 53 `!important` overrides fighting CDN Bootstrap — `site.css:85-86,272-273,301-321`
- [M] tokens.css/site.css conflicting body typography; grain background defined twice — `tokens.css:116-127`, `site.css:31-50`
- [M] 273 inline `style=` attributes in member views; z-index values untokenized — `_Layout.cshtml:177,188`
- [M] Google Fonts double-load adds render-blocking latency — `tokens.css:6`, `_Layout.cshtml:24-26`
- [M] 19 raw `<style>` blocks render in body (invalid HTML, mid-render recalc); 6 `@section Styles` — `Shifts/Index.cshtml:37-44`
- [M] City-planning: 22 unbundled ES modules = 22 sequential requests on poor connections; no build pipeline anywhere — `js/city-planning/`, `BarrioMap.cshtml:127`
- [M] 45 `@section Scripts` islands; non-trivial inline JS (Notifications bulk-select 100+ lines, Profile Edit Places bootstrap) — `Notifications/Index.cshtml:153-240`
- [L] EasyMDE init polls via setInterval → up to 5s CLS jump on slow connections — `MarkdownEditorTagHelper.cs:221-273`
- [L] *(constraint, positive)* 30 ViewComponents with stable `<vc:>` call sites + 3 tag helpers are the consolidation surface — `ViewComponents/`, `Views/Shared/Components/`
