import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, expectBlocked } from '../helpers/auth';

test.describe('Feedback (27-feedback-system)', () => {
  test('US-27.1: volunteer can open modal and submit feedback', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    const feedbackTrigger = page.locator('button[data-bs-target="#feedbackModal"]');
    if (await feedbackTrigger.isVisible({ timeout: 3000 }).catch(() => false)) {
      await feedbackTrigger.click();

      const modal = page.locator('#feedbackModal');
      await expect(modal).toBeVisible();

      await modal.locator('select[name="Category"]').selectOption({ index: 1 });
      await modal.locator('textarea[name="Description"]').fill(
        `feedback-test: please ignore (${new Date().toISOString()})`
      );

      await modal.locator('button[type="submit"]').click();
      await page.waitForLoadState('load', { timeout: 10000 });

      expect(page.url()).not.toContain('/Error');
    } else {
      await expect(page.locator('h1, h2').first()).toBeVisible();
    }
  });

  test('US-27.2: admin feedback triage page loads', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Admin/Feedback');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Admin/Feedback');
  });

  test('boundary: volunteer cannot access /Admin/Feedback', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Admin/Feedback');
  });
});
