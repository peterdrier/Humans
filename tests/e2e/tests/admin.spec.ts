import { test, expect } from '@playwright/test';
import { loginAsAdmin, loginAsVolunteer, expectBlocked } from '../helpers/auth';

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

  test('boundary: volunteer cannot access /Admin', async ({ page }) => {
    // Post #349 /Admin is gated by AnyAdminRole (composite of 12 admin-shaped
    // roles, including Board), so Board members reach the dashboard. Volunteer
    // is the closest non-admin role and the right boundary check.
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Admin');
  });
});
