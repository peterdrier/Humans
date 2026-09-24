import { test, expect, type Page } from '@playwright/test';
import {
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
 * Source-of-truth for the group/item map is every section's
 * src/Sections/*/SectionAdminNav.cs, merged by AdminNavComposition. Per-item
 * policies determine which roles see which items; only role-based-policy items
 * are asserted here (no environment-gated Development items, no
 * requirement-based policies such as ShiftDepartmentManager or
 * CampComplianceAccess, no claim-dependent variants).
 *
 * The full-Admin row and the AdminOnly pages (/Debug/*) it reached are covered
 * in-process instead — the E2E suite cannot mint an Admin session against a
 * deployed host since #1332. See AdminLayoutRenderTests and DebugPageRenderTests.
 * Board is the widest role left here, so it stands in wherever a test is about
 * the shell's chrome rather than about one role's item list.
 *
 * Markup notes: the sidebar is one row per group, a[data-group="<label>"],
 * linking to the group's first visible item. A group's items render as the
 * tab strip (nav.admin-tabs) on its pages, hidden when only one is visible.
 */
interface SidebarExpectation {
  name: string;
  login: (page: Page) => Promise<void>;
  groups: { label: string; items: string[] }[];
}

const sidebarMatrix: SidebarExpectation[] = [
  {
    name: 'board',
    login: loginAsBoard,
    groups: [
      { label: 'Audit', items: ['Audit log'] },
      { label: 'Google', items: ['Resource sync'] },
      { label: 'Governance', items: ['Voting', 'Applications'] },
      { label: 'Onboarding', items: ['Review'] },
      { label: 'Scanner', items: ['Scanner'] },
      { label: 'Surveys', items: ['Surveys'] },
      { label: 'Tickets', items: ['Tickets', 'Onsite roster'] },
      { label: 'Users', items: ['Humans', 'Roles'] },
      { label: 'Workgroups', items: ['Workgroups'] },
    ],
  },
  {
    name: 'humanAdmin',
    login: loginAsHumanAdmin,
    groups: [
      { label: 'Users', items: ['Humans', 'Roles'] },
    ],
  },
  {
    name: 'ticketAdmin',
    login: loginAsTicketAdmin,
    groups: [
      { label: 'Gate', items: ['Terminal'] },
      { label: 'Scanner', items: ['Scanner'] },
      { label: 'Tickets', items: ['Tickets', 'Transfer requests', 'Attendee contacts', 'Onsite roster'] },
    ],
  },
  {
    name: 'consentCoordinator',
    login: loginAsConsentCoordinator,
    groups: [
      { label: 'Onboarding', items: ['Review'] },
    ],
  },
  {
    name: 'volunteerCoordinator',
    login: loginAsVolunteerCoordinator,
    groups: [
      { label: 'Early Entry', items: ['Early entry'] },
      { label: 'Onboarding', items: ['Review'] },
      { label: 'Shifts', items: ['Volunteer tracking', 'Workload', 'Post-event stats'] },
    ],
  },
  {
    name: 'financeAdmin',
    login: loginAsFinanceAdmin,
    groups: [
      { label: 'Budget', items: ['Overview'] },
      { label: 'Expenses', items: ['Review'] },
      { label: 'Finance', items: ['Holded connector'] },
      { label: 'Holded', items: ['Holded'] },
      { label: 'Store', items: ['Catalog', 'Summary', 'Payments'] },
    ],
  },
];

// Note: 'Development' is intentionally omitted — its items are env-gated
// (env.IsDevelopment()), so the group renders only on local dev, and
// the comment at the top of this file scopes us to role-based-policy items.
const ALL_GROUP_LABELS = [
  'Agent',
  'Audit',
  'Backdoor',
  'Barrios',
  'Budget',
  'Campaigns',
  'Cantina',
  'City Planning',
  'Consent',
  'Debug',
  'Early Entry',
  'Email',
  'Events',
  'Expenses',
  'Finance',
  'Gate',
  'Google',
  'Governance',
  'Holded',
  'Issues',
  'MailerLite',
  'Onboarding',
  'Rideshare',
  'Scanner',
  'Settings',
  'Shifts',
  'Store',
  'Surveys',
  'Tickets',
  'Users',
  'Workgroups',
];

test.describe('Admin shell — sidebar visibility matrix', () => {
  for (const role of sidebarMatrix) {
    test(`${role.name}: sees expected sidebar rows + tabs`, async ({ page }) => {
      await role.login(page);
      await page.goto('/Admin');

      const sidebar = page.locator('aside.sidebar');
      await expect(sidebar).toBeVisible();

      const rowSelector = (label: string) => `nav.sidebar-rows a[data-group="${label}"]`;
      const expectedGroups = new Set(role.groups.map(g => g.label));

      for (const label of ALL_GROUP_LABELS) {
        if (expectedGroups.has(label)) continue;
        await expect(
          sidebar.locator(rowSelector(label)),
          `${role.name} should NOT see group '${label}'`,
        ).toHaveCount(0);
      }

      for (const group of role.groups) {
        const row = sidebar.locator(rowSelector(group.label));
        await expect(row, `${role.name} should see group '${group.label}'`).toBeVisible();
        if (group.items.length < 2) continue;

        // The row lands on the group's first page, whose tab strip lists the group's items.
        await page.goto((await row.getAttribute('href'))!);
        const tabs = page.locator('nav.admin-tabs');
        for (const item of group.items) {
          // \s* absorbs whitespace around each tab's label.
          await expect(
            tabs.locator('a').filter({ hasText: new RegExp(`^\\s*${escapeRegex(item)}\\b`) }),
            `${role.name} should see tab '${item}' in '${group.label}'`,
          ).toBeVisible();
        }
        await page.goto('/Admin');
      }
    });
  }
});

test.describe('Admin shell — rows and tabs', () => {
  test('a group row lands on its first page, marks itself active and shows the tabs', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Admin');

    const sidebar = page.locator('aside.sidebar');
    await sidebar.locator('nav.sidebar-rows a[data-group="Users"]').click();
    await page.waitForLoadState('domcontentloaded');

    await expect(sidebar.locator('nav.sidebar-rows a.active')).toHaveAttribute('data-group', 'Users');
    await expect(page.locator('nav.admin-tabs a[aria-current="page"]')).toHaveText(/Humans/);
    await expect(page.locator('.crumb')).toContainText('Users');
  });
});

