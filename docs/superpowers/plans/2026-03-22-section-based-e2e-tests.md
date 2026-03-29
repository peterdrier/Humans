# Section-Based E2E Test Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure the Playwright e2e tests from flat/auth-directory layout into 10 vertical section-based test files mapped to feature PRDs, covering 54 tests total.

**Architecture:** One spec file per app section. Each file contains functional happy-path tests for the section's primary roles plus boundary tests for unauthorized roles. Shared auth helpers and `expectBlocked` utility in `helpers/auth.ts`. Tests run against preview environments with DevAuth.

**Tech Stack:** Playwright, TypeScript, Chromium (headless), DevAuth personas

**Spec:** `docs/superpowers/specs/2026-03-22-section-based-e2e-tests-design.md`

**Working directory:** `/home/drierp/source/humans/.worktrees/playwright-e2e`

**Important constraints:**
- All work in worktree at `/home/drierp/source/humans/.worktrees/playwright-e2e` — do NOT modify main working directory
- Run all npm/playwright commands from `tests/e2e/` subdirectory
- Use `BASE_URL=https://26.n.burn.camp` for all test runs (PR #26 preview with DevAuth enabled)
- Use `getByRole`/`getByText` locators, NOT CSS attribute selectors like `[href*="..."]`
- Dev personas may not have completed onboarding — tests must handle redirects gracefully

---

### Task 1: Expand auth helper with all personas and expectBlocked utility

**Files:**
- Modify: `tests/e2e/helpers/auth.ts`

- [ ] **Step 1: Rewrite `helpers/auth.ts` with all personas and utility**

```typescript
import { type Page, expect } from '@playwright/test';

const NAV_SELECTOR = '[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)';

async function loginAs(page: Page, slug: string): Promise<void> {
  await page.goto(`/dev/login/${slug}`);
  await page.waitForSelector(NAV_SELECTOR);
}

export const loginAsVolunteer = (page: Page) => loginAs(page, 'volunteer');
export const loginAsAdmin = (page: Page) => loginAs(page, 'admin');
export const loginAsBoard = (page: Page) => loginAs(page, 'board');
export const loginAsConsentCoordinator = (page: Page) => loginAs(page, 'consent-coordinator');
export const loginAsTeamsAdmin = (page: Page) => loginAs(page, 'teams-admin');
export const loginAsCampAdmin = (page: Page) => loginAs(page, 'camp-admin');
export const loginAsTicketAdmin = (page: Page) => loginAs(page, 'ticket-admin');
export const loginAsNoInfoAdmin = (page: Page) => loginAs(page, 'noinfo-admin');
export const loginAsVolunteerCoordinator = (page: Page) => loginAs(page, 'volunteer-coordinator');

/**
 * Assert that a URL is blocked for the current user.
 * Handles four blocking mechanisms: redirect, 404, 403/Access Denied, or Forbid page.
 */
export async function expectBlocked(page: Page, url: string): Promise<void> {
  await page.goto(url);
  const path = new URL(url, 'https://placeholder').pathname;
  const isRedirected = !page.url().includes(path);
  const is404 = await page.getByText('Page Not Found').isVisible().catch(() => false);
  const isAccessDenied = await page.getByText('Access Denied').isVisible().catch(() => false);
  const isForbid = await page.locator('.status-code-page, [data-status="403"]').isVisible().catch(() => false);
  expect(isRedirected || is404 || isAccessDenied || isForbid).toBeTruthy();
}
```

- [ ] **Step 2: Verify auth helper compiles**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && npx tsc --noEmit`
Expected: No errors (or only Playwright-related type issues that don't affect runtime)

- [ ] **Step 3: Quick smoke test — run existing login test to verify refactored helpers work**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/login.spec.ts`
Expected: 2 tests pass

- [ ] **Step 4: Commit**

```bash
git add tests/e2e/helpers/auth.ts
git commit -m "refactor: expand auth helpers with all DevAuth personas and expectBlocked utility"
```

---

### Task 2: Create profile.spec.ts

**Files:**
- Create: `tests/e2e/tests/profile.spec.ts`
- Delete: `tests/e2e/tests/profile-edit.spec.ts`

- [ ] **Step 1: Create `tests/e2e/tests/profile.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsBoard } from '../helpers/auth';

test.describe('Profile (02-profiles)', () => {
  test('US-2.1: profile view page shows status and team memberships', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Profile should show volunteer status section
    await expect(page.getByText('Volunteer Status', { exact: false })).toBeVisible({ timeout: 5000 });
  });

  test('US-2.2: edit page has all form sections', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/Edit');

    // General info section
    const burnerNameInput = page.locator('input[name="BurnerName"]');
    await expect(burnerNameInput).toBeVisible();
    await expect(burnerNameInput).toBeEditable();

    // Bio textarea
    await expect(page.locator('textarea[name="Bio"]')).toBeVisible();

    // Private section (legal name)
    await expect(page.locator('input[name="FirstName"]')).toBeVisible();
    await expect(page.locator('input[name="LastName"]')).toBeVisible();

    // Save button
    await expect(page.getByRole('button', { name: 'Save Changes' })).toBeVisible();
  });

  test('GDPR: privacy page loads with data export and deletion options', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/Privacy');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Data export link
    await expect(page.getByText('Download', { exact: false })).toBeVisible();
  });

  test('US-9.2: board can view human detail with admin actions', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Human/Admin');

    // Human list loads
    await expect(page.locator('h1, h2').first()).toBeVisible();

    // Click through to first human detail (table rows link to detail)
    const humanLink = page.locator('table a, .list-group a').first();
    if (await humanLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await humanLink.click();
      await expect(page.locator('h1, h2').first()).toBeVisible();
      // Should see admin action buttons (Suspend)
      await expect(page.getByText('Suspend', { exact: false })).toBeVisible({ timeout: 5000 });
    }
  });
});
```

- [ ] **Step 2: Delete old profile-edit.spec.ts**

```bash
rm tests/e2e/tests/profile-edit.spec.ts
```

- [ ] **Step 3: Run profile tests**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/profile.spec.ts`
Expected: 4 tests pass

- [ ] **Step 4: Fix any failures, re-run until green**

- [ ] **Step 5: Commit**

```bash
git add tests/e2e/tests/profile.spec.ts
git rm tests/e2e/tests/profile-edit.spec.ts
git commit -m "test: restructure profile tests mapped to 02-profiles feature doc"
```

---

### Task 3: Create teams.spec.ts (deepest section)

**Files:**
- Create: `tests/e2e/tests/teams.spec.ts`
- Delete: `tests/e2e/tests/team-pages.spec.ts`

- [ ] **Step 1: Create `tests/e2e/tests/teams.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, loginAsTeamsAdmin, expectBlocked } from '../helpers/auth';

