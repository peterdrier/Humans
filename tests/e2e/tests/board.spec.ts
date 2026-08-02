import { test, expect } from '@playwright/test';
import { loginAsBoard, loginAsVolunteer, expectBlocked } from '../helpers/auth';

test.describe('Board (09-administration + 18-board-voting)', () => {
  // NOTE: there is no /Board route (no BoardController; GET /Board is 404 even
  // anonymously). Board functionality lives at /Admin (board satisfies
  // AnyAdminRole) and /Governance/BoardVoting. The previous tests pointed at
  // /Board and passed vacuously: the 404 page has a heading, so `h1, h2` was
  // visible, and expectBlocked() treats "Page Not Found" as blocked.
  test('US-9.1: board dashboard loads with quick actions', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Admin');

    // Assert a dashboard-specific element, not just a heading: the app uses
    // UseStatusCodePagesWithReExecute("/Home/Error/{0}"), which renders
    // Views/Home/Error.cshtml *at the original URL*. That view has its own
    // h1 + h2, so `h1, h2` visible + `url contains /Admin` would still pass if
    // board lost AnyAdminRole or /Admin returned any 4xx/5xx.
    await expect(page.locator('.stats')).toBeVisible();
    expect(page.url()).toContain('/Admin');
  });

  test('US-9.2: humans list loads at /Users/Admin', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Users/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Users/Admin');
  });

  test('US-18.1: voting dashboard loads', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Governance/BoardVoting');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // May show voting table or "no pending applications" — either is valid
    expect(page.url()).not.toContain('/Error');
  });

  test('nav: board sees the Admin link', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/');

    // The layout has no "Board" nav entry — the Admin link is gated on
    // AnyAdminRole, which board satisfies. The old assertion ("sees Board, not
    // Admin") described a nav that no longer exists and had been failing.
    const nav = page.locator('nav');
    await expect(nav.getByRole('link', { name: 'Admin' })).toBeVisible();
  });

  test('nav: volunteer does not see the Admin link', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/');

    await expect(page.locator('nav').getByRole('link', { name: 'Admin' })).toHaveCount(0);
  });

  test('boundary: volunteer cannot access board voting', async ({ page }) => {
    await loginAsVolunteer(page);
    // /Board does not exist, so the old target passed on a 404 without ever
    // exercising authorization. /Governance/BoardVoting is the real BoardOrAdmin route.
    await expectBlocked(page, '/Governance/BoardVoting');
  });
});
