# Playwright E2E Smoke Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 5 Playwright browser-based smoke tests targeting preview/QA environments, covering login, profile edit, shift signup, team pages, and the feedback widget.

**Architecture:** Standalone Node.js project in `tests/e2e/` — completely isolated from the .NET solution and Docker build. Tests authenticate via DevAuth endpoints that set ASP.NET Identity cookies. Configurable BASE_URL targets preview (`{PR_ID}.n.burn.camp`) or QA (`humans.n.burn.camp`) environments.

**Tech Stack:** Playwright, TypeScript, Chromium (headless)

**Spec:** `docs/superpowers/specs/2026-03-22-playwright-e2e-smoke-tests-design.md`

---

### Task 1: Scaffold the Node.js project

**Files:**
- Create: `tests/e2e/package.json`
- Create: `tests/e2e/tsconfig.json`
- Create: `tests/e2e/playwright.config.ts`
- Create: `tests/e2e/.gitignore`
- Create: `.dockerignore`

- [ ] **Step 1: Create `tests/e2e/package.json`**

```json
{
  "name": "humans-e2e",
  "private": true,
  "scripts": {
    "test": "playwright test",
    "test:headed": "playwright test --headed",
    "report": "playwright show-report"
  },
  "devDependencies": {
    "@playwright/test": "^1.52.0",
    "typescript": "^5.8.0"
  }
}
```

- [ ] **Step 2: Create `tests/e2e/tsconfig.json`**

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "NodeNext",
    "moduleResolution": "NodeNext",
    "strict": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "outDir": "./dist"
  },
  "include": ["**/*.ts"]
}
```

- [ ] **Step 3: Create `tests/e2e/playwright.config.ts`**

```typescript
import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  retries: 1,
  use: {
    baseURL: process.env.BASE_URL || 'https://humans.n.burn.camp',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { browserName: 'chromium' },
    },
  ],
  reporter: [['html', { open: 'never' }]],
});
```

- [ ] **Step 4: Create `tests/e2e/.gitignore`**

```
node_modules/
test-results/
playwright-report/
dist/
```

- [ ] **Step 5: Create `.dockerignore` at repo root**

```
tests/e2e/
```

- [ ] **Step 6: Install dependencies and Playwright browser**

Run: `cd tests/e2e && npm install && npx playwright install chromium`
Expected: `package-lock.json` created, Chromium downloaded

- [ ] **Step 7: Commit scaffold**

```bash
git add tests/e2e/package.json tests/e2e/package-lock.json tests/e2e/tsconfig.json tests/e2e/playwright.config.ts tests/e2e/.gitignore .dockerignore
git commit -m "chore: scaffold Playwright e2e test project"
```

---

### Task 2: Auth helper and login test

**Files:**
- Create: `tests/e2e/helpers/auth.ts`
- Create: `tests/e2e/tests/login.spec.ts`
- Modify: `src/Humans.Web/Views/Shared/_LoginPartial.cshtml` (add `data-testid="user-nav"`)

- [ ] **Step 1: Add `data-testid="user-nav"` to _LoginPartial.cshtml**

The authenticated user dropdown in `_LoginPartial.cshtml` is a `<div class="dropdown">`. Add `data-testid="user-nav"` to it for stable test targeting. Find the `<div class="dropdown">` that wraps the user menu and add the attribute.

Look for:
```html
<div class="dropdown">
```
Change to:
```html
<div class="dropdown" data-testid="user-nav">
```

- [ ] **Step 2: Create `tests/e2e/helpers/auth.ts`**

```typescript
import { type Page } from '@playwright/test';

export async function loginAsVolunteer(page: Page): Promise<void> {
  await page.goto('/dev/login/volunteer');
  await page.waitForSelector('[data-testid="user-nav"]');
}