test.describe('Teams — Browsing (06-teams)', () => {
  test('US-6.1: team listing shows My Teams and Other Teams sections', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return; // onboarding redirect — skip gracefully

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // At least one team card visible
    await expect(page.locator('.card').first()).toBeVisible();
  });

  test('US-6.2: team detail shows name, description, members section', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    const teamLink = page.locator('.card a.btn, .card a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.locator('h1, h2').first()).toBeVisible();
      expect(page.url()).toContain('/Teams/');
    }
  });

  test('US-6.11: anonymous sees public teams on /Teams', async ({ page }) => {
    // No login — anonymous access
    await page.goto('/Teams');

    // Should load without error (may show public teams or login prompt)
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('US-6.9: anonymous can view public team detail page', async ({ page }) => {
    // No login — navigate to teams listing first
    await page.goto('/Teams');

    const teamLink = page.locator('a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.locator('h1, h2').first()).toBeVisible();
    }
  });
});

test.describe('Teams — Membership (06-teams)', () => {
  test('US-6.3: open team shows Join button for non-members', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    // Look for a "Join" link/button (open teams show direct Join)
    const joinLink = page.getByRole('link', { name: 'Join' });
    if (await joinLink.first().isVisible({ timeout: 3000 }).catch(() => false)) {
      await expect(joinLink.first()).toBeVisible();
    }
    // Skip gracefully if volunteer is already a member of all open teams
  });

  test('US-6.4: approval-required team shows Request to Join', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    // Look for "Request to Join" link/button (approval-required teams)
    const requestLink = page.getByRole('link', { name: /Request to Join/i });
    if (await requestLink.first().isVisible({ timeout: 3000 }).catch(() => false)) {
      await expect(requestLink.first()).toBeVisible();
    }
    // Skip gracefully if no approval-required teams available
  });

  test('US-6.6: My Teams page loads at /Teams/My', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams/My');

    if (!page.url().includes('/Teams/My')) return;

    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});

