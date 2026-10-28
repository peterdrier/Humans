import { test, expect } from '@playwright/test';
import { loginAs, expectBlocked } from '../helpers/auth';

test.describe('Camps (20-camps)', () => {
  test('US-20.1: anonymous can browse camps listing', async ({ page }) => {
    await page.goto('/Camps');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Camp cards or empty state should be visible
    expect(page.url()).not.toContain('/Error');
  });

  test('US-20.3: volunteer can access camp registration form', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto('/Camps/Register');

    // Either shows registration form or "no open season" message
    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).not.toContain('/Error');
  });

  test('US-20.6: camp admin can access admin dashboard', async ({ page }) => {
    await loginAs(page, 'camp-admin');
    await page.goto('/Camps/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Camps/Admin');
  });

  test('boundary: volunteer cannot access /Camps/Admin', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await expectBlocked(page, '/Camps/Admin');
  });
});