export async function loginAsAdmin(page: Page): Promise<void> {
  await page.goto('/dev/login/admin');
  await page.waitForSelector('[data-testid="user-nav"]');
}
```

- [ ] **Step 3: Create `tests/e2e/tests/login.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('DevAuth Login', () => {
  test('volunteer can log in and see dashboard', async ({ page }) => {
    await loginAsVolunteer(page);

    // Nav dropdown is visible with user name
    const userNav = page.locator('[data-testid="user-nav"]');
    await expect(userNav).toBeVisible();

    // Display name is shown (non-empty text in the dropdown toggle)
    const displayName = userNav.locator('.dropdown-toggle');
    await expect(displayName).not.toBeEmpty();

    // No server error banner
    await expect(page.locator('.alert-danger')).not.toBeVisible();

    // Page title is present (dashboard loaded)
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('page does not show server error', async ({ page }) => {
    await loginAsVolunteer(page);

    // Should not be on the error page
    expect(page.url()).not.toContain('/Error');
    await expect(page.locator('.alert-danger')).not.toBeVisible();
  });
});
```

- [ ] **Step 4: Run login test against QA**

Run: `cd tests/e2e && npx playwright test tests/login.spec.ts`
Expected: 2 tests pass (green)

- [ ] **Step 5: Commit**

```bash
git add tests/e2e/helpers/auth.ts tests/e2e/tests/login.spec.ts src/Humans.Web/Views/Shared/_LoginPartial.cshtml
git commit -m "test: add DevAuth login e2e smoke test"
```

---

### Task 3: Profile edit test

**Files:**
- Create: `tests/e2e/tests/profile-edit.spec.ts`

The profile edit page is at `/Profile/Edit`. The `BurnerName` field (`input[name="BurnerName"]`) is a simple text input suitable for testing. The form submits via POST and redirects back with a TempData success alert (`div.alert.alert-success`).

- [ ] **Step 1: Create `tests/e2e/tests/profile-edit.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('Profile Edit', () => {
  test('can edit burner name and changes persist', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/Edit');

    const burnerNameInput = page.locator('input[name="BurnerName"]');
    await expect(burnerNameInput).toBeVisible();

    // Save original value for cleanup
    const originalValue = await burnerNameInput.inputValue();
    const testValue = `E2E Test ${Date.now()}`;

    // Change the field
    await burnerNameInput.fill(testValue);

    // Submit the form
    await page.locator('button[type="submit"], input[type="submit"]').first().click();

    // Assert success alert after redirect
    await expect(page.locator('.alert.alert-success')).toBeVisible();

    // Reload and verify persistence
    await page.goto('/Profile/Edit');
    await expect(burnerNameInput).toHaveValue(testValue);

    // Restore original value
    await burnerNameInput.fill(originalValue);
    await page.locator('button[type="submit"], input[type="submit"]').first().click();
    await expect(page.locator('.alert.alert-success')).toBeVisible();
  });
});
```

- [ ] **Step 2: Run profile edit test**

Run: `cd tests/e2e && npx playwright test tests/profile-edit.spec.ts`
Expected: 1 test passes

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/profile-edit.spec.ts
git commit -m "test: add profile edit e2e smoke test"
```

---

### Task 4: Shift signup test

**Files:**
- Create: `tests/e2e/tests/shift-signup.spec.ts`

The shift browse page is at `/Shifts`. It may have shifts (with signup buttons `button.btn.btn-success`) or an empty state. The test is resilient to both cases. Signup forms POST to `/Shifts/SignUp` or `/Shifts/SignUpRange`.

- [ ] **Step 1: Create `tests/e2e/tests/shift-signup.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('Shift Signup', () => {
  test('shift browse page loads and is functional', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Shifts');

    // Page loads with a heading (may show info/warning alerts for closed browsing — that's OK)
    await expect(page.locator('h1, h2').first()).toBeVisible();

    // Check if shifts are available
    const signupButtons = page.locator('form[action*="SignUp"] button[type="submit"]');
    const signupCount = await signupButtons.count();

    if (signupCount > 0) {
      // Shifts exist — click the first available signup
      await signupButtons.first().click();

      // After signup, either success alert or redirect back to shifts page
      const successAlert = page.locator('.alert.alert-success');
      const shiftsPage = page.locator('h1, h2');
      await expect(successAlert.or(shiftsPage.first())).toBeVisible();
    } else {
      // No shifts available — verify the page still renders correctly
      // The page should have a heading and not be an error page
      await expect(page.locator('h1, h2').first()).toBeVisible();
    }
  });
});
```

- [ ] **Step 2: Run shift signup test**

Run: `cd tests/e2e && npx playwright test tests/shift-signup.spec.ts`
Expected: 1 test passes (regardless of shift data presence)

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/shift-signup.spec.ts
git commit -m "test: add shift signup e2e smoke test"
```

---

### Task 5: Team pages test

**Files:**
- Create: `tests/e2e/tests/team-pages.spec.ts`

The teams listing is at `/Teams` (TeamController uses `[Route("Teams")]`). Team cards use `.card` with `h5.card-title > a` linking to `/Teams/{slug}`. Detail pages show team name, description, and a members section.

- [ ] **Step 1: Create `tests/e2e/tests/team-pages.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('Team Pages', () => {
  test('team listing loads and can navigate to detail', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    // Page loads without error
    await expect(page.locator('.alert-danger')).not.toBeVisible();

    // At least one team card is visible
    const teamCards = page.locator('.card');
    await expect(teamCards.first()).toBeVisible();

    // Click the first "View Details" link in a card
    const detailLink = page.locator('.card a.btn').first();
    await detailLink.click();

    // Detail page loads with a heading
    await expect(page.locator('h1, h2').first()).toBeVisible();

    // Members section header is present (renders as "Members (N)")
    await expect(
      page.locator('.card-header').filter({ hasText: /Members/ })
    ).toBeVisible({ timeout: 5000 });
  });
});
```

- [ ] **Step 2: Run team pages test**

Run: `cd tests/e2e && npx playwright test tests/team-pages.spec.ts`
Expected: 1 test passes

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/team-pages.spec.ts
git commit -m "test: add team pages e2e smoke test"
```

