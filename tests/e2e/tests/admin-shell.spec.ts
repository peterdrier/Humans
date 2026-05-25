import { test, expect, type Page } from '@playwright/test';
import {
  loginAsAdmin,
  loginAsBoard,
  loginAsConsentCoordinator,
  loginAsFinanceAdmin,
  loginAsHumanAdmin,
  loginAsTicketAdmin,
  loginAsVolunteerCoordinator,
  postWithCsrf,
} from '../helpers/auth';

/**
 * Admin shell coverage (#604) — verifies the new sidebar-driven /Admin surface
 * delivered by peterdrier/Humans#349.
 *
 * Source-of-truth for the sidebar group/item map is
 * src/Humans.Web/ViewComponents/AdminNavTree.cs. Per-item policies determine
 * which roles see which items; only role-based-policy items are asserted here
 * (no environment-gated Dev items, no claim-dependent variants).
 */

// Role -> sidebar group labels expected to be present (groups whose items the
// role can see at all). Items inside each group are asserted per-role below.
//
// Derived from AdminNavTree policies:
//   Operations       — TicketAdminBoardOrAdmin (TicketAdmin / Admin / Board)
//   Members.Humans   — HumanAdminBoardOrAdmin
//   Members.Review   — ReviewQueueAccess (ConsentCoord / VolunteerCoord / Board / Admin)
//   Money.Finance    — FinanceAdminOrAdmin
//   Money.Store      — StoreCatalogAdmin (StoreAdmin / FinanceAdmin / Admin)
//   Governance       — BoardOrAdmin
//   Integrations,
//   Agent,
//   People data,
//   Diagnostics,
//   Dev              — AdminOnly
interface SidebarExpectation {
  name: string;
  login: (page: Page) => Promise<void>;
  groups: { label: string; items: string[] }[];
}

const sidebarMatrix: SidebarExpectation[] = [
  {
    name: 'admin',
    login: loginAsAdmin,
    groups: [
      { label: 'Operations', items: ['Tickets', 'Scanner'] },
      { label: 'Members', items: ['Humans', 'Roles', 'Review'] },
      { label: 'Money', items: ['Finance', 'Store catalog'] },
      { label: 'Governance', items: ['Voting', 'Board'] },
      { label: 'Integrations', items: ['Google', 'Email preview', 'Email outbox', 'Campaigns', 'Workspace accounts'] },
      { label: 'Agent', items: ['Agent Config', 'Agent History'] },
      { label: 'People data', items: ['Merge requests', 'Duplicate detection', 'Audience segmentation', 'Legal documents', 'Backfill Provider/IsGoogle', 'Stub Profile Backfill'] },
      { label: 'Diagnostics', items: ['Logs', 'DB stats', 'Cache stats', 'Configuration', 'Maintenance', 'Orphan signups', 'Hangfire', 'Health'] },
    ],
  },
  {
    name: 'board',
    login: loginAsBoard,
    groups: [
      { label: 'Operations', items: ['Tickets', 'Scanner'] },
      { label: 'Members', items: ['Humans', 'Roles', 'Review'] },
      { label: 'Governance', items: ['Voting', 'Board'] },
    ],
  },
  {
    name: 'humanAdmin',
    login: loginAsHumanAdmin,
    groups: [
      { label: 'Members', items: ['Humans', 'Roles'] },
    ],
  },
  {
    name: 'ticketAdmin',
    login: loginAsTicketAdmin,
    groups: [
      { label: 'Operations', items: ['Tickets', 'Scanner'] },
    ],
  },
  {
    name: 'consentCoordinator',
    login: loginAsConsentCoordinator,
    groups: [
      { label: 'Members', items: ['Review'] },
    ],
  },
  {
    name: 'volunteerCoordinator',
    login: loginAsVolunteerCoordinator,
    groups: [
      { label: 'Members', items: ['Review'] },
    ],
  },
  {
    name: 'financeAdmin',
    login: loginAsFinanceAdmin,
    groups: [
      { label: 'Money', items: ['Finance', 'Store catalog'] },
    ],
  },
];

// Note: 'Dev' is intentionally omitted — its items are env-gated
// (!env.IsProduction()), so the group renders for admins in QA/Preview, and
// the comment at the top of this file scopes us to role-based-policy items.
const ALL_GROUP_LABELS = [
  'Operations',
  'Members',
  'Money',
  'Governance',
  'Integrations',
  'Agent',
  'People data',
  'Diagnostics',
];

test.describe('Admin shell — sidebar visibility matrix', () => {
  for (const role of sidebarMatrix) {
    test(`${role.name}: sees expected sidebar groups + items`, async ({ page }) => {
      await role.login(page);
      await page.goto('/Admin');

      const sidebar = page.locator('aside.sidebar');
      await expect(sidebar).toBeVisible();

      const expectedGroups = new Set(role.groups.map(g => g.label));

      for (const group of role.groups) {
        await expect(
          sidebar.locator('h4', { hasText: new RegExp(`^${escapeRegex(group.label)}$`) }),
          `${role.name} should see group '${group.label}'`,
        ).toBeVisible();

        for (const item of group.items) {
          await expect(
            sidebar.getByRole('link', { name: new RegExp(`^${escapeRegex(item)}\\b`) }),
            `${role.name} should see item '${item}' in '${group.label}'`,
          ).toBeVisible();
        }
      }

      for (const label of ALL_GROUP_LABELS) {
        if (expectedGroups.has(label)) continue;
        await expect(
          sidebar.locator('h4', { hasText: new RegExp(`^${escapeRegex(label)}$`) }),
          `${role.name} should NOT see group '${label}'`,
        ).not.toBeVisible();
      }
    });
  }
});

