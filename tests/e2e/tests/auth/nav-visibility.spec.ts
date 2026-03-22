import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, loginAsBoard } from '../../helpers/auth';

test.describe('Nav Visibility by Role', () => {
  test('volunteer sees standard nav, no Admin or Board links', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/');

    const nav = page.locator('nav');

    // Should see standard links
    await expect(nav.getByRole('link', { name: 'Home' })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Teams' })).toBeVisible();

    // Should NOT see Admin link
    await expect(nav.getByRole('link', { name: 'Admin' })).not.toBeVisible();

    // Should NOT see Board link
    await expect(nav.getByRole('link', { name: 'Board' })).not.toBeVisible();
  });

  test('admin sees Admin link in nav', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/');

    const nav = page.locator('nav');
    await expect(nav.getByRole('link', { name: 'Admin' })).toBeVisible();
  });

  test('board member sees Board link but not Admin link', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/');

    const nav = page.locator('nav');

    // Board members see the Board nav link
    await expect(nav.getByRole('link', { name: 'Board' })).toBeVisible();

    // Board members do NOT see Admin link (admin-only)
    await expect(nav.getByRole('link', { name: 'Admin' })).not.toBeVisible();
  });
});
