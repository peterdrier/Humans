# e2e Auth-State Reuse + Persona Completeness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kill the e2e per-test login herd by reusing a once-captured `storageState` cookie per persona, and close the persona-coverage gap in `nav-visibility`.

**Architecture:** A Playwright `setup` project logs in + seeds each persona once (serially, in one looping test) and saves `storageState` to `playwright/.auth/{slug}.json`. `loginAsX(page)` helpers keep their signatures but, instead of hitting `/dev/login`, attach the saved cookies via `addCookies`. The chromium project depends on `setup` and caps `workers`.

**Tech Stack:** Playwright `@playwright/test` ^1.60, TypeScript (CommonJS — `__dirname` available), targets the shared deploy (`BASE_URL`, default `https://humans.n.burn.camp`).

**Spec:** `docs/superpowers/specs/2026-06-05-e2e-auth-state-reuse-design.md`

**Working dir:** `H:/source/humans/.worktrees/e2e-auth-reuse` (branch `test/e2e-auth-state-reuse`). All paths below are under `tests/e2e/`.

---

## File Structure

- `tests/e2e/helpers/auth.ts` — **modify.** Add `PERSONAS` (source of truth), `authFile()`, `liveLoginAndSave()` (live login + capture, used only by setup), rewrite `loginAs()` to reuse saved cookies, add 5 new persona helpers. Keep `getAntiForgeryToken`/`postWithCsrf`/`expectBlocked` unchanged.
- `tests/e2e/auth.setup.ts` — **create.** Setup project: loop `PERSONAS`, capture state serially.
- `tests/e2e/playwright.config.ts` — **modify.** Add `setup` project (own `testDir: '.'`), `dependencies: ['setup']` on chromium, `workers` cap.
- `tests/e2e/tests/nav-visibility.spec.ts` — **modify.** Add 5 matrix rows.
- `tests/e2e/.gitignore` — **modify.** Ignore `playwright/.auth/`.

No `src/` changes. 18 other spec files unchanged.

---

## Task 1: SPIKE — prove `addCookies` re-authenticates (de-risk before rollout)

The load-bearing assumption: re-adding a saved Identity cookie via `addCookies` to a fresh context yields an authenticated request. If false, the fallback is `test.use({ storageState })`, which forces splitting multi-persona spec files — a much bigger change. Prove it on one persona first.

**Files:**
- Modify: `tests/e2e/helpers/auth.ts`
- Create: `tests/e2e/auth.setup.ts`
- Modify: `tests/e2e/playwright.config.ts`

- [ ] **Step 1: Add persona list + path helper + capture/reuse functions to `auth.ts`**

Add near the top of `tests/e2e/helpers/auth.ts` (after the existing `NAV_SELECTOR` const), and add `import fs from 'fs'; import path from 'path';` to the import block, plus `type BrowserContext` to the `@playwright/test` import:

```ts
// Single source of truth: every reusable persona slug (= /dev/login/{slug} segment).
export const PERSONAS = [
  'volunteer', 'admin', 'board', 'consent-coordinator', 'teams-admin',
  'camp-admin', 'ticket-admin', 'no-info-admin', 'volunteer-coordinator',
  'human-admin', 'finance-admin', 'feedback-admin', 'city-planning',
  'coordinator', 'events-admin', 'store-admin', 'cantina-admin',
  'e-e-team-admin', 'barrio-1-lead',
] as const;

// Resolve relative to THIS file (not cwd): tests run via --config from a different cwd.
const AUTH_DIR = path.join(__dirname, '..', 'playwright', '.auth');
export const authFile = (slug: string): string => path.join(AUTH_DIR, `${slug}.json`);

function baseUrl(): string {
  return process.env.BASE_URL || 'https://humans.n.burn.camp';
}

// Live /dev/login + seed, then capture storageState. Used ONLY by auth.setup.ts.
export async function liveLoginAndSave(context: BrowserContext, slug: string): Promise<void> {
  await context.addCookies([
    { name: 'cookieConsent', value: 'accepted', url: baseUrl(), sameSite: 'Lax' },
  ]);
  const page = await context.newPage();
  await page.goto(`/dev/login/${slug}`, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector(NAV_SELECTOR, { timeout: 60_000 });
  await context.storageState({ path: authFile(slug) });
  await page.close();
}
```

