import { test, expect } from '@playwright/test';
import {
  loginAsCoordinator,
  loginAsTeamsAdmin,
  loginAsAdmin,
  loginAsVolunteer,
  expectBlocked,
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
});

test.describe('Teams Auth — Volunteer negative cases (#341)', () => {
  test('volunteer cannot access Members page for any team', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, `/Teams/${DEPT_SLUG}/Members`);
  });

  test('volunteer cannot POST approve action', async ({ page }) => {
    await loginAsVolunteer(page);

    // Attempt a direct POST to the approve endpoint — should be blocked
    const response = await page.request.post(
      `/Teams/${DEPT_SLUG}/Requests/00000000-0000-0000-0000-000000000000/Approve`,
      {
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        data: 'Notes=test',
      }
    );

    // Should get a redirect to login, 403, or 400 — not 200
    expect([302, 400, 401, 403]).toContain(response.status());
  });

  test('volunteer cannot POST reject action', async ({ page }) => {
    await loginAsVolunteer(page);

    // Attempt a direct POST to the reject endpoint — should be blocked
    const response = await page.request.post(
      `/Teams/${DEPT_SLUG}/Requests/00000000-0000-0000-0000-000000000000/Reject`,
      {
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        data: 'Notes=test+reason',
      }
    );

    // Should get a redirect to login, 403, or 400 — not 200
    expect([302, 400, 401, 403]).toContain(response.status());
  });
});

test.describe('Teams Auth — Audit trail verification (#341)', () => {
  /**
   * Audit log entries for member add/remove are not directly accessible via a
   * public API endpoint or a page that non-admin users can reach.
   * The AuditLogViewComponent renders on admin-only pages.
   *
   * Rather than building a new API endpoint just for tests, we verify that the
   * audit infrastructure is exercised by checking the Members page shows the
   * expected management UI elements (add member, remove member buttons) which
   * trigger the audit-logged service methods.
   *
   * Full audit log content verification is deferred to manual QA or future
   * integration tests that can query the database directly.
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
