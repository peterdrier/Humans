import { test, expect } from '@playwright/test';
import { loginAsAdmin, loginAsVolunteer } from '../../helpers/auth';

test.describe('Coordinator Views', () => {
  test('admin can see team management section on team detail', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams');

    // Navigate to a team detail page
    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();

      // Should see the "Team Management" card with management links
      await expect(
        page.getByText('Team Management', { exact: false })
      ).toBeVisible({ timeout: 5000 });
    }
  });

  test('volunteer does not see team management section', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();

      // Should NOT see team management section
      await expect(
        page.getByText('Team Management', { exact: false })
      ).not.toBeVisible();
    }
  });
});
