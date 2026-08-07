import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, loginAsFeedbackAdmin, expectBlocked } from '../helpers/auth';

// Feedback is retired (nobodies-collective/Humans#977): no creation path, no
// reporter-facing view, every screen AdminOnly. These tests pin the lockdown —
// the old US-27.1 submission-modal test went with the widget it exercised.
test.describe('Feedback (27-feedback-system, retired)', () => {
  test('no feedback submission modal is reachable from the help widget', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    await expect(page.locator('#feedbackWidgetModal')).toHaveCount(0);
    await expect(page.locator('button[data-bs-target="#feedbackWidgetModal"]')).toHaveCount(0);
  });

  test('admin feedback queue loads', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Feedback');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Feedback');
  });

  test('boundary: volunteer cannot access /Feedback', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Feedback');
  });

  test('boundary: FeedbackAdmin alone cannot access /Feedback', async ({ page }) => {
    await loginAsFeedbackAdmin(page);
    await expectBlocked(page, '/Feedback');
  });
});
