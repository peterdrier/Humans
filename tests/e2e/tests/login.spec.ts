import { test, expect } from '@playwright/test';
import { loginAs } from '../helpers/auth';

test.describe('DevAuth Login', () => {
  test('volunteer can log in and see the dashboard', async ({ page }) => {
    await loginAs(page, 'volunteer');

    const userNav = page.locator('[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)');
    await expect(userNav).toBeVisible();
    await expect(userNav.locator('.dropdown-toggle')).not.toBeEmpty();

    expect(page.url()).not.toContain('/Error');
    await expect(page.locator('.alert-danger')).not.toBeVisible();
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});