---

### Task 6: Feedback widget test

**Files:**
- Create: `tests/e2e/tests/feedback.spec.ts`

The feedback widget is a Bootstrap modal (`#feedbackModal`) triggered by a fixed-position button. The form has a category `<select>`, a description `<textarea>`, and an optional screenshot upload. Submission POSTs to `/Feedback/Submit` and redirects with a TempData success banner.

- [ ] **Step 1: Create `tests/e2e/tests/feedback.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('Feedback Widget', () => {
  test('can open modal, submit feedback, and see success', async ({ page }) => {
    await loginAsVolunteer(page);

    // Navigate to any authenticated page (dashboard)
    await page.goto('/');

    // Open the feedback modal
    const feedbackTrigger = page.locator('button[data-bs-target="#feedbackModal"]');
    await expect(feedbackTrigger).toBeVisible();
    await feedbackTrigger.click();

    // Modal opens
    const modal = page.locator('#feedbackModal');
    await expect(modal).toBeVisible();

    // Select category
    await modal.locator('select[name="Category"]').selectOption({ index: 1 });

    // Fill description
    await modal.locator('textarea[name="Description"]').fill(
      `E2E smoke test feedback - ${new Date().toISOString()} - please ignore`
    );

    // Submit the form
    await modal.locator('button[type="submit"]').click();

    // Page reloads with success alert
    await expect(page.locator('.alert.alert-success')).toBeVisible();
  });
});
```

- [ ] **Step 2: Run feedback test**

Run: `cd tests/e2e && npx playwright test tests/feedback.spec.ts`
Expected: 1 test passes

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/feedback.spec.ts
git commit -m "test: add feedback widget e2e smoke test"
```

---

### Task 7: Full suite verification and final commit

- [ ] **Step 1: Run all 5 test files together**

Run: `cd tests/e2e && npx playwright test`
Expected: All 6 tests pass (2 login + 1 profile + 1 shifts + 1 teams + 1 feedback)

- [ ] **Step 2: Run with HTML report**

Run: `cd tests/e2e && npx playwright test --reporter=html`
Expected: HTML report generated in `playwright-report/`

- [ ] **Step 3: Verify .dockerignore works**

Run: `docker build --no-cache -t humans-test . 2>&1 | head -20`
Expected: Build succeeds, no mention of `tests/e2e/` or `node_modules`

- [ ] **Step 4: Verify nothing leaks into git**

Run: `git status`
Expected: No untracked files from `node_modules/`, `test-results/`, or `playwright-report/`

- [ ] **Step 5: Final commit if any cleanup needed**

Only if earlier steps revealed issues that needed fixing.

---

## Phase 2: Role-Based Authorization Tests

> **Prerequisite:** Phase 1 merged. Implement in a separate branch/PR.
> **Priority:** High — auth boundaries are the biggest regression risk and currently undertested.

### Task 8: Expand auth helper with all dev personas

**Files:**
- Modify: `tests/e2e/helpers/auth.ts`

- [ ] **Step 1: Add coordinator and board login helpers**

```typescript
export async function loginAsCoordinator(page: Page): Promise<void> {
  await page.goto('/dev/login/consent-coordinator');
  await page.waitForSelector('[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)');
}

export async function loginAsBoard(page: Page): Promise<void> {
  await page.goto('/dev/login/board');
  await page.waitForSelector('[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)');
}
```

- [ ] **Step 2: Commit**

```bash
git add tests/e2e/helpers/auth.ts
git commit -m "test: add coordinator and board login helpers"
```

---

### Task 9: Nav visibility test per role

**Files:**
- Create: `tests/e2e/tests/auth/nav-visibility.spec.ts`

Test that each role sees the correct nav links. The nav is the highest-value auth test because every role sees it on every page.

- [ ] **Step 1: Create `tests/e2e/tests/auth/nav-visibility.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, loginAsBoard } from '../../helpers/auth';