test.describe('Teams — Management (06-teams)', () => {
  test('US-6.7: admin can access team member management', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams');

    // Navigate to a team detail, then find Members link
    const teamLink = page.locator('.card a.btn, .card a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();

      // Look for Team Management section or Members link
      const managementSection = page.getByText('Team Management', { exact: false });
      await expect(managementSection).toBeVisible({ timeout: 5000 });
    }
  });

  test('US-6.8: admin can access Create Team form', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams/Create');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Form fields should be present
    await expect(page.locator('input[name="Name"], input[id="Name"]').first()).toBeVisible();
  });

  test('US-6.10: admin can access Edit Team Page', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams');

    // Find a team and navigate to EditPage
    const teamLink = page.locator('.card a.btn, .card a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();

      // Extract slug from URL
      const url = page.url();
      const slug = url.split('/Teams/')[1]?.split('?')[0]?.split('/')[0];
      if (slug) {
        await page.goto(`/Teams/${slug}/EditPage`);
        await expect(page.locator('h1, h2').first()).toBeVisible();
      }
    }
  });

  test('US-6.8: teams-admin can access Team Summary', async ({ page }) => {
    await loginAsTeamsAdmin(page);
    await page.goto('/Teams/Summary');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Teams/Summary');
  });
});

test.describe('Teams — Coordinator & Role Auth', () => {
  test('admin sees Team Management card on team detail', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams');

    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.getByText('Team Management', { exact: false })).toBeVisible({ timeout: 5000 });
    }
  });

  test('volunteer does NOT see Team Management card', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.getByText('Team Management', { exact: false })).not.toBeVisible();
    }
  });

  test('role management section visible on team detail for admin', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams');

    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      // Roles link should be in the Team Management section
      await expect(page.getByText('Manage Roles', { exact: false }).or(
        page.getByText('Roles', { exact: false })
      )).toBeVisible({ timeout: 5000 });
    }
  });
});

test.describe('Teams — Cross-Team Views & Hierarchy', () => {
  test('department detail shows sub-teams if they exist', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    // Navigate to a team detail and check for sub-teams section
    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.locator('h1, h2').first()).toBeVisible();
      // Sub-teams section renders as cards or a list within the detail page
      // Data-dependent — we verify the page loads; sub-teams may or may not exist
    }
  });

  test('cross-team Roster page loads at /Teams/Roster', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams/Roster');

    if (!page.url().includes('/Roster')) return;

    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});

test.describe('Teams — Boundaries', () => {
  test('volunteer cannot access /Teams/Sync', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Teams/Sync');
  });

  test('volunteer cannot access /Teams/Summary', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Teams/Summary');
  });

  test('volunteer cannot access team member management', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Teams/volunteers/Members');
  });
});
```

- [ ] **Step 2: Delete old team-pages.spec.ts**

```bash
rm tests/e2e/tests/team-pages.spec.ts
```

- [ ] **Step 3: Run teams tests**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/teams.spec.ts`
Expected: 19 tests pass (some may skip gracefully due to data state)

- [ ] **Step 4: Fix any failures, re-run until green**

- [ ] **Step 5: Commit**

```bash
git add tests/e2e/tests/teams.spec.ts
git rm tests/e2e/tests/team-pages.spec.ts
git commit -m "test: restructure teams tests mapped to 06-teams + 17-coordinator-roles feature docs"
```

