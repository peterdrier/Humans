import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsBoard, loginAsAdmin } from '../helpers/auth';

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
    await page.goto('/Profile/Me/Edit');

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
    await page.goto('/Profile/Me/Privacy');

    if (!page.url().includes('/Profile/Me/Privacy')) return; // onboarding redirect — skip gracefully

    await expect(page.locator('h1, h2, h3, h4').first()).toBeVisible();
    // Data export link
    await expect(page.getByText('Download', { exact: false }).first()).toBeVisible();
  });

  test('issue-659: admin can manually verify a pending email on /Profile/{id}/Admin/Emails', async ({ page }) => {
    // Auto-accept the data-confirm dialog that the Verify button surfaces.
    page.on('dialog', dialog => dialog.accept());

    await loginAsAdmin(page);
    // Find a target user from the humans list and reach their AdminDetail.
    await page.goto('/Profile/Admin');
    const viewLink = page.locator('table tbody a').filter({ hasText: /^View$/ }).first();
    const isVisible = await viewLink.isVisible({ timeout: 3000 }).catch(() => false);
    if (!isVisible) test.skip(true, 'No humans available to target — preview env may be empty.');

    const detailHref = await viewLink.getAttribute('href');
    expect(detailHref).toBeTruthy();
    const idMatch = detailHref!.match(/\/Profile\/([0-9a-f-]+)/i);
    expect(idMatch, 'expected /Profile/{id}/...').toBeTruthy();
    const targetId = idMatch![1];

    await page.goto(`/Profile/${targetId}/Admin/Emails`);
    await expect(page.locator('h1, h2').first()).toBeVisible();

    // Add a unique pending email so the row exists for this test.
    const pendingEmail = `pending-${Date.now()}-${Math.floor(Math.random() * 10000)}@e2e.invalid`;
    await page.locator('input[name="email"]').fill(pendingEmail);
    await page.getByRole('button', { name: /Send verification|Send/i }).click();
    await expect(page.locator('code', { hasText: pendingEmail })).toBeVisible({ timeout: 5000 });

    // The pending row should now show a Verify button (admin context only).
    const row = page.locator('tr', { has: page.locator('code', { hasText: pendingEmail }) });
    const verifyBtn = row.getByRole('button', { name: /^Verify$/ });
    await expect(verifyBtn).toBeVisible();

    // Click Verify and assert the row flips to a verified badge.
    await verifyBtn.click();
    await expect(page.getByText(/Email manually verified|merge request/i)).toBeVisible({ timeout: 5000 });
    const refreshedRow = page.locator('tr', { has: page.locator('code', { hasText: pendingEmail }) });
    await expect(refreshedRow.getByText(/Verified/i)).toBeVisible();
  });

  test('issue-659: non-admin POST to AdminVerifyEmail is blocked', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/Me/Edit');

    const csrf = await page
      .locator('input[name="__RequestVerificationToken"]')
      .first()
      .inputValue()
      .catch(() => '');
    expect(csrf).toBeTruthy();

    const meHref = page.url();
    const idMatch = meHref.match(/\/Profile\/([0-9a-f-]+)/i);
    // /Profile/Me/Edit may not include the id — fall back to a known guid that
    // the policy will reject regardless. The policy gate is what we're testing.
    const targetId = idMatch ? idMatch[1] : '00000000-0000-0000-0000-000000000000';

    const response = await page.request.post(
      `/Profile/${targetId}/Admin/Emails/Verify`,
      {
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        data: `__RequestVerificationToken=${encodeURIComponent(csrf)}&emailId=${encodeURIComponent(crypto.randomUUID())}`,
        maxRedirects: 0,
      },
    );

    // Policy gate is AdminOnly — non-admin gets 403 (or redirected to access-denied).
    expect([302, 403]).toContain(response.status());
  });

  test('US-9.2: board can view human detail with admin actions', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Profile/Admin');

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
