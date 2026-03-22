import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('DevAuth Login', () => {
  test('volunteer can log in and see dashboard', async ({ page }) => {
    await loginAsVolunteer(page);

    // Nav dropdown is visible with user name
    const userNav = page.locator('[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)');
    await expect(userNav).toBeVisible();

    // Display name is shown (non-empty text in the dropdown toggle)
    const displayName = userNav.locator('.dropdown-toggle');
    await expect(displayName).not.toBeEmpty();

    // No server error banner
    await expect(page.locator('.alert-danger')).not.toBeVisible();

    // Page title is present (dashboard loaded)
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('page does not show server error', async ({ page }) => {
    await loginAsVolunteer(page);

    // Should not be on the error page
    expect(page.url()).not.toContain('/Error');
    await expect(page.locator('.alert-danger')).not.toBeVisible();
  });
});
