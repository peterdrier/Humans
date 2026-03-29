import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, expectBlocked } from '../helpers/auth';

test.describe('Shifts (25-shift-management)', () => {
  test('US-25.3: browse shifts page loads', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Shifts');

    // Page loads — may show shifts or "browsing closed" info alert
    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).not.toContain('/Error');
  });

  test('US-25.5: My Shifts page loads at /Shifts/Mine', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Shifts/Mine');

    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('US-25.1: shift settings page loads for admin', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Shifts/Settings');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Settings');
  });

  test('boundary: volunteer cannot access /Shifts/Settings', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Shifts/Settings');
  });

  test('boundary: volunteer cannot access /Vol/Management', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Vol/Management');
  });
});
