import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsFeedbackAdmin, expectBlocked } from '../helpers/auth';

// Feedback is retired (nobodies-collective/Humans#977): no creation path, no
// reporter-facing view, every screen AdminOnly. These tests pin the lockdown —
// the old US-27.1 submission-modal test went with the widget it exercised.
// Every screen being AdminOnly also means the positive path has no persona here
// since #1332; FeedbackPageRenderTests renders /Feedback as Admin in-process.
test.describe('Feedback (27-feedback-system, retired)', () => {
  test('no feedback submission modal is reachable from the help widget', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    await expect(page.locator('#feedbackWidgetModal')).toHaveCount(0);
    await expect(page.locator('button[data-bs-target="#feedbackWidgetModal"]')).toHaveCount(0);
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
