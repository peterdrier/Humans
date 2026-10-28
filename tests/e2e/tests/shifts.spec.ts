import { test, expect } from '@playwright/test';
import { loginAs, postWithCsrf } from '../helpers/auth';

test.describe('Shifts (25-shift-management)', () => {
  test('US-25.3: browse shifts page loads', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto('/Shifts');

    // Page loads — may show shifts or "browsing closed" info alert.
    // Both Shifts pages are tab-based and render no visible h1/h2: the only h2 in
    // the DOM is inside the collapsed help panel (SectionHelpContent "How Shifts
    // Work"), which is hidden, so `locator('h1, h2').first()` could never pass.
    // Assert the tab that identifies the page instead.
    const browseTab = page.getByRole('tab', { name: 'Browse Volunteer Options' });
    await expect(browseTab, 'needs an active event with Shift Browsing Open on the target host').toBeVisible();
    await expect(browseTab).toHaveAttribute('aria-selected', 'true');
    expect(page.url()).not.toContain('/Error');
  });

  test('US-25.5: My Shifts page loads at /Shifts/Mine', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await page.goto('/Shifts/Mine');

    // See above — assert the selected tab, not a heading.
    const mineTab = page.getByRole('tab', { name: /^My Shifts\b/ });
    await expect(mineTab).toBeVisible();
    await expect(mineTab).toHaveAttribute('aria-selected', 'true');
  });

  // The shift knobs are edited on the Settings admin page and POSTed here (AdminOnly). The
  // positive case has no E2E persona since #1332; the boundary is the POST.
  test('boundary: volunteer cannot POST /Shifts/Settings', async ({ page }) => {
    await loginAs(page, 'volunteer');
    const response = await postWithCsrf(page, '/Shifts/Settings', 'IsShiftBrowsingOpen=true');
    expect([302, 403]).toContain(response.status());
    if (response.status() === 302) {
      expect(response.headers()['location'] ?? '').toContain('/Account/AccessDenied');
    }
  });
});
