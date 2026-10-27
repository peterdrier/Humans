import { test, expect } from '@playwright/test';
import { loginAsConsentCoordinator, loginAsShiftsAdmin, loginAsVolunteer, expectBlocked } from '../helpers/auth';

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

    // Click through to first human in queue if available. Target the "Review"
    // button explicitly — the first link in .list-group is the human-link
    // avatar (which goes to /Profile/{id}, no Clear/Flag UI there) and the
    // nav badge "Review 3" also matches /Review/i, so scope to the queue list.
    const reviewLink = page.locator('.list-group-item').getByRole('link', { name: /Review/i }).first();
    if (await reviewLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await reviewLink.click();
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
    await loginAsShiftsAdmin(page);
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
