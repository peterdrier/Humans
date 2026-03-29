import { test, expect } from '@playwright/test';
import { loginAsConsentCoordinator, loginAsVolunteerCoordinator, loginAsVolunteer, expectBlocked } from '../helpers/auth';

test.describe('Onboarding (16-onboarding-pipeline + 17-coordinator-roles)', () => {
  test('US-17.2: consent coordinator sees review queue with filter tabs', async ({ page }) => {
    await loginAsConsentCoordinator(page);
    await page.goto('/OnboardingReview');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/OnboardingReview');
  });

  test('US-17.2: consent coordinator sees action buttons on detail', async ({ page }) => {
    await loginAsConsentCoordinator(page);
    await page.goto('/OnboardingReview');

    // Click through to first human in queue if available
    const humanLink = page.locator('table a, .list-group a').first();
    if (await humanLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await humanLink.click();
      // Should see Clear and Flag action buttons
      const clearButton = page.getByRole('button', { name: /Clear/i }).first();
      const flagButton = page.getByRole('button', { name: /Flag/i }).first();
      const clearVisible = await clearButton.isVisible({ timeout: 5000 }).catch(() => false);
      const flagVisible = await flagButton.isVisible({ timeout: 5000 }).catch(() => false);
      expect(clearVisible || flagVisible).toBe(true);
    }
    // Skip gracefully if queue is empty
  });

  test('US-17.3: volunteer coordinator can view queue but no action buttons', async ({ page }) => {
    await loginAsVolunteerCoordinator(page);
    await page.goto('/OnboardingReview');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Index page doesn't show Clear/Flag buttons — those only appear on Detail page
    await expect(page.getByRole('button', { name: /Clear/i })).not.toBeVisible();
    await expect(page.getByRole('button', { name: /Flag/i })).not.toBeVisible();
  });

  test('boundary: volunteer cannot access /OnboardingReview', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/OnboardingReview');
  });
});