Then replace the existing `loginAs` function body (currently the `/dev/login` + `waitForSelector` version) with the reuse version:

```ts
// Reuse: attach the persona's once-captured cookies to this test's context.
// No /dev/login, no seeding, no SeedLock contention.
async function loginAs(page: Page, slug: string): Promise<void> {
  const state = JSON.parse(fs.readFileSync(authFile(slug), 'utf-8'));
  await page.context().addCookies(state.cookies);
}
```

Leave all existing `export const loginAsX = (page) => loginAs(page, '...')` lines and `seedCookieConsent`/`getAntiForgeryToken`/`postWithCsrf`/`expectBlocked` as-is.

- [ ] **Step 2: Create the setup project `tests/e2e/auth.setup.ts`**

```ts
import { test as setup, expect } from '@playwright/test';
import { PERSONAS, liveLoginAndSave } from './helpers/auth';

// One serial test: seed + log in every persona once, capturing storageState.
// Single test = single worker = no login herd during seeding.
setup('authenticate all personas', async ({ browser }) => {
  setup.setTimeout(PERSONAS.length * 60_000);
  const failures: string[] = [];
  for (const slug of PERSONAS) {
    const context = await browser.newContext();
    try {
      await liveLoginAndSave(context, slug);
    } catch (e) {
      failures.push(`${slug}: ${(e as Error).message.split('\n')[0]}`);
    } finally {
      await context.close();
    }
  }
  expect(failures, `personas failed to seed/login:\n${failures.join('\n')}`).toEqual([]);
});
```

- [ ] **Step 3: Wire the setup project in `tests/e2e/playwright.config.ts`**

Replace the `projects` array with:

```ts
  projects: [
    { name: 'setup', testDir: '.', testMatch: /auth\.setup\.ts/ },
    {
      name: 'chromium',
      use: { browserName: 'chromium' },
      dependencies: ['setup'],
    },
  ],
```

(`testDir: '.'` lets the setup project find `auth.setup.ts` at the e2e root; the chromium project keeps the top-level `testDir: './tests'` and the default `*.spec.ts` match, so it ignores the setup file.)

- [ ] **Step 4: Run the spike — one persona, reuse path**

Run (from anywhere; absolute config path):

```bash
BASE_URL=https://humans.n.burn.camp \
H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/node_modules/.bin/playwright test nav-visibility \
  --config H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/playwright.config.ts \
  --grep "volunteer: sees correct top-nav" --reporter=line
```

Expected: setup runs (captures all personas once), then the `volunteer` row passes via cookie reuse. **Decision gate:** if it passes → `addCookies` re-auth works, continue to Task 2. If the volunteer row fails at a nav/`expectBlocked` assertion (not a setup failure) → the mechanism does NOT re-auth; STOP and switch to the `test.use({ storageState })` fallback (re-plan with per-persona spec splits).

- [ ] **Step 5: Commit**

```bash
git -C H:/source/humans/.worktrees/e2e-auth-reuse add tests/e2e/helpers/auth.ts tests/e2e/auth.setup.ts tests/e2e/playwright.config.ts
git -C H:/source/humans/.worktrees/e2e-auth-reuse commit -m "test(e2e): storageState reuse mechanism + setup project (spike)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Verify all 14 existing personas pass via reuse

The reuse `loginAs` + the full `PERSONAS` list already cover the 14 existing helpers (their `loginAsX` exports are unchanged and now route through reuse). Confirm the existing matrix is green before adding new rows.

**Files:** none (verification + commit only).

- [ ] **Step 1: Run the full existing `nav-visibility` (13 rows) via reuse**

```bash
BASE_URL=https://humans.n.burn.camp \
H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/node_modules/.bin/playwright test nav-visibility \
  --config H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/playwright.config.ts --reporter=line
