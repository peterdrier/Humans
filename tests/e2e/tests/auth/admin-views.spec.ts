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
    expect(page.url()).not.toMatch(/\/Admin\b/);
  });
});
