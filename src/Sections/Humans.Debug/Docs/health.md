# Debug — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

The developer's window onto the running process. An admin opens it to answer "what is the
app doing right now, and is it healthy?" — what has been logged since the last restart, which
requests failed and for whom, what browsers and screens visitors bring, how the database and
the caches are being hit, how long named operations take, which configuration values are set,
and which sections the app was composed from. Nearly all of it is read from memory and
vanishes on the next deploy; the section says so on every page.

Operational levers sit beside the read-outs: reset a counter, and clear Hangfire's stale
locks after a job dies mid-run. One anonymous machine endpoint reports migration status to
deployment tooling.

A second, unrelated job shares the roof: the design reference — every colour token, control
and typographic style, and a catalogue of every reusable widget rendered against sample data,
so a designer or developer can see what already exists before building another one. The admin
dashboard also borrows one card from here: the Venn / UpSet picture of how the member base
overlaps across profile, ticket, shift and marketing opt-in.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "Show me the recent buffer of X" (log events, error responses) | admin, reading | `DebugController.Logs` / `HttpErrors` — count-clamped, newest first, lifetime per-level/per-code counts as toggles |
| "Show me the tally since restart of X" (queries, cache hits, client mix, status codes, timings) | admin, reading | `DbStats` / `CacheStats` / `ClientStats` / `Timings` — snapshot → rows with rounding and percent share |
| "Reset that tally" | admin, acting | `ResetDbStats` / `ResetCacheStats` — POST, anti-forgery, redirect back |
| "What is configured, without showing me secrets?" | admin, reading | `Configuration` — every registered key, sensitive values masked to a 4-char prefix |
| "What was the app composed from?" | admin, reading | `Sections` — the DI-published catalog, rendered as-is |
| "What migrations has this instance applied?" | deployment tooling, anonymous | `DbVersion` — JSON, names and counts only |
| "Unstick Hangfire" | admin, acting | `ClearHangfireLocks` — the one write to infrastructure state; logged at Warning |
| "What does the design system contain?" | designer/developer | `ColorPalette` (static, anonymous), `WidgetGallery` (admin; live keys for the search-row cards), `FormatGallery` and `Translations` (reflection over Base's formatter and resource sets) |
| "How does the member base overlap?" | the admin dashboard, via a chrome slot | `UserSetMembershipCardViewComponent` + `UserSetMembershipCalculator` — bitmask partition of the user cache over profile, ticket, shift and marketing |

Vocabulary: none crosses the boundary. Every model here is `internal` and read only by
this section's views.

## 3. Structure

The shapes imply a section with no data layer at all:

- **One controller per audience.** `DebugController` (diagnostics + the
  reflection galleries), `WidgetGalleryController` (the widget catalogue, which needs live
  keys and sample DTOs from other sections' contracts) and `ColorPaletteController`
  (a static page). All `internal`, routed by Shell's feature provider.
- **View models per page**, internal records/classes, built in the controller from the
  Base singletons' snapshots. No service layer: there is no rule to enforce, only projection.
- **One static calculator** (`UserSetMembershipCalculator`) and its view component, because
  the dashboard card's mask math is the one piece of logic worth a unit test.
- **Contribution seams at the root**: `SectionAdminNav` (the Diagnostics and Design
  sidebar groups) and `SectionChrome` (the dashboard card). `Section.Register` is empty and
  the class exists only so Shell discovers the assembly.
- **Docs** — the invariant doc, the authorization table, and one feature spec per
  telemetry screen that has non-obvious capture rules (client stats, HTTP errors).
- **No resx, no Contracts project, no tests of the views** beyond the local-only render
  walk in `Humans.Integration.Tests`.

## 4. Invariants

- **Every page and endpoint is Admin-only** (`PolicyNames.AdminOnly`, class-level on
  `DebugController` and `WidgetGalleryController`) **except the deliberate anonymous
  surfaces**: `/Debug/DbVersion` (migration names and counts, for deployment tooling) and
  `/ColorPalette` (static design tokens, no data).
- **Sensitive configuration values never render in full**: at most the first four
  characters, and values of four characters or fewer are fully masked.
- **The section owns no tables and writes no domain state.** Its only writes are counter
  resets and the Hangfire lock clear, all POST + anti-forgery, and the lock clear is logged
  at Warning so it reaches production logs.
- **Every buffer read is count-clamped** to 1..1000 (`Logs`, `HttpErrors`).
- **Everything shown is process-local** and every page that shows a tally says it resets
  on restart/redeploy.
- **The only localizer the section binds is `SharedResource`**, read as data by
  `/Debug/Translations`; every string on these pages is English developer copy
  (`DebugArchitectureTests` pins the localizer rule).
- **The widget gallery never fabricates a key** for the search-row cards: it resolves a real
  team / camp / rota / event id from cache-served reads, and says "none in this environment"
  when there is none — a blank card would be indistinguishable from an unbound tag helper.

## 5. Seams

- **Read-split on `IBurnSettingsService`.** Both the widget gallery and the dashboard card
  inject the full Shifts write interface to ask one read question ("what is the active
  event?"). No `IBurnSettingsServiceRead` exists yet; when Shifts carves one, both callers
  move to it. Items touching those constructors are shaped by this.
- **`/api/client-metrics`, `ClientStatsMiddleware`, the trackers and `UserAgentClassifier`
  live in Shell / Base**, not here. The feature specs in this section describe them because
  the screens are the only consumer; if a Telemetry section ever forms, the specs move with
  the code.

## 6. Deliberately not done

- **No service layer, no `IApplicationService`.** Every action is a projection of a
  singleton's snapshot into a view model; a service would be a pass-through.
- **No caching decorator.** The sources are already in-memory singletons.
- **No persistence for any tally.** Since-deploy is the accepted window for a debug aid
  (client-stats.md, http-errors.md).
- **No resx.** Admin-only pages are exempt, and `/Debug/Translations` reads the shared set
  as data, not as its own UI strings.
- **No `Humans.Debug.Contracts` project.** Nothing outside the section names a Debug type;
  the nav and the dashboard card are contributed by name through Base seams.
- **No `/Debug/Admin/*` split.** The section is admin-only end to end, so the
  `/<Section>/Admin/*` convention has nothing to disambiguate.
- **No `Humans.Web` reference and no Serilog sink of its own.** `/Debug/Logs` reads Base's
  `InMemoryLogSink`; Shell configures it.

## Load-bearing weirdness

- **`ColorPaletteController` is `[AllowAnonymous]` and uses the public `_Layout`** on
  purpose: a static design reference, reachable without a session, linked from the admin
  sidebar's Design group.
- **The widget gallery references the section assemblies whose public components it renders**
  (Camps, Users, Tickets, Shifts, Calendar, AuditLog, Teams, Events) plus contracts leaves. That
  fan-in is the page's job — it renders other sections' public view components as `<vc:>` tag
  helpers — and every one of those references is opened in `_ViewImports` so a binding walk
  sees the tags as bound.
- **`Section.Register` is empty and must stay so.** Anything Debug would register is
  something another owner should register; no test pins the absence
  ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).
- **The status-code tally and the error buffer disagree slightly by design**: Kestrel-rejected
  requests never reach the middleware, so `/Debug/HttpErrors` can run below the
  `/Debug/ClientStats` meter count. Both pages say so.
- **Aborted requests record as 499 except authenticated `/Profile/Picture` aborts** — routine
  avatar-load cancellation, not an error.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | First doctor pass — the code is close to target; the docs are not: the invariant doc knows only one of the section's controllers, the contracts README credits a nav tree the section replaced, and comments across the section name projects (`Humans.Infrastructure`, `Humans.UI`) that no longer exist | peterdrier/Humans#1598 |