test.describe('Admin shell — chrome', () => {
  test('mobile viewport (<768px) renders the rows as a horizontal strip', async ({ page }) => {
    // Per src/Humans.Web/wwwroot/css/admin-shell.css the sub-768px design is a
    // horizontally scrolling row strip beneath the topbar (NOT a Bootstrap offcanvas).
    // Log in at desktop width first — the nav dropdown the auth helper waits
    // for is collapsed behind the mobile hamburger at <768px.
    await loginAsBoard(page);
    await page.setViewportSize({ width: 480, height: 800 });
    await page.goto('/Admin');

    const sidebar = page.locator('aside.sidebar');
    await expect(sidebar).toBeVisible();
    const rows = sidebar.locator('nav.sidebar-rows');
    await expect(rows).toBeVisible();
    await expect(rows).toHaveCSS('flex-direction', 'row');

    // Tapping a row navigates to that group.
    await rows.locator('a[data-group="Users"]').click();
    await page.waitForLoadState('domcontentloaded');
    await expect(rows.locator('a.active')).toHaveAttribute('data-group', 'Users');

    // Topbar exit-admin remains reachable.
    await expect(page.locator('a.exit-admin')).toBeVisible();
  });

  test('exit-admin link navigates to member home', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Admin');

    const exit = page.locator('a.exit-admin').first();
    await expect(exit).toBeVisible();
    await exit.click();
    await page.waitForLoadState('domcontentloaded');

    // Member home is /Home/Index (path "/"), and the admin shell is gone.
    expect(new URL(page.url()).pathname).toMatch(/^\/(Home(\/Index)?)?$/i);
    await expect(page.locator('body.admin-shell')).toHaveCount(0);
  });

  test('dashboard tiles render: active profiles, shift coverage', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Admin');

    // Tiles from _DashboardStats. The "Active humans" tile was renamed to
    // "Active (has profile)" by #546 (UserInfo-driven stats); the test was never
    // updated, so this assertion had been failing against a label that no longer exists.
    // The "Open feedback" tile and the "Recent activity" card are authorize-policy
    // ="AdminOnly" (#977) — Board reaches /Admin but is deliberately not shown either,
    // so they are asserted in-process instead (AdminLayoutRenderTests).
    const stats = page.locator('.stats');
    await expect(stats).toBeVisible();
    await expect(stats.locator('.stat .label', { hasText: 'Active (has profile)' })).toBeVisible();
    await expect(stats.locator('.stat .label', { hasText: /Shifts staffed/ })).toBeVisible();

    // Shift coverage delta (drives the system-health-style summary line).
    await expect(page.locator('.page-head .sub')).toContainText('shift coverage');
  });
});

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