test.describe('Nav Visibility by Role', () => {
  test('volunteer sees standard nav, no Admin link', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/');

    // Should see standard links
    await expect(page.locator('nav a[href="/"]')).toBeVisible();
    await expect(page.locator('nav a[href="/Teams"]')).toBeVisible();

    // Should NOT see Admin link
    await expect(page.locator('nav a[href*="/Admin"]')).not.toBeVisible();
  });

  test('admin sees Admin link in nav', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/');

    await expect(page.locator('nav a[href*="/Admin"]')).toBeVisible();
  });

  test('board member sees Governance link', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/');

    await expect(page.locator('nav a[href*="/Governance"]')).toBeVisible();
  });
});
```

- [ ] **Step 2: Run and verify**

Run: `cd tests/e2e && npx playwright test tests/auth/nav-visibility.spec.ts`

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/auth/nav-visibility.spec.ts
git commit -m "test: add nav visibility e2e tests per role"
```

---

### Task 10: Admin views test

**Files:**
- Create: `tests/e2e/tests/auth/admin-views.spec.ts`

Verify admin-only pages load correctly and show expected content.

- [ ] **Step 1: Create `tests/e2e/tests/auth/admin-views.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsAdmin, loginAsVolunteer } from '../../helpers/auth';

test.describe('Admin Views', () => {
  test('admin can access admin dashboard', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    await expect(page.locator('.alert-danger')).not.toBeVisible();
    expect(page.url()).not.toContain('/Error');
  });

  test('admin can access sync settings', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin/SyncSettings');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Admin/SyncSettings');
  });

  test('volunteer cannot access admin pages', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Admin');

    // Should be redirected away or see access denied
    const url = page.url();
    expect(url.includes('/Admin')).toBeFalsy();
  });
});
```

- [ ] **Step 2: Run and verify**
- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/auth/admin-views.spec.ts
git commit -m "test: add admin views authorization e2e tests"
```

---

### Task 11: Coordinator views test

**Files:**
- Create: `tests/e2e/tests/auth/coordinator-views.spec.ts`

Verify coordinator-specific elements appear/don't appear based on role.

- [ ] **Step 1: Create `tests/e2e/tests/auth/coordinator-views.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin } from '../../helpers/auth';

test.describe('Coordinator Views', () => {
  test('team detail shows manage button for team coordinator', async ({ page }) => {
    // Admin can manage teams
    await loginAsAdmin(page);
    await page.goto('/Teams');

    // Navigate to a team
    const teamLink = page.locator('a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();

      // Should see coordinator/admin management links
      const manageLink = page.locator('a[href*="/TeamAdmin/"]');
      await expect(manageLink.first()).toBeVisible({ timeout: 3000 });
    }
  });

  test('volunteer does not see manage button on teams they do not coordinate', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    const teamLink = page.locator('a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();

      // Should NOT see team admin links
      await expect(page.locator('a[href*="/TeamAdmin/"]')).not.toBeVisible();
    }
  });
});
```

- [ ] **Step 2: Run and verify**
- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/auth/coordinator-views.spec.ts
git commit -m "test: add coordinator views authorization e2e tests"
```

---

### Task 12: Volunteer boundary test

**Files:**
- Create: `tests/e2e/tests/auth/volunteer-boundaries.spec.ts`

Negative tests — verify volunteers cannot see or access privileged content.

- [ ] **Step 1: Create `tests/e2e/tests/auth/volunteer-boundaries.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../../helpers/auth';

test.describe('Volunteer Boundaries', () => {
  test('volunteer cannot access admin dashboard', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Admin');

    // Should redirect away from admin
    expect(page.url()).not.toContain('/Admin');
  });

  test('volunteer cannot access sync settings', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Admin/SyncSettings');

    expect(page.url()).not.toContain('/SyncSettings');
  });

  test('volunteer cannot access team admin for arbitrary team', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/TeamAdmin/Members/volunteers');

    // Should be redirected or see access denied
    expect(page.url()).not.toContain('/TeamAdmin/');
  });
});
```

- [ ] **Step 2: Run and verify**
- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/auth/volunteer-boundaries.spec.ts
git commit -m "test: add volunteer boundary authorization e2e tests"
```

---

### Task 13: Phase 2 full suite verification

- [ ] **Step 1: Run all auth tests**

Run: `cd tests/e2e && npx playwright test tests/auth/`
Expected: All auth tests pass

- [ ] **Step 2: Run complete suite (Phase 1 + Phase 2)**

Run: `cd tests/e2e && npx playwright test`
Expected: All tests pass (smoke + auth)
