# e2e auth-state reuse + persona completeness

- **Date:** 2026-06-05
- **Status:** Approved (design), pending implementation
- **Branch:** `test/e2e-auth-state-reuse`
- **Related:** predecessor PR peterdrier#881 (name-only access via stored `UserState`); follow-up nobodies-collective/Humans#832 (authorization matrix + section-admin convention)

## Problem

The Playwright e2e suite is fragile under its own load. Every test calls `loginAsX(page)`,
which hits `/dev/login/{slug}` → triggers heavy first-touch persona seeding in
`DevPersonaSeeder` (profile, consent, teams, roles; camp+season for barrio leads) behind a
single global `SemaphoreSlim SeedLock`, against one small shared preview deploy. Run with
default multi-worker parallelism, ~120 tests stampede that lock and the deploy, so the login
helper's `waitForSelector` for the nav dropdown blows its 60s budget.

Evidence (PR #881 preview, `https://881.n.burn.camp`): full suite = 66 passed / 32 flaky /
17 failed; **every** failure was `auth.ts:27` (the login nav-wait), none were access
assertions. The same `nav-visibility` tests passed 13/13 in 9.6s when run alone with 1 worker.
So the assertions are sound; the per-test login-under-concurrency is the fragility.

A second gap surfaced: the suite only has helpers for 14 personas, but the app defines 15
`RoleNames` + a camp-lead class. Missing e2e coverage: `EventsAdmin`, `StoreAdmin`,
`CantinaAdmin` (all in `AnyAdminRole` → should see Admin nav), `EETeamAdmin` (role but NOT in
`AnyAdminRole` → should NOT see Admin nav), and `barrio-N-lead` (camp lead — access via
`camp.IsLead`, no governance role). For a PR that rewrote `AnyAdminRole`, those rows are
exactly the access surface left untested.

## Goals

- Eliminate the per-test login herd so failures reflect real behavior, not login latency.
- Keep the 19 spec files unchanged (helper signatures stay identical).
- Retain coverage of the live login + seeding path (exercise it once, explicitly).
- Close the persona-coverage gap in the `nav-visibility` access matrix.

## Non-goals

- The role × route authorization matrix and section-admin route convention → tracked in #832.
- Resource-scoped authz tests (camp-lead-of-*this*-camp) → #832.
- Changing the e2e target away from the shared preview deploy.

## Design

### A. Auth-state reuse (robustness)

**Single source of truth.** `helpers/auth.ts` exports a `PERSONAS` array (slug per persona).
The named helpers (`loginAsVolunteer`, …) each reference a slug from it. Adding a persona =
one array entry + one named export.

**New setup project — `tests/e2e/auth.setup.ts`.** For each persona slug, once:
1. `seedCookieConsent`, then `goto('/dev/login/{slug}')` (real login → seeds + authenticates),
   `waitForSelector(NAV_SELECTOR)`.
2. `context.storageState({ path: 'playwright/.auth/{slug}.json' })`.

Runs serially, before the test projects. This is the **sole, explicit coverage of the live
login + seeding path** — if seeding or the onboarding redirect breaks, setup fails fast and
loudly, before any feature test.

**`helpers/auth.ts` change.** `loginAsX(page)` keeps its exact signature but no longer hits
`/dev/login`. It seeds the consent cookie and applies the persona's saved cookies via
`page.context().addCookies(...)` read from `playwright/.auth/{slug}.json`. Instant, no network
login, no nav-wait, no `SeedLock` contention. All ~175 call sites are untouched.

**`playwright.config.ts` change.** Add a `setup` project (`testMatch: /auth\.setup\.ts/`);
make `chromium` depend on it (`dependencies: ['setup']`); add a moderate `workers` cap (gentle
on the single shared deploy); keep `retries: 2` for residual network flake.

**`.gitignore`.** Add `tests/e2e/playwright/.auth/` — generated live session cookies; never
committed.

**Technical risk (verify first).** Confirm that re-adding a saved ASP.NET Identity cookie via
`addCookies` re-authenticates a request. Prove with one persona (apply saved cookie → GET a
protected page → expect 200) before converting all helpers. Fallback if it fails:
`test.use({ storageState })` at the spec level (more idiomatic, but touches spec files).

### B. Persona completeness (`nav-visibility`)

Add 5 helpers + `PERSONAS` entries and 5 matrix rows in `nav-visibility.spec.ts` with the
correct expected nav (per `AnyAdminRole` in `AuthorizationPolicyExtensions.cs`):

| Persona | slug | Expected top-nav |
|---|---|---|
| `eventsAdmin` | `events-admin` | Volunteer + Admin |
| `storeAdmin` | `store-admin` | Volunteer + Admin |
| `cantinaAdmin` | `cantina-admin` | Volunteer + Admin |
| `eeTeamAdmin` | `e-e-team-admin` | Volunteer only (role-holder, not in `AnyAdminRole`) |
| `barrioLead` | `barrio-1-lead` | Volunteer only (camp lead, no governance role) |

All five already exist as dev-login personas (the controller auto-generates one per
`RoleNames` constant; barrio leads are hardcoded), so they seed today with no app change.

## Files touched

- `tests/e2e/helpers/auth.ts` — `PERSONAS` source of truth; cookie-reuse login; 5 new helpers.
- `tests/e2e/auth.setup.ts` — **new** setup project.
- `tests/e2e/playwright.config.ts` — setup project, `dependencies`, `workers`, retries.
- `tests/e2e/tests/nav-visibility.spec.ts` — 5 new matrix rows.
- `.gitignore` — ignore `tests/e2e/playwright/.auth/`.

No `src/` changes. The 18 other spec files are unchanged.

## Verification

- Run `nav-visibility` (now 18 rows) against the QA/preview deploy → all green.
- Run the full suite multi-worker → no `auth.ts` login-wait timeouts; failures (if any) are
  real assertions, not login latency.
- Confirm setup fails loudly if a persona can't reach `Active`/its role (negative check).
