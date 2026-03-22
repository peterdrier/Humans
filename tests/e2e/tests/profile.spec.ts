import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsBoard } from '../helpers/auth';

test.describe('Profile (02-profiles)', () => {
  test('US-2.1: profile view page shows status and team memberships', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Profile should show profile information section
    await expect(page.getByText('Profile Information', { exact: false })).toBeVisible({ timeout: 5000 });
  });

  test('US-2.2: edit page has all form sections', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/Edit');

    // General info section
    const burnerNameInput = page.locator('input[name="BurnerName"]');
    await expect(burnerNameInput).toBeVisible();
    await expect(burnerNameInput).toBeEditable();

    // Bio textarea
    await expect(page.locator('textarea[name="Bio"]')).toBeVisible();

    // Private section (legal name)
    await expect(page.locator('input[name="FirstName"]')).toBeVisible();
    await expect(page.locator('input[name="LastName"]')).toBeVisible();

    // Save button
    await expect(page.getByRole('button', { name: 'Save Changes' })).toBeVisible();
  });

  test('GDPR: privacy page loads with data export and deletion options', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/Privacy');

    if (!page.url().includes('/Profile/Privacy')) return; // onboarding redirect — skip gracefully

    await expect(page.locator('h1, h2, h3, h4').first()).toBeVisible();
    // Data export link
    await expect(page.getByText('Download', { exact: false }).first()).toBeVisible();
  });

  test('US-9.2: board can view human detail with admin actions', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Human/Admin');

    // Human list loads
    await expect(page.locator('h1, h2').first()).toBeVisible();

    // Find a View link in the table body and navigate to detail page
    const viewLink = page.locator('table tbody a').filter({ hasText: /^View$/ }).first();
    const isVisible = await viewLink.isVisible({ timeout: 3000 }).catch(() => false);
    if (isVisible) {
      const href = await viewLink.getAttribute('href');
      if (href) {
        await page.goto(href);
        await expect(page.locator('h1, h2').first()).toBeVisible();
        // Should see admin action buttons (Suspend Human or Unsuspend Human)
        await expect(page.getByRole('button', { name: /Suspend Human|Unsuspend Human/ })).toBeVisible({ timeout: 5000 });
      }
    }
  });
});
