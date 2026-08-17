import { test, expect } from '@playwright/test';
import { loginAsBoard, loginAsVolunteer, expectBlocked } from '../helpers/auth';

// No full-Admin persona here — see helpers/auth.ts. /Admin is AnyAdminRole, so Board
// reaches the dashboard; the AdminOnly pages this file used to cover (/Google/SyncSettings,
// /Debug/Configuration) are covered in-process by GoogleIntegrationPageRenderTests and
// DebugPageRenderTests.
test.describe('Admin (09-administration)', () => {
  test('US-9.1: admin dashboard loads with metrics cards', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Admin');
    await expect(page.locator('.alert-danger')).not.toBeVisible();
  });

  test('boundary: volunteer cannot access /Admin', async ({ page }) => {
    // Post #349 /Admin is gated by AnyAdminRole (composite of 12 admin-shaped
    // roles, including Board), so Board members reach the dashboard. Volunteer
    // is the closest non-admin role and the right boundary check.
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Admin');
  });
});
