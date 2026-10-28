import { test, expect } from '@playwright/test';
import { loginAs } from '../helpers/auth';

test.describe('Profile (02-profiles)', () => {
  test('US-2.1: profile view page shows status and team memberships', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto('/Profile');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // Profile should show profile information section
    await expect(page.getByText('Profile Information', { exact: false })).toBeVisible({ timeout: 5000 });
  });

  test('US-2.2: edit page has all form sections', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto('/Profile/Me/Edit');

    // General info section
    const burnerNameInput = page.locator('input[name="BurnerName"]');
    await expect(burnerNameInput).toBeVisible();
    await expect(burnerNameInput).toBeEditable();

    // Bio field. MarkdownEditorTagHelper upgrades it to an EasyMDE editor, which
    // hides the underlying <textarea> and renders its own editing surface — so
    // assert both: the field that actually posts still exists, and the editor the
    // user types into is visible.
    await expect(page.locator('textarea[name="Bio"]')).toBeAttached();
    await expect(page.locator('.EasyMDEContainer .CodeMirror')).toBeVisible();

    // Private section (legal name)
    await expect(page.locator('input[name="FirstName"]')).toBeVisible();
    await expect(page.locator('input[name="LastName"]')).toBeVisible();

    // Save button
    await expect(page.getByRole('button', { name: 'Save Changes' })).toBeVisible();
  });

  test('GDPR: privacy page loads with data export and deletion options', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto('/Consent/MyData');

    expect(new URL(page.url()).pathname).toBe('/Consent/MyData');
    await expect(page.locator('h1, h2, h3, h4').first()).toBeVisible();
    // Data export link
    await expect(page.getByText('Download', { exact: false }).first()).toBeVisible();
  });

  // issue-659's positive case lives in EmailGridFlowTests instead: the Verify button and
  // the action behind it are both AdminOnly, so HumanAdmin/Board reach the page without
  // them and there is no E2E persona left for it since #1332. The negative half stays —
  // a volunteer still serves.
  test('issue-659: non-admin POST to AdminVerifyEmail is blocked', async ({ page }) => {
    await loginAs(page, 'volunteer');
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
    await loginAs(page, 'board');
    await page.goto('/Users/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();

    // The list always has rows: the dev personas themselves are humans on every host.
    const viewLink = page.locator('.list-group-item a.btn').filter({ hasText: /^\s*View\s*$/ }).first();
    await page.goto((await viewLink.getAttribute('href'))!);
    await expect(page.getByRole('button', { name: /Suspend Human|Unsuspend Human/ })).toBeVisible();
  });
});