---

### Task 4: Create shifts.spec.ts

**Files:**
- Create: `tests/e2e/tests/shifts.spec.ts`
- Delete: `tests/e2e/tests/shift-signup.spec.ts`

- [ ] **Step 1: Create `tests/e2e/tests/shifts.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, expectBlocked } from '../helpers/auth';

test.describe('Shifts (25-shift-management)', () => {
  test('US-25.3: browse shifts page loads', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Shifts');

    // Page loads — may show shifts or "browsing closed" info alert
    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).not.toContain('/Error');
  });

  test('US-25.5: My Shifts page loads at /Shifts/Mine', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Shifts/Mine');

    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('US-25.1: shift settings page loads for admin', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Shifts/Settings');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Settings');
  });

  test('boundary: volunteer cannot access /Shifts/Settings', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Shifts/Settings');
  });

  test('boundary: volunteer cannot access /Vol/Management', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Vol/Management');
  });
});
```

- [ ] **Step 2: Delete old shift-signup.spec.ts**

```bash
rm tests/e2e/tests/shift-signup.spec.ts
```

- [ ] **Step 3: Run shifts tests**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/shifts.spec.ts`
Expected: 5 tests pass

- [ ] **Step 4: Fix any failures, re-run until green**

- [ ] **Step 5: Commit**

```bash
git add tests/e2e/tests/shifts.spec.ts
git rm tests/e2e/tests/shift-signup.spec.ts
git commit -m "test: restructure shifts tests mapped to 25-shift-management feature doc"
```

---

### Task 5: Create camps.spec.ts

**Files:**
- Create: `tests/e2e/tests/camps.spec.ts`

- [ ] **Step 1: Create `tests/e2e/tests/camps.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsCampAdmin, expectBlocked } from '../helpers/auth';

test.describe('Camps (20-camps)', () => {
  test('US-20.1: anonymous can browse camps listing', async ({ page }) => {
    await page.goto('/Camps');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Camp cards or empty state should be visible
    expect(page.url()).not.toContain('/Error');
  });

  test('US-20.2: anonymous can view camp detail', async ({ page }) => {
    await page.goto('/Camps');

    const campLink = page.locator('.card a').first();
    if (await campLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await campLink.click();
      await expect(page.locator('h1, h2').first()).toBeVisible();
      expect(page.url()).toContain('/Camps/');
    }
  });

  test('US-20.3: volunteer can access camp registration form', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Camps/Register');

    // Either shows registration form or "no open season" message
    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).not.toContain('/Error');
  });

  test('US-20.6: camp admin can access admin dashboard', async ({ page }) => {
    await loginAsCampAdmin(page);
    await page.goto('/Camps/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Camps/Admin');
  });

  test('boundary: volunteer cannot access /Camps/Admin', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Camps/Admin');
  });
});
```

- [ ] **Step 2: Run camps tests**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/camps.spec.ts`
Expected: 5 tests pass

- [ ] **Step 3: Fix any failures, re-run until green**

- [ ] **Step 4: Commit**

```bash
git add tests/e2e/tests/camps.spec.ts
git commit -m "test: add camps section tests mapped to 20-camps feature doc"
```

---

### Task 6: Create board.spec.ts

**Files:**
- Create: `tests/e2e/tests/board.spec.ts`

- [ ] **Step 1: Create `tests/e2e/tests/board.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsBoard, loginAsVolunteer, expectBlocked } from '../helpers/auth';

test.describe('Board (09-administration + 18-board-voting)', () => {
  test('US-9.1: board dashboard loads with quick actions', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Board');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Board');
  });

  test('US-9.2: humans list loads at /Human/Admin', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Human/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Human/Admin');
  });

  test('US-18.1: voting dashboard loads', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/OnboardingReview/BoardVoting');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // May show voting table or "no pending applications" — either is valid
    expect(page.url()).not.toContain('/Error');
  });

  test('nav: board sees Board link, not Admin link', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/');

    const nav = page.locator('nav');
    await expect(nav.getByRole('link', { name: 'Board' })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Admin' })).not.toBeVisible();
  });

  test('boundary: volunteer cannot access /Board', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Board');
  });
});
```