```

Expected: setup + 13 passed, 0 failed. (Pre-change baseline was 13/13 in 9.6s with live login; reuse should match and be faster.)

- [ ] **Step 2: Spot-check a boundary spec actually executes its assertion (not just login)**

```bash
BASE_URL=https://humans.n.burn.camp \
H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/node_modules/.bin/playwright test tickets \
  --config H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/playwright.config.ts --reporter=line
```

Expected: 3 passed — including `boundary: volunteer cannot access /Tickets` reaching `expectBlocked` (the assertion that previously never ran because login timed out).

No commit (no file changes).

---

## Task 3: Add the 5 missing personas + nav-visibility rows

**Files:**
- Modify: `tests/e2e/helpers/auth.ts`
- Modify: `tests/e2e/tests/nav-visibility.spec.ts`

- [ ] **Step 1: Add 5 persona helpers to `auth.ts`**

After the existing `loginAsCityPlanning`/`loginAsCoordinator` exports, add:

```ts
export const loginAsEventsAdmin = (page: Page) => loginAs(page, 'events-admin');
export const loginAsStoreAdmin = (page: Page) => loginAs(page, 'store-admin');
export const loginAsCantinaAdmin = (page: Page) => loginAs(page, 'cantina-admin');
export const loginAsEETeamAdmin = (page: Page) => loginAs(page, 'e-e-team-admin');
export const loginAsBarrioLead = (page: Page) => loginAs(page, 'barrio-1-lead');
```

(The slugs already exist as dev-login personas — `RoleNames` auto-generate one each; `barrio-1-lead` is hardcoded. `e-e-team-admin` is the kebab of `EETeamAdmin`. All are already in `PERSONAS`, so setup already captures them.)

- [ ] **Step 2: Add the 5 rows to `nav-visibility.spec.ts`**

Add these imports to the existing `import { ... } from '../helpers/auth';` block: `loginAsEventsAdmin, loginAsStoreAdmin, loginAsCantinaAdmin, loginAsEETeamAdmin, loginAsBarrioLead`.

Append to the `roles: RoleTest[]` array (before the closing `]`):

```ts
  { name: 'eventsAdmin', login: loginAsEventsAdmin, visible: ['volunteer', 'admin'] },
  { name: 'storeAdmin', login: loginAsStoreAdmin, visible: ['volunteer', 'admin'] },
  { name: 'cantinaAdmin', login: loginAsCantinaAdmin, visible: ['volunteer', 'admin'] },
  // EETeamAdmin holds a role (passes AppAccess via HasAnyRole) but is NOT in the
  // AnyAdminRole composite, so it does NOT see the Admin top-nav.
  { name: 'eeTeamAdmin', login: loginAsEETeamAdmin, visible: ['volunteer'] },
  // Camp lead: no governance role; access via UserState.Active. Sees Volunteer, not Admin.
  { name: 'barrioLead', login: loginAsBarrioLead, visible: ['volunteer'] },
```

- [ ] **Step 3: Run the expanded matrix (18 rows)**

```bash
BASE_URL=https://humans.n.burn.camp \
H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/node_modules/.bin/playwright test nav-visibility \
  --config H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/playwright.config.ts --reporter=line
```

Expected: 18 passed, 0 failed. Specifically `eeTeamAdmin` and `barrioLead` see Volunteer but NOT Admin; `eventsAdmin`/`storeAdmin`/`cantinaAdmin` see both.

- [ ] **Step 4: Commit**

```bash
git -C H:/source/humans/.worktrees/e2e-auth-reuse add tests/e2e/helpers/auth.ts tests/e2e/tests/nav-visibility.spec.ts
git -C H:/source/humans/.worktrees/e2e-auth-reuse commit -m "test(e2e): cover EventsAdmin/StoreAdmin/CantinaAdmin/EETeamAdmin + camp lead in nav matrix

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Cap workers, ignore generated state, full-suite verification

