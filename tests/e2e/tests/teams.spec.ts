import { test, expect } from '@playwright/test';
import { loginAs, expectBlocked } from '../helpers/auth';

// "Dev Test Department" (approval required, with sub-team "Dev Test SubTeam") is seeded on every
// host when the coordinator persona logs in, so the specs point at it instead of clicking the
// first card they find.
const DEPT_SLUG = 'dev-test-department';

test.describe('Teams — Browsing (06-teams)', () => {
  test('US-6.1: team listing shows team cards', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto('/Teams');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    await expect(page.locator('.card').first()).toBeVisible();
  });

  test('US-6.2: team detail shows the team and its sub-teams', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto(`/Teams/${DEPT_SLUG}`);

    await expect(page.getByRole('heading', { name: 'Dev Test Department' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Dev Test SubTeam' })).toBeVisible();
  });

  test('US-6.11: anonymous sees the teams directory', async ({ page }) => {
    await page.goto('/Teams');

    expect(new URL(page.url()).pathname).toBe('/Teams');
    await expect(page.locator('.card').first()).toBeVisible();
  });

  test('US-6.9: anonymous gets a 404 for a team without a public page', async ({ page }) => {
    const response = await page.goto(`/Teams/${DEPT_SLUG}`);

    expect(response?.status()).toBe(404);
  });
});

test.describe('Teams — Membership (06-teams)', () => {
  test('US-6.4: approval-required team shows Request to Join to a non-member', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto(`/Teams/${DEPT_SLUG}`);

    await expect(page.getByRole('link', { name: 'Request to Join' })).toBeVisible();
  });

  test('US-6.6: My Teams page loads at /Teams/My', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto('/Teams/My');

    expect(new URL(page.url()).pathname).toBe('/Teams/My');
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});

test.describe('Teams — Management (06-teams)', () => {
  test('US-6.7: teams-admin sees the Team Management card with Manage Roles', async ({ page }) => {
    await loginAs(page, 'teams-admin');
    await page.goto(`/Teams/${DEPT_SLUG}`);

    await expect(page.getByText('Team Management', { exact: false })).toBeVisible();
    await expect(page.getByRole('link', { name: /Manage Roles/i })).toBeVisible();
  });

  test('volunteer does NOT see the Team Management card', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto(`/Teams/${DEPT_SLUG}`);

    await expect(page.getByRole('heading', { name: 'Dev Test Department' })).toBeVisible();
    await expect(page.getByText('Team Management', { exact: false })).not.toBeVisible();
  });

  test('US-6.8: teams-admin can access Create Team form', async ({ page }) => {
    await loginAs(page, 'teams-admin');
    await page.goto('/Teams/Create');

    await expect(page.getByRole('textbox', { name: /Team Name/i })).toBeVisible();
  });

  test('US-6.10: teams-admin can access Edit Team Page', async ({ page }) => {
    await loginAs(page, 'teams-admin');
    await page.goto(`/Teams/${DEPT_SLUG}/EditPage`);

    expect(new URL(page.url()).pathname).toBe(`/Teams/${DEPT_SLUG}/EditPage`);
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('US-6.8: teams-admin can access Team Summary', async ({ page }) => {
    await loginAs(page, 'teams-admin');
    await page.goto('/Teams/Summary');

    expect(new URL(page.url()).pathname).toBe('/Teams/Summary');
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});

test.describe('Teams — Cross-Team Views', () => {
  test('cross-team Roster page loads at /Teams/Roster', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto('/Teams/Roster');

    expect(new URL(page.url()).pathname).toBe('/Teams/Roster');
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});

test.describe('Teams — Boundaries', () => {
  test('volunteer cannot access /Teams/Summary', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await expectBlocked(page, '/Teams/Summary');
  });

  test('volunteer cannot access team member management', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await expectBlocked(page, '/Teams/volunteers/Members');
  });
});
