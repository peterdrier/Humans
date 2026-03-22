import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../../helpers/auth';

test.describe('Volunteer Boundaries', () => {
  test('volunteer cannot access admin dashboard', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Admin');

    // Should redirect away from admin
    expect(page.url()).not.toMatch(/\/Admin\b/);
  });

  test('volunteer cannot access sync settings', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Admin/SyncSettings');

    expect(page.url()).not.toContain('/SyncSettings');
  });

  test('volunteer cannot access team admin for arbitrary team', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/TeamAdmin/Members/volunteers');

    // Should get 404 or redirect — either way, not functional access
    const is404 = await page.getByText('Page Not Found').isVisible().catch(() => false);
    const isRedirected = !page.url().includes('/TeamAdmin/');

    expect(is404 || isRedirected).toBeTruthy();
  });
});
