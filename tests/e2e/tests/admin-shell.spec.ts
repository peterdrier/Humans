import { test, expect } from '@playwright/test';
import { loginAs, type Persona } from '../helpers/auth';

/**
 * Admin shell coverage (#604) — verifies the sidebar-driven /Admin surface.
 *
 * Source-of-truth for the group/item map is every section's
 * src/Sections/{Section}/Section.cs (ISectionAdminNav), merged by AdminNavComposition. Per-item
 * policies determine which roles see which items. Each role's sidebar row set is
 * asserted exactly, so a gate change shows up here and the matrix moves with it.
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
  persona: Persona;
  groups: { label: string; items: string[] }[];
}

const sidebarMatrix: SidebarExpectation[] = [
  {
    // Board reads every section admin page and writes only its own decisions, so it sees every
    // <Section>Policies.Read and BoardOrAdmin item and no AdminOnly or Manage one.
    name: 'board',
    persona: 'board',
    groups: [
      { label: 'Agent', items: ['Status', 'History'] },
      { label: 'Audit', items: ['Audit log'] },
      { label: 'Backdoor', items: ['API keys'] },
      { label: 'Box Office', items: ['Box Office'] },
      { label: 'Budget', items: ['Overview'] },
      { label: 'Cantina', items: ['Roster'] },
      { label: 'Consent', items: ['Review queue', 'Legal documents'] },
      {
        label: 'Debug',
        items: [
          'Logs', 'HTTP errors', 'Database', 'DB stats', 'Cache stats', 'Client stats', 'Timings', 'Sections',
          'Configuration', 'Maintenance', 'All users', 'Data migrations', 'Hangfire', 'Health',
          'Color palette', 'Components', 'Date formats', 'Translations',
        ],
      },
      { label: 'Early Entry', items: ['Early entry'] },
      { label: 'Events', items: ['Dashboard', 'Moderation', 'Categories', 'Venues', 'Export'] },
      {
        label: 'Finance',
        items: [
          'Overview', 'Cash flow', 'Creditors', 'SEPA payouts', 'Pleo reconcile', 'Expense recon', 'Account map',
          'Unmatched docs',
        ],
      },
      { label: 'Gate', items: ['Terminal', 'Arrivals'] },
      { label: 'Google', items: ['Resource sync'] },
      { label: 'Governance', items: ['Voting', 'Applications', 'Assembly Votes'] },
      { label: 'Hi.Events', items: ['Hi.Events'] },
      { label: 'Holded', items: ['Summary', 'Accounts', 'Purchases', 'API calls'] },
      { label: 'Issues', items: ['Issues'] },
      { label: 'Pleo', items: ['Pleo'] },
      { label: 'pretix', items: ['pretix'] },
      { label: 'Rideshare', items: ['Settings & stats', 'Day roster'] },
      { label: 'Roles', items: ['Roles'] },
      { label: 'Scanner', items: ['Scanner'] },
      { label: 'Store', items: ['Catalog', 'Summary', 'Payments', 'Team transfers', 'Order years', 'Payment methods'] },
      { label: 'Stripe', items: ['Stripe'] },
      // Board also sees Approvals, but the Surveys index is app-access and renders outside the admin
      // shell, so its page has no tab strip to assert against.
      { label: 'Surveys', items: ['Surveys'] },
      { label: 'Tasks', items: ['Tasks'] },
      { label: 'Tickets', items: ['Tickets', 'Attendee contacts', 'Onsite roster', 'Sales', 'Find an order'] },
      { label: 'Users', items: ['Humans', 'Roles'] },
      { label: 'Workgroups', items: ['Workgroups'] },
    ],
  },
  {
    name: 'usersAdmin',
    persona: 'users-admin',
    groups: [
      { label: 'Issues', items: ['Issues'] },
      { label: 'Surveys', items: ['Surveys'] },
      { label: 'Users', items: ['Humans', 'Roles'] },
    ],
  },
  {
    name: 'ticketsAdmin',
    persona: 'tickets-admin',
    groups: [
      { label: 'Box Office', items: ['Box Office'] },
      { label: 'Gate', items: ['Terminal', 'Arrivals'] },
      { label: 'Hi.Events', items: ['Hi.Events'] },
      { label: 'Issues', items: ['Issues'] },
      { label: 'pretix', items: ['pretix'] },
      { label: 'Scanner', items: ['Scanner'] },
      { label: 'Surveys', items: ['Surveys'] },
      { label: 'Tickets', items: ['Tickets', 'Attendee contacts', 'Onsite roster', 'Sales', 'Find an order'] },
    ],
  },
  {
    name: 'consentCoordinator',
    persona: 'consent-coordinator',
    groups: [
      { label: 'Consent', items: ['Review queue'] },
      { label: 'Issues', items: ['Issues'] },
      { label: 'Surveys', items: ['Surveys'] },
    ],
  },
  {
    name: 'shiftsAdmin',
    persona: 'shifts-admin',
    groups: [
      { label: 'Issues', items: ['Issues'] },
      {
        label: 'Shifts',
        items: ['Dashboard', 'Summary by camp', 'Volunteer tracking', 'Workload', 'Post-event stats'],
      },
      { label: 'Surveys', items: ['Surveys'] },
    ],
  },
  {
    name: 'financeAdmin',
    persona: 'finance-admin',
    groups: [
      { label: 'Budget', items: ['Overview'] },
      { label: 'Expenses', items: ['Review'] },
      {
        label: 'Finance',
        items: [
          'Overview', 'Cash flow', 'Creditors', 'SEPA payouts', 'Pleo reconcile', 'Expense recon', 'Account map',
          'Unmatched docs',
        ],
      },
      { label: 'Holded', items: ['Summary', 'Accounts', 'Purchases', 'API calls'] },
      { label: 'Issues', items: ['Issues'] },
      { label: 'Pleo', items: ['Pleo'] },
      { label: 'Store', items: ['Catalog', 'Summary', 'Payments', 'Team transfers', 'Order years', 'Payment methods'] },
      { label: 'Stripe', items: ['Stripe'] },
      { label: 'Surveys', items: ['Surveys'] },
    ],
  },
];

// Groups a persona can also reach through QA data rather than its role: program managers
// (Grants, Pre-approvals), team coordinators (Shifts) and camp leads (Barrios). Left out of the
// exact comparison so a persona added to a program or team on QA doesn't fail the matrix.
const DATA_DEPENDENT_GROUPS = ['Barrios', 'Grants', 'Pre-approvals', 'Shifts'];

test.describe('Admin shell — sidebar visibility matrix', () => {
  for (const role of sidebarMatrix) {
    test(`${role.name}: sees expected sidebar rows + tabs`, async ({ page }) => {
      await loginAs(page, role.persona);
      await page.goto('/Admin');

      const sidebar = page.locator('aside.sidebar');
      await expect(sidebar).toBeVisible();

      const rowSelector = (label: string) => `nav.sidebar-rows a[data-group="${label}"]`;

      // The exact row set, so a group the role gains or loses fails here by name.
      const roleOnly = (labels: string[]) => labels.filter(l => !DATA_DEPENDENT_GROUPS.includes(l)).sort();
      const rows = await sidebar.locator('nav.sidebar-rows a[data-group]').evaluateAll(
        els => els.map(el => el.getAttribute('data-group')!),
      );
      expect(roleOnly(rows), `${role.name} sidebar rows`).toEqual(roleOnly(role.groups.map(g => g.label)));

      for (const group of role.groups) {
        const row = sidebar.locator(rowSelector(group.label));
        await expect(row, `${role.name} should see group '${group.label}'`).toBeVisible();

        // The row lands on the group's first page, whose tab strip lists exactly the group's visible items, in order
        // — none when only one is visible. The pill span is a count, not part of the label.
        await page.goto((await row.getAttribute('href'))!);
        const tabs = await page.locator('nav.admin-tabs a > span:not(.pill)').allTextContents();
        expect(tabs.map(t => t.trim()), `${role.name} tabs in '${group.label}'`)
          .toEqual(group.items.length < 2 ? [] : group.items);
        await page.goto('/Admin');
      }
    });
  }
});

test.describe('Admin shell — rows and tabs', () => {
  test('a group row lands on its first page, marks itself active and shows the tabs', async ({ page }) => {
    await loginAs(page, 'board');
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
    await loginAs(page, 'board');
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
    await loginAs(page, 'board');
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
    await loginAs(page, 'board');
    await page.goto('/Admin');

    // Tiles from _DashboardStats. The "Active humans" tile was renamed to
    // "Active (has profile)" by #546 (UserInfo-driven stats); the test was never
    // updated, so this assertion had been failing against a label that no longer exists.
    // The "Recent activity" card is authorize-policy
    // ="AdminOnly" (#977): Board reaches /Admin but is deliberately not shown it,
    // so it is asserted in-process instead (AdminLayoutRenderTests).
    const stats = page.locator('.stats');
    await expect(stats).toBeVisible();
    await expect(stats.locator('.stat .label', { hasText: 'Active (has profile)' })).toBeVisible();
    await expect(stats.locator('.stat .label', { hasText: /Shifts staffed/ })).toBeVisible();

    // Shift coverage delta (drives the system-health-style summary line).
    await expect(page.locator('.page-head .sub')).toContainText('shift coverage');
  });
});