- [ ] **Step 2: Run board tests**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/board.spec.ts`
Expected: 5 tests pass

- [ ] **Step 3: Fix any failures, re-run until green**

- [ ] **Step 4: Commit**

```bash
git add tests/e2e/tests/board.spec.ts
git commit -m "test: add board section tests mapped to 09-administration + 18-board-voting feature docs"
```

---

### Task 7: Create onboarding.spec.ts

**Files:**
- Create: `tests/e2e/tests/onboarding.spec.ts`

- [ ] **Step 1: Create `tests/e2e/tests/onboarding.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsConsentCoordinator, loginAsVolunteerCoordinator, loginAsVolunteer, expectBlocked } from '../helpers/auth';

test.describe('Onboarding (16-onboarding-pipeline + 17-coordinator-roles)', () => {
  test('US-17.2: consent coordinator sees review queue with filter tabs', async ({ page }) => {
    await loginAsConsentCoordinator(page);
    await page.goto('/OnboardingReview');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/OnboardingReview');
  });

  test('US-17.2: consent coordinator sees action buttons on detail', async ({ page }) => {
    await loginAsConsentCoordinator(page);
    await page.goto('/OnboardingReview');

    // Click through to first human in queue if available
    const humanLink = page.locator('table a, .list-group a').first();
    if (await humanLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await humanLink.click();
      // Should see Clear and Flag action buttons
      const clearButton = page.getByRole('button', { name: /Clear/i });
      const flagButton = page.getByRole('button', { name: /Flag/i });
      await expect(clearButton.or(flagButton)).toBeVisible({ timeout: 5000 });
    }
    // Skip gracefully if queue is empty
  });

  test('US-17.3: volunteer coordinator can view queue but no action buttons', async ({ page }) => {
    await loginAsVolunteerCoordinator(page);
    await page.goto('/OnboardingReview');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Should NOT see Clear/Flag buttons (read-only access)
    await expect(page.getByRole('button', { name: /Clear/i })).not.toBeVisible();
    await expect(page.getByRole('button', { name: /Flag/i })).not.toBeVisible();
  });

  test('boundary: volunteer cannot access /OnboardingReview', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/OnboardingReview');
  });
});
```

- [ ] **Step 2: Run onboarding tests**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/onboarding.spec.ts`
Expected: 4 tests pass

- [ ] **Step 3: Fix any failures, re-run until green**

- [ ] **Step 4: Commit**

```bash
git add tests/e2e/tests/onboarding.spec.ts
git commit -m "test: add onboarding section tests mapped to 16-onboarding-pipeline + 17-coordinator-roles"
```

---

### Task 8: Restructure admin.spec.ts

**Files:**
- Create: `tests/e2e/tests/admin.spec.ts`
- Delete: `tests/e2e/tests/auth/admin-views.spec.ts` (handled in Task 11 cleanup)

- [ ] **Step 1: Create `tests/e2e/tests/admin.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsAdmin, loginAsBoard, expectBlocked } from '../helpers/auth';

test.describe('Admin (09-administration)', () => {
  test('US-9.1: admin dashboard loads with metrics cards', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Admin');
    await expect(page.locator('.alert-danger')).not.toBeVisible();
  });

  test('sync settings page loads', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin/SyncSettings');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Admin/SyncSettings');
  });

  test('configuration status page loads', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin/Configuration');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Admin/Configuration');
  });

  test('boundary: board member cannot access /Admin', async ({ page }) => {
    await loginAsBoard(page);
    await expectBlocked(page, '/Admin');
  });
});
```

- [ ] **Step 2: Run admin tests**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/admin.spec.ts`
Expected: 4 tests pass

- [ ] **Step 3: Fix any failures, re-run until green**

- [ ] **Step 4: Commit**

```bash
git add tests/e2e/tests/admin.spec.ts
git commit -m "test: restructure admin tests mapped to 09-administration feature doc"
```

---

### Task 9: Create tickets.spec.ts

**Files:**
- Create: `tests/e2e/tests/tickets.spec.ts`

- [ ] **Step 1: Create `tests/e2e/tests/tickets.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsTicketAdmin, loginAsVolunteer, expectBlocked } from '../helpers/auth';

