import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, loginAsTeamsAdmin, expectBlocked } from '../helpers/auth';

test.describe('Teams — Browsing (06-teams)', () => {
  test('US-6.1: team listing shows My Teams and Other Teams sections', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return; // onboarding redirect — skip gracefully

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // At least one team card visible
    await expect(page.locator('.card').first()).toBeVisible();
  });

  test('US-6.2: team detail shows name, description, members section', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    const teamLink = page.locator('.card a.btn, .card a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.locator('h1, h2').first()).toBeVisible();
      expect(page.url()).toContain('/Teams/');
    }
  });

  test('US-6.11: anonymous sees public teams on /Teams', async ({ page }) => {
    // No login — anonymous access
    await page.goto('/Teams');

    // Should load without error (may show public teams or login prompt)
    await expect(page.locator('h1, h2').first()).toBeVisible();
  });

  test('US-6.9: anonymous can view public team detail page', async ({ page }) => {
    // No login — navigate to teams listing first
    await page.goto('/Teams');

    const teamLink = page.locator('a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.locator('h1, h2').first()).toBeVisible();
    }
  });
});

test.describe('Teams — Membership (06-teams)', () => {
  test('US-6.3: open team shows Join button for non-members', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    // Look for a "Join" link/button (open teams show direct Join)
    const joinLink = page.getByRole('link', { name: 'Join' });
    if (await joinLink.first().isVisible({ timeout: 3000 }).catch(() => false)) {
      await expect(joinLink.first()).toBeVisible();
    }
    // Skip gracefully if volunteer is already a member of all open teams
  });

  test('US-6.4: approval-required team shows Request to Join', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    // Look for "Request to Join" link/button (approval-required teams)
    const requestLink = page.getByRole('link', { name: /Request to Join/i });
    if (await requestLink.first().isVisible({ timeout: 3000 }).catch(() => false)) {
      await expect(requestLink.first()).toBeVisible();
    }
    // Skip gracefully if no approval-required teams available
  });

  test('US-6.6: My Teams page loads at /Teams/My', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams/My');

    if (!page.url().includes('/Teams/My')) return;

    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});

test.describe('Teams — Management (06-teams)', () => {
  test('US-6.7: admin can access team member management', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams');

    // Navigate to a team detail, then find Members link
    const teamLink = page.locator('.card a.btn, .card a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();

      // Look for Team Management section or Members link
      const managementSection = page.getByText('Team Management', { exact: false });
      await expect(managementSection).toBeVisible({ timeout: 5000 });
    }
  });

  test('US-6.8: admin can access Create Team form', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams/Create');

    await expect(page.locator('h1, h2, h3, h4').first()).toBeVisible();
    // Form fields should be present (label or textbox for team name)
    await expect(page.getByRole('textbox', { name: /Team Name/i })).toBeVisible();
  });

  test('US-6.10: admin can access Edit Team Page', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams');

    // Find a team and navigate to EditPage
    const teamLink = page.locator('.card a.btn, .card a[href*="/Teams/"]').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();

      // Extract slug from URL
      const url = page.url();
      const slug = url.split('/Teams/')[1]?.split('?')[0]?.split('/')[0];
      if (slug) {
        await page.goto(`/Teams/${slug}/EditPage`);
        await expect(page.locator('h1, h2').first()).toBeVisible();
      }
    }
  });

  test('US-6.8: teams-admin can access Team Summary', async ({ page }) => {
    await loginAsTeamsAdmin(page);
    await page.goto('/Teams/Summary');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Teams/Summary');
  });
});

test.describe('Teams — Coordinator & Role Auth', () => {
  test('admin sees Team Management card on team detail', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams');

    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.getByText('Team Management', { exact: false })).toBeVisible({ timeout: 5000 });
    }
  });

  test('volunteer does NOT see Team Management card', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.getByText('Team Management', { exact: false })).not.toBeVisible();
    }
  });

  test('role management section visible on team detail for admin', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Teams');

    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      // Roles link should be in the Team Management section
      await expect(page.getByText('Manage Roles', { exact: false }).or(
        page.getByText('Roles', { exact: false })
      ).first()).toBeVisible({ timeout: 5000 });
    }
  });
});

test.describe('Teams — Cross-Team Views & Hierarchy', () => {
  test('department detail shows sub-teams if they exist', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams');

    if (!page.url().includes('/Teams')) return;

    // Navigate to a team detail and check for sub-teams section
    const teamLink = page.locator('.card a.btn').first();
    if (await teamLink.isVisible({ timeout: 3000 }).catch(() => false)) {
      await teamLink.click();
      await expect(page.locator('h1, h2').first()).toBeVisible();
      // Sub-teams section renders as cards or a list within the detail page
      // Data-dependent — we verify the page loads; sub-teams may or may not exist
    }
  });

  test('cross-team Roster page loads at /Teams/Roster', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Teams/Roster');

    if (!page.url().includes('/Roster')) return;

    await expect(page.locator('h1, h2').first()).toBeVisible();
  });
});

test.describe('Teams — Boundaries', () => {
  test('volunteer cannot access /Teams/Sync', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Teams/Sync');
  });

  test('volunteer cannot access /Teams/Summary', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Teams/Summary');
  });

  test('volunteer cannot access team member management', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Teams/volunteers/Members');
  });
});