**Files:**
- Modify: `tests/e2e/playwright.config.ts`
- Modify: `tests/e2e/.gitignore`

- [ ] **Step 1: Add a workers cap in `playwright.config.ts`**

Add `workers: 4,` to the top-level `defineConfig({...})` object (next to `retries: 2`). Feature tests no longer log in (cookie reuse), so 4 concurrent workers is gentle on the single shared deploy while staying fast.

- [ ] **Step 2: Ignore the generated auth state**

Append to `tests/e2e/.gitignore`:

```
playwright/.auth/
```

- [ ] **Step 3: Verify `.auth/` is not tracked**

Run:

```bash
git -C H:/source/humans/.worktrees/e2e-auth-reuse status --porcelain tests/e2e/playwright/.auth
```

Expected: empty output (ignored, untracked).

- [ ] **Step 4: Full-suite run — the real test of the fix**

```bash
BASE_URL=https://humans.n.burn.camp \
H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/node_modules/.bin/playwright test \
  --config H:/source/humans/.worktrees/e2e-auth-reuse/tests/e2e/playwright.config.ts --reporter=line \
  2>&1 | tee C:/Users/PeterDrier/AppData/Local/Temp/e2e-reuse-full.log | tail -8
```

Expected: dramatically fewer/zero failures vs the pre-change baseline (66 pass / 32 flaky / 17 fail). Critically: **zero `auth.ts` login-wait timeouts.** Any remaining failure must be a real assertion (investigate per systematic-debugging) — confirm by grepping the log:

```bash
grep -c "waiting for locator('\[data-testid=\"user-nav\"\]" C:/Users/PeterDrier/AppData/Local/Temp/e2e-reuse-full.log
```

Expected: `0`.

- [ ] **Step 5: Commit**

```bash
git -C H:/source/humans/.worktrees/e2e-auth-reuse add tests/e2e/playwright.config.ts tests/e2e/.gitignore
git -C H:/source/humans/.worktrees/e2e-auth-reuse commit -m "test(e2e): cap workers at 4, gitignore generated .auth state

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Push and open PR

- [ ] **Step 1: Push**

```bash
git -C H:/source/humans/.worktrees/e2e-auth-reuse push
```

- [ ] **Step 2: Open PR to `origin/main`**

```bash
gh pr create -R peterdrier/Humans --base main --head test/e2e-auth-state-reuse \
  --title "test(e2e): reuse persona auth state; cover missing role personas" \
  --body "See docs/superpowers/specs/2026-06-05-e2e-auth-state-reuse-design.md. Removes the per-test /dev/login herd (capture storageState once in a setup project, reuse the cookie in loginAsX), and adds nav-visibility rows for EventsAdmin/StoreAdmin/CantinaAdmin/EETeamAdmin + camp lead. Test-only; no src changes. Follow-up: nobodies-collective/Humans#832."
```

---

## Self-Review

- **Spec coverage:** A (setup project + reuse + config + .gitignore) → Tasks 1,4. Specs-unchanged invariant → only `nav-visibility` edited (Task 3), helper signatures preserved (Task 1 keeps `loginAsX` exports). Live-login coverage retained → setup project (Task 1). b (5 personas + rows) → Task 3. Risk-verify-first → Task 1 decision gate. All covered.
- **Placeholder scan:** none — every code/command step is concrete.
- **Type consistency:** `PERSONAS`, `authFile`, `liveLoginAndSave`, `loginAs`, the 5 `loginAsX` names, and the `RoleTest` row shape (`name`/`login`/`visible`) match the existing `nav-visibility.spec.ts` interface and across tasks.
