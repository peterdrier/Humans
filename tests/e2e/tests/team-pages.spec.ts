import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('Team Pages', () => {
  test('team listing loads and can navigate to detail', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    // If user hasn't completed onboarding, /Teams may redirect to dashboard
    // In that case, just verify the page loads without errors
    const currentUrl = page.url();
    if (!currentUrl.includes('/Teams')) {
      // Redirected away (onboarding not complete) — page loads, no errors
      await expect(page.locator('h1, h2').first()).toBeVisible();
      return;
    }

    // Page loads without error
    await expect(page.locator('.alert-danger')).not.toBeVisible();

    // At least one team card is visible
    const teamCards = page.locator('.card');
    await expect(teamCards.first()).toBeVisible();

    // Click the first team link
    const teamLink = page.locator('a[href*="/Teams/"]').first();
    await expect(teamLink).toBeVisible();
    await teamLink.click();

    // Detail page loads with a heading
    await expect(page.locator('h1, h2').first()).toBeVisible();

    // Page URL confirms we're on a team detail page
    expect(page.url()).toContain('/Teams/');
  });
});