test.describe('Admin shell — breadcrumb regression (controller-only-match bug, ddfdb6c1)', () => {
  test('breadcrumb on /Admin/Logs shows Diagnostics / Logs', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin/Logs');

    const crumb = page.locator('.crumb');
    await expect(crumb).toContainText('Diagnostics');
    await expect(crumb.locator('.here')).toHaveText('Logs');

    // Regression: only the "Logs" sidebar link should be marked active, not
    // every other item under controller=Admin.
    const sidebar = page.locator('aside.sidebar');
    const activeLinks = sidebar.locator('a.active');
    await expect(activeLinks).toHaveCount(1);
    await expect(activeLinks).toHaveText(/Logs/);
  });

  test('breadcrumb on /Admin/DbStats shows Diagnostics / DB stats', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin/DbStats');

    const crumb = page.locator('.crumb');
    await expect(crumb).toContainText('Diagnostics');
    await expect(crumb.locator('.here')).toHaveText('DB stats');

    const sidebar = page.locator('aside.sidebar');
    const activeLinks = sidebar.locator('a.active');
    await expect(activeLinks).toHaveCount(1);
    await expect(activeLinks).toHaveText(/DB stats/);
  });
});

test.describe('Admin shell — maintenance + backfill pages', () => {
  test('/Admin/Maintenance loads with Clear Hangfire Locks form', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin/Maintenance');

    expect(page.url()).toContain('/Admin/Maintenance');
    await expect(page.locator('h1', { hasText: 'Maintenance' })).toBeVisible();

    const form = page.locator('form[action*="ClearHangfireLocks"]');
    await expect(form).toBeVisible();
    await expect(form.locator('button[type="submit"]', { hasText: 'Clear Hangfire Locks' })).toBeVisible();
  });

  test('/Admin/BackfillUserEmailProviders GET loads, POST runs idempotently', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin/BackfillUserEmailProviders');

    expect(page.url()).toContain('/Admin/BackfillUserEmailProviders');
    // Form is asp-action="BackfillUserEmailProvidersRun" but that POST is
    // attribute-routed as [HttpPost("BackfillUserEmailProviders")], so the
    // rendered action attribute resolves to /Admin/BackfillUserEmailProviders.
    await expect(page.locator('form[action*="BackfillUserEmailProviders"]')).toBeVisible();
    await expect(page.locator('form button[type="submit"]', { hasText: 'Run backfill' })).toBeVisible();

    // Backfill is idempotent — re-running over already-backfilled rows is safe.
    const response = await postWithCsrf(page, '/Admin/BackfillUserEmailProviders', '');
    // Either redirected back to the page or rendered inline; both are non-error.
    expect([200, 302]).toContain(response.status());
  });
});

test.describe('Admin shell — chrome', () => {
  test('mobile viewport (<768px) renders sidebar without breaking the shell', async ({ page }) => {
    // Per src/Humans.Web/wwwroot/css/admin-shell.css the sub-768px design is a
    // horizontal scroll strip beneath the topbar (NOT a Bootstrap offcanvas).
    // We assert the shell still renders cleanly at narrow viewport.
    // Log in at desktop width first — the nav dropdown the auth helper waits
    // for is collapsed behind the mobile hamburger at <768px.
    await loginAsAdmin(page);
    await page.setViewportSize({ width: 480, height: 800 });
    await page.goto('/Admin');

    await expect(page.locator('aside.sidebar')).toBeVisible();
    await expect(page.locator('aside.sidebar .sidebar-scroll')).toBeVisible();
    // Topbar exit-admin remains reachable.
    await expect(page.locator('a.exit-admin')).toBeVisible();
  });

  test('exit-admin link navigates to member home', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin');

    const exit = page.locator('a.exit-admin').first();
    await expect(exit).toBeVisible();
    await exit.click();
    await page.waitForLoadState('domcontentloaded');

    // Member home is /Home/Index (path "/"), and the admin shell is gone.
    expect(new URL(page.url()).pathname).toMatch(/^\/(Home(\/Index)?)?$/i);
    await expect(page.locator('body.admin-shell')).toHaveCount(0);
  });

  test('dashboard tiles render: active humans, shift coverage, open feedback, recent activity', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin');

    // Tiles from _DashboardStats (active humans, shifts staffed, open feedback)
    const stats = page.locator('.stats');
    await expect(stats).toBeVisible();
    await expect(stats.locator('.stat .label', { hasText: 'Active humans' })).toBeVisible();
    await expect(stats.locator('.stat .label', { hasText: /Shifts staffed/ })).toBeVisible();
    await expect(stats.locator('.stat .label', { hasText: 'Open feedback' })).toBeVisible();

    // Shift coverage delta (drives the system-health-style summary line).
    await expect(page.locator('.page-head .sub')).toContainText('shift coverage');

    // Recent activity card (last 24h).
    await expect(page.locator('.card-head h3', { hasText: 'Recent activity' })).toBeVisible();
  });
});

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
