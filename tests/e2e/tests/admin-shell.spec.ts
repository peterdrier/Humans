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
 * Source-of-truth for the sidebar group/item map is
 * src/Humans.Web/ViewComponents/AdminNavTree.cs. Per-item policies determine
 * which roles see which items; only role-based-policy items are asserted here
 * (no environment-gated Dev items, no requirement-based policies such as
 * ShiftDepartmentManager or CampComplianceAccess, no claim-dependent variants).
 *
 * The full-Admin row and the AdminOnly pages (/Debug/*) it reached are covered
 * in-process instead — the E2E suite cannot mint an Admin session against a
 * deployed host since #1332. See AdminLayoutRenderTests and DebugPageRenderTests.
 * Board is the widest role left here, so it stands in wherever a test is about
 * the shell's chrome rather than about one role's item list.
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
          // System groups start collapsed on desktop, so their links are
          // display:none until expanded — hence toBeAttached rather than
          // toBeVisible. Match on DOM text rather than getByRole: role locators
          // resolve against the accessibility tree, which omits hidden elements,
          // so on a collapsed group they find nothing and toBeAttached can never
          // pass. (getByRole with includeHidden is not the fix — it also pulls
          // icon glyph text into the accessible name, which breaks the anchor.)
          // \s* absorbs whitespace contributed by each item's leading icon.
          await expect(
            section.locator('a').filter({
              hasText: new RegExp(`^\\s*${escapeRegex(item)}\\b`),
            }),
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

test.describe('Admin shell — desktop accordion', () => {
  test('system groups start collapsed; toggling expands them', async ({ page }) => {
    // Board's own Google item is "Resource sync" (TeamsAdminBoardOrAdmin); the rest
    // of the group is AdminOnly. The test is about the collapse behaviour, not the
    // item list, so any item Board can see serves.
    await loginAsBoard(page);
    await page.goto('/Admin');

    const sidebar = page.locator('aside.sidebar');
    const google = sidebar.locator('section.nav-group[data-group="Google"]');
    await expect(google).toHaveClass(/collapsed/);
    await expect(google.getByRole('link', { name: /^Resource sync\b/ })).not.toBeVisible();

    await google.locator('.group-toggle').click();
    await expect(google).not.toHaveClass(/collapsed/);
    await expect(google.getByRole('link', { name: /^Resource sync\b/ })).toBeVisible();

    // Operational groups start expanded.
    const tickets = sidebar.locator('section.nav-group[data-group="Tickets"]');
    await expect(tickets).not.toHaveClass(/collapsed/);
    await expect(tickets.getByRole('link', { name: /^Scanner\b/ })).toBeVisible();
  });
});

test.describe('Admin shell — chrome', () => {
  test('mobile viewport (<768px) renders the two-tier strip and chips switch groups', async ({ page }) => {
    // Per src/Humans.Web/wwwroot/css/admin-shell.css the sub-768px design is a
    // two-tier strip beneath the topbar (group chips above the selected
    // group's items — NOT a Bootstrap offcanvas).
    // Log in at desktop width first — the nav dropdown the auth helper waits
    // for is collapsed behind the mobile hamburger at <768px.
    await loginAsBoard(page);
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
