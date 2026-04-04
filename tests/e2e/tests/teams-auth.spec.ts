import { test, expect } from '@playwright/test';
import {
  loginAsCoordinator,
  loginAsTeamsAdmin,
  loginAsAdmin,
  loginAsBoard,
  loginAsVolunteer,
  expectBlocked,
  postWithCsrf,
} from '../helpers/auth';

/**
 * Team join request authorization tests (#341).
 *
 * These tests verify the authorization boundaries around the Members page
 * (Teams/{slug}/Members) which is the gateway for approving/rejecting
 * join requests and managing team membership.
 *
 * The "coordinator" dev persona owns "Dev Test Department" (slug: dev-test-department)
 * and its sub-team "Dev Test SubTeam" (slug: dev-test-subteam). The coordinator
 * does NOT coordinate any other department.
 */

const DEPT_SLUG = 'dev-test-department';
const SUBTEAM_SLUG = 'dev-test-subteam';
const FAKE_REQUEST_ID = '00000000-0000-0000-0000-000000000000';

test.describe('Teams Auth — Coordinator positive cases (#341)', () => {
  test('coordinator can access Members page for their department', async ({ page }) => {
    await loginAsCoordinator(page);
    await page.goto(`/Teams/${DEPT_SLUG}/Members`);

    // Should NOT be blocked — coordinator owns this department
    expect(page.url()).toContain(`/Teams/${DEPT_SLUG}/Members`);
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('coordinator can access Members page for sub-team of their department', async ({ page }) => {
    await loginAsCoordinator(page);
    await page.goto(`/Teams/${SUBTEAM_SLUG}/Members`);

    // Should NOT be blocked — coordinator of parent department has access
    expect(page.url()).toContain(`/Teams/${SUBTEAM_SLUG}/Members`);
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});

test.describe('Teams Auth — Coordinator negative cases (#341)', () => {
  test('coordinator cannot access Members page for another department', async ({ page }) => {
    await loginAsCoordinator(page);

    // "volunteers" is a system team the coordinator does NOT coordinate
    await expectBlocked(page, '/Teams/volunteers/Members');
  });
});

test.describe('Teams Auth — Coordinator POST cases (#341)', () => {
  test('coordinator can POST approve on their department (auth passes)', async ({ page }) => {
    await loginAsCoordinator(page);
    await page.goto(`/Teams/${DEPT_SLUG}/Members`);

    // POST with valid CSRF token — auth check should pass (request not found is OK)
    const response = await postWithCsrf(
      page,
      `/Teams/${DEPT_SLUG}/Requests/${FAKE_REQUEST_ID}/Approve`,
      'Notes=test',
    );

    // Auth passed if we get anything other than 401/403. A 302 (redirect) or 404
    // (request not found) both prove the authorization layer allowed the action.
    expect([401, 403]).not.toContain(response.status());
  });

  test('coordinator cannot POST approve on another department', async ({ page }) => {
    await loginAsCoordinator(page);
    // Navigate to own department first to get a valid CSRF token
    await page.goto(`/Teams/${DEPT_SLUG}/Members`);

    const response = await postWithCsrf(
      page,
      `/Teams/volunteers/Requests/${FAKE_REQUEST_ID}/Approve`,
      'Notes=test',
    );

    // Should be blocked by authorization — coordinator doesn't own this team
    expect([302, 403]).toContain(response.status());
    if (response.status() === 302) {
      const location = response.headers()['location'] ?? '';
      expect(location).not.toContain('/Members');
    }
  });
});

test.describe('Teams Auth — TeamsAdmin and Admin positive cases (#341)', () => {
  test('TeamsAdmin can access Members page for any team', async ({ page }) => {
    await loginAsTeamsAdmin(page);
    await page.goto(`/Teams/${DEPT_SLUG}/Members`);

    expect(page.url()).toContain(`/Teams/${DEPT_SLUG}/Members`);
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('Admin can access Members page for any team', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto(`/Teams/${DEPT_SLUG}/Members`);

    expect(page.url()).toContain(`/Teams/${DEPT_SLUG}/Members`);
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('Board can access Members page for any team', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto(`/Teams/${DEPT_SLUG}/Members`);

    expect(page.url()).toContain(`/Teams/${DEPT_SLUG}/Members`);
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});

test.describe('Teams Auth — Volunteer negative cases (#341)', () => {
  test('volunteer cannot access Members page for any team', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, `/Teams/${DEPT_SLUG}/Members`);
  });

  test('volunteer cannot POST approve action (with valid CSRF)', async ({ page }) => {
    await loginAsVolunteer(page);
    // Navigate to home page to get a valid CSRF token (volunteers can't access Members)
    await page.goto('/');

    const response = await postWithCsrf(
      page,
      `/Teams/${DEPT_SLUG}/Requests/${FAKE_REQUEST_ID}/Approve`,
      'Notes=test',
    );

    // Should be blocked by authorization, not just CSRF validation
    expect([302, 401, 403]).toContain(response.status());
  });

  test('volunteer cannot POST reject action (with valid CSRF)', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/');

    const response = await postWithCsrf(
      page,
      `/Teams/${DEPT_SLUG}/Requests/${FAKE_REQUEST_ID}/Reject`,
      'Notes=test+reason',
    );

    expect([302, 401, 403]).toContain(response.status());
  });
});

test.describe('Teams Auth — Management UI verification (#341)', () => {
  /**
   * Verifies the Members page shows management UI controls (add/remove) that
   * trigger audit-logged service methods. Direct audit log verification is
   * deferred — there is no public API endpoint or UI for non-admin audit access.
   */
  test('admin Members page shows management controls that trigger audited actions', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto(`/Teams/${DEPT_SLUG}/Members`);

    // The Members page should load successfully with management capabilities
    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Members');

    // Verify at least one management control is present (add or remove)
    // These actions trigger audit-logged service methods
    const hasAddMember = await page
      .locator('[data-testid="add-member"], input[name="q"], .add-member-search, #member-search')
      .first()
      .isVisible({ timeout: 3000 })
      .catch(() => false);

    const hasRemoveButton = await page
      .locator('button:has-text("Remove"), a:has-text("Remove"), form[action*="Remove"]')
      .first()
      .isVisible({ timeout: 3000 })
      .catch(() => false);

    expect(hasAddMember || hasRemoveButton).toBeTruthy();
  });
});