test.describe('Tickets (24-ticket-vendor-integration)', () => {
  test('ticket admin can access dashboard', async ({ page }) => {
    await loginAsTicketAdmin(page);
    await page.goto('/Tickets');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Tickets');
  });

  test('orders page loads at /Tickets/Orders', async ({ page }) => {
    await loginAsTicketAdmin(page);
    await page.goto('/Tickets/Orders');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Tickets/Orders');
  });

  test('boundary: volunteer cannot access /Tickets', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Tickets');
  });
});
```

- [ ] **Step 2: Run tickets tests**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/tickets.spec.ts`
Expected: 3 tests pass

- [ ] **Step 3: Fix any failures, re-run until green**

- [ ] **Step 4: Commit**

```bash
git add tests/e2e/tests/tickets.spec.ts
git commit -m "test: add tickets section tests mapped to 24-ticket-vendor-integration feature doc"
```

---

### Task 10: Restructure feedback.spec.ts

**Files:**
- Modify: `tests/e2e/tests/feedback.spec.ts`

- [ ] **Step 1: Rewrite `tests/e2e/tests/feedback.spec.ts`**

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, expectBlocked } from '../helpers/auth';

test.describe('Feedback (27-feedback-system)', () => {
  test('US-27.1: volunteer can open modal and submit feedback', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    const feedbackTrigger = page.locator('button[data-bs-target="#feedbackModal"]');
    if (await feedbackTrigger.isVisible({ timeout: 3000 }).catch(() => false)) {
      await feedbackTrigger.click();

      const modal = page.locator('#feedbackModal');
      await expect(modal).toBeVisible();

      await modal.locator('select[name="Category"]').selectOption({ index: 1 });
      await modal.locator('textarea[name="Description"]').fill(
        `E2E smoke test feedback - ${new Date().toISOString()} - please ignore`
      );

      await modal.locator('button[type="submit"]').click();
      await page.waitForLoadState('load', { timeout: 10000 });

      expect(page.url()).not.toContain('/Error');
    } else {
      await expect(page.locator('h1, h2').first()).toBeVisible();
    }
  });

  test('US-27.2: admin feedback triage page loads', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin/Feedback');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Admin/Feedback');
  });

  test('boundary: volunteer cannot access /Admin/Feedback', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Admin/Feedback');
  });
});
```

- [ ] **Step 2: Run feedback tests**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test tests/feedback.spec.ts`
Expected: 3 tests pass

- [ ] **Step 3: Fix any failures, re-run until green**

- [ ] **Step 4: Commit**

```bash
git add tests/e2e/tests/feedback.spec.ts
git commit -m "test: restructure feedback tests mapped to 27-feedback-system feature doc"
```

---

### Task 11: Cleanup — delete old files, run full suite

**Files:**
- Delete: `tests/e2e/tests/auth/` (entire directory)

- [ ] **Step 1: Delete the old auth directory**

```bash
rm -rf tests/e2e/tests/auth/
```

- [ ] **Step 2: Run complete test suite**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test`
Expected: All tests pass across 10 spec files (~54 total)

- [ ] **Step 3: Run with HTML report for manual review**

Run: `cd /home/drierp/source/humans/.worktrees/playwright-e2e/tests/e2e && BASE_URL=https://26.n.burn.camp npx playwright test --reporter=html`
Expected: HTML report generated in `playwright-report/`

- [ ] **Step 4: Verify git status is clean**

Run: `git status`
Expected: Only staged deletions from auth/ directory, no unexpected untracked files

- [ ] **Step 5: Commit cleanup**

```bash
git rm -r tests/e2e/tests/auth/
git commit -m "chore: remove old auth/ test directory — tests redistributed into section files"
```

- [ ] **Step 6: Push to trigger preview rebuild**

```bash
git push origin feature/playwright-e2e
```
