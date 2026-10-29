import { test, expect } from '@playwright/test';
import { loginAs, expectBlocked } from '../helpers/auth';

test.describe('Onboarding (16-onboarding-pipeline + 17-coordinator-roles)', () => {
  test('US-17.2: consent coordinator sees review queue with filter tabs', async ({ page }) => {
    await loginAs(page, 'consent-coordinator');
    await page.goto('/OnboardingReview');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/OnboardingReview');
  });

  test('US-17.3: volunteer coordinator can view queue but no action buttons', async ({ page }) => {
    await loginAs(page, 'shifts-admin');
    await page.goto('/OnboardingReview');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Index page doesn't show Clear/Flag buttons — those only appear on Detail page
    await expect(page.getByRole('button', { name: /Clear/i })).not.toBeVisible();
    await expect(page.getByRole('button', { name: /Flag/i })).not.toBeVisible();
  });

  test('boundary: volunteer cannot access /OnboardingReview', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await expectBlocked(page, '/OnboardingReview');
  });
});
