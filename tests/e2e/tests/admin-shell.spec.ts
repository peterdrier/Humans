import { test, expect, type Page } from '@playwright/test';
import {
  loginAsAdmin,
  loginAsBoard,
  loginAsConsentCoordinator,
  loginAsFinanceAdmin,
  loginAsHumanAdmin,
  loginAsTicketAdmin,
  loginAsVolunteerCoordinator,
} from '../helpers/auth';

/**
 * Admin shell coverage (#604) — verifies the sidebar-driven /Admin surface.
 *
 * Source-of-truth for the sidebar group/item map is
 * src/Humans.Web/ViewComponents/AdminNavTree.cs. Per-item policies determine
 * which roles see which items; only role-based-policy items are asserted here
 * (no environment-gated Dev items, no requirement-based policies such as
 * ShiftDepartmentManager or CampComplianceAccess, no claim-dependent variants).
 *
 * Markup notes: each group renders as section.nav-group[data-group="<label>"].
 * System groups start collapsed on desktop (items attached but not visible),
 * so item assertions use toBeAttached(). Group presence is asserted on the
 * section element, which also covers the mobile chip row.
 */
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
      { label: 'Tickets', items: ['Tickets', 'Transfer requests', 'Attendee contacts', 'Onsite roster', 'Campaigns', 'Scanner', 'Gate terminal', 'Early entry'] },
      { label: 'Members', items: ['Humans', 'Roles', 'Review', 'Account merges', 'Email problems', 'Audience segmentation'] },
      { label: 'Shifts', items: ['Volunteer tracking', 'Workload', 'Post-event stats', 'Orphan signups'] },
      { label: 'Barrios', items: ['Overview', 'Roles', 'Barrio map'] },
      { label: 'Cantina', items: ['Roster'] },
      { label: 'Money', items: ['Expense review', 'Finance', 'Store catalog', 'Store summary', 'Store payments'] },
      { label: 'Event Guide', items: ['Dashboard', 'Moderation', 'Settings', 'Categories', 'Venues', 'Export'] },
      { label: 'Governance', items: ['Voting', 'Applications'] },
      { label: 'Audit', items: ['Audit log'] },
      { label: 'Feedback', items: ['Feedback queue', 'Issues'] },
      { label: 'Messaging', items: ['Email preview', 'Email outbox', 'Mailer', 'Surveys'] },
      { label: 'Google', items: ['Overview', 'Workspace accounts', 'Sync outbox'] },
      { label: 'Agent', items: ['Status', 'Config', 'History'] },
      { label: 'Legal', items: ['Legal documents'] },
      { label: 'Diagnostics', items: ['Logs', 'DB stats', 'Timings', 'Hangfire', 'Health'] },
      { label: 'Design', items: ['Color palette', 'Components', 'Date formats'] },
      { label: 'Temp', items: ['Picture migration', 'Stub profile backfill'] },
    ],
  },
  {
    name: 'board',
    login: loginAsBoard,
    groups: [
      { label: 'Tickets', items: ['Tickets', 'Onsite roster', 'Scanner'] },
      { label: 'Members', items: ['Humans', 'Roles', 'Review'] },
      { label: 'Governance', items: ['Voting', 'Applications'] },
      { label: 'Audit', items: ['Audit log'] },
      { label: 'Messaging', items: ['Surveys'] },
      { label: 'Google', items: ['Resource sync'] },
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
      { label: 'Tickets', items: ['Tickets', 'Transfer requests', 'Attendee contacts', 'Onsite roster', 'Scanner', 'Gate terminal'] },
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
      { label: 'Tickets', items: ['Early entry'] },
      { label: 'Members', items: ['Review'] },
      { label: 'Shifts', items: ['Volunteer tracking', 'Workload', 'Post-event stats'] },
    ],
  },
  {
    name: 'financeAdmin',
    login: loginAsFinanceAdmin,
    groups: [
      { label: 'Money', items: ['Expense review', 'Finance', 'Store catalog', 'Store summary', 'Store payments'] },
    ],
  },
];

// Note: 'Dev' is intentionally omitted — its items are env-gated
// (!env.IsProduction()), so the group renders for admins in QA/Preview, and
// the comment at the top of this file scopes us to role-based-policy items.
const ALL_GROUP_LABELS = [
  'Tickets',
  'Members',
  'Shifts',
  'Barrios',
  'Cantina',
  'Money',
  'Event Guide',
  'Governance',
  'Audit',
  'Feedback',
  'Messaging',
  'Google',
  'Agent',
  'Legal',
  'Diagnostics',
  'Design',
  'Temp',
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
        const section = sidebar.locator(`section.nav-group[data-group="${group.label}"]`);
        await expect(section, `${role.name} should see group '${group.label}'`).toBeVisible();

        for (const item of group.items) {
          // toBeAttached, not toBeVisible: system groups start collapsed on
          // desktop, so their links render display:none until expanded.
          await expect(
            section.getByRole('link', { name: new RegExp(`^${escapeRegex(item)}\\b`) }),
            `${role.name} should see item '${item}' in '${group.label}'`,
          ).toBeAttached();
        }
      }

      for (const label of ALL_GROUP_LABELS) {
        if (expectedGroups.has(label)) continue;
        await expect(
          sidebar.locator(`section.nav-group[data-group="${label}"]`),
          `${role.name} should NOT see group '${label}'`,
        ).toHaveCount(0);
      }
    });
  }
});

