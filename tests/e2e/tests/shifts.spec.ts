import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin, expectBlocked } from '../helpers/auth';

test.describe('Shifts (25-shift-management)', () => {
  test('US-25.3: browse shifts page loads', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Shifts');

    // Page loads — may show shifts or "browsing closed" info alert.
    // Both Shifts pages are tab-based and render no visible h1/h2: the only h2 in
    // the DOM is inside the collapsed help panel (SectionHelpContent "How Shifts
    // Work"), which is hidden, so `locator('h1, h2').first()` could never pass.
    // Assert the tab that identifies the page instead.
    const browseTab = page.getByRole('tab', { name: 'Browse Volunteer Options' });
    await expect(browseTab).toBeVisible();
    await expect(browseTab).toHaveAttribute('aria-selected', 'true');
    expect(page.url()).not.toContain('/Error');
  });

  test('US-25.5: My Shifts page loads at /Shifts/Mine', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Shifts/Mine');

    // See above — assert the selected tab, not a heading.
    const mineTab = page.getByRole('tab', { name: /^My Shifts\b/ });
    await expect(mineTab).toBeVisible();
    await expect(mineTab).toHaveAttribute('aria-selected', 'true');
  });

  test('US-25.1: shift settings page loads for admin', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Shifts/Settings');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Settings');
  });

  test('boundary: volunteer cannot access /Shifts/Settings', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Shifts/Settings');
  });
});
