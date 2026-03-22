import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsCampAdmin, expectBlocked } from '../helpers/auth';

test.describe('Camps (20-camps)', () => {
  test('US-20.1: anonymous can browse camps listing', async ({ page }) => {
    await page.goto('/Camps');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Camp cards or empty state should be visible
    expect(page.url()).not.toContain('/Error');
  });

  test('US-20.2: anonymous can view camp detail', async ({ page }) => {
    await page.goto('/Camps');

    // Find a link that points to a camp detail page specifically
    const campLink = page.locator('a[href*="/Camps/"]').first();
    if (await campLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await campLink.click();
      await expect(page.locator('h1, h2').first()).toBeVisible();
      expect(page.url()).toContain('/Camps/');
    }
  });

  test('US-20.3: volunteer can access camp registration form', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Camps/Register');

    // Either shows registration form or "no open season" message
    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).not.toContain('/Error');
  });

  test('US-20.6: camp admin can access admin dashboard', async ({ page }) => {
    await loginAsCampAdmin(page);
    await page.goto('/Camps/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Camps/Admin');
  });

  test('boundary: volunteer cannot access /Camps/Admin', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Camps/Admin');
  });
});