test.describe('Admin shell — breadcrumb regression (controller-only-match bug, ddfdb6c1)', () => {
  test('breadcrumb on /Debug/Logs shows Diagnostics / Logs', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Debug/Logs');

    const crumb = page.locator('.crumb');
    await expect(crumb).toContainText('Diagnostics');
    await expect(crumb.locator('.here')).toHaveText('Logs');

    // Regression: only the "Logs" sidebar link should be marked active, not
    // every other item under controller=Admin.
    const sidebar = page.locator('aside.sidebar');
    const activeLinks = sidebar.locator('a.active');
    await expect(activeLinks).toHaveCount(1);
    await expect(activeLinks).toHaveText(/Logs/);

    // The system group holding the active item must not start collapsed.
    const diagnostics = sidebar.locator('section.nav-group[data-group="Diagnostics"]');
    await expect(diagnostics).not.toHaveClass(/collapsed/);
    await expect(activeLinks).toBeVisible();
  });

  test('breadcrumb on /Debug/DbStats shows Diagnostics / DB stats', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Debug/DbStats');

    const crumb = page.locator('.crumb');
    await expect(crumb).toContainText('Diagnostics');
    await expect(crumb.locator('.here')).toHaveText('DB stats');

    const sidebar = page.locator('aside.sidebar');
    const activeLinks = sidebar.locator('a.active');
    await expect(activeLinks).toHaveCount(1);
    await expect(activeLinks).toHaveText(/DB stats/);
  });
});

test.describe('Admin shell — desktop accordion', () => {
  test('system groups start collapsed; toggling expands them', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin');

    const sidebar = page.locator('aside.sidebar');
    const google = sidebar.locator('section.nav-group[data-group="Google"]');
    await expect(google).toHaveClass(/collapsed/);
    await expect(google.getByRole('link', { name: /^Workspace accounts\b/ })).not.toBeVisible();

    await google.locator('.group-toggle').click();
    await expect(google).not.toHaveClass(/collapsed/);
    await expect(google.getByRole('link', { name: /^Workspace accounts\b/ })).toBeVisible();

    // Operational groups start expanded.
    const tickets = sidebar.locator('section.nav-group[data-group="Tickets"]');
    await expect(tickets).not.toHaveClass(/collapsed/);
    await expect(tickets.getByRole('link', { name: /^Scanner\b/ })).toBeVisible();
  });
});

test.describe('Admin shell — maintenance page', () => {
  test('/Debug/Maintenance loads with Clear Hangfire Locks form', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Debug/Maintenance');

    expect(page.url()).toContain('/Debug/Maintenance');
    await expect(page.locator('h1', { hasText: 'Maintenance' })).toBeVisible();

    const form = page.locator('form[action*="ClearHangfireLocks"]');
    await expect(form).toBeVisible();
    await expect(form.locator('button[type="submit"]', { hasText: 'Clear Hangfire Locks' })).toBeVisible();
  });

});

test.describe('Admin shell — chrome', () => {
  test('mobile viewport (<768px) renders the two-tier strip and chips switch groups', async ({ page }) => {
    // Per src/Humans.Web/wwwroot/css/admin-shell.css the sub-768px design is a
    // two-tier strip beneath the topbar (group chips above the selected
    // group's items — NOT a Bootstrap offcanvas).
    // Log in at desktop width first — the nav dropdown the auth helper waits
    // for is collapsed behind the mobile hamburger at <768px.
    await loginAsAdmin(page);
    await page.setViewportSize({ width: 480, height: 800 });
    await page.goto('/Admin');

    const sidebar = page.locator('aside.sidebar');
    await expect(sidebar).toBeVisible();
    await expect(sidebar.locator('.group-chips')).toBeVisible();
    // Exactly one item row is shown at a time.
    await expect(sidebar.locator('section.nav-group.m-active')).toHaveCount(1);

    // Tapping a chip switches the visible item row.
    await sidebar.locator('.group-chip[data-group="Members"]').click();
    await expect(sidebar.locator('section.nav-group.m-active')).toHaveAttribute('data-group', 'Members');
    await expect(
      sidebar.locator('section.nav-group[data-group="Members"]').getByRole('link', { name: /^Humans\b/ }),
    ).toBeVisible();

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
