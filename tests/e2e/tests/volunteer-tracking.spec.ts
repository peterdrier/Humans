import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsVolunteerCoordinator, expectBlocked } from '../helpers/auth';

/**
 * Volunteer Tracking E2E (feature 47).
 *
 * NOTE — these tests are blocked on local Playwright/Docker setup at the time
 * of authoring. The Docker daemon is reachable but the user lacks the socket
 * permission needed to spin up the Postgres container that the BASE_URL preview
 * environment depends on, and the dev seeder has not yet been extended with
 * the deterministic gap / unbooked / camp-set-up fixtures these flows assert
 * against. They are intentionally `test.fixme()`'d so the file lands in the
 * tree (and in CI's discovery output) without false-failing on either the env
 * gap or the seed gap. Unblock by:
 *
 *   1. Add `volunteer-tracking` cohort to `DevelopmentDashboardSeeder`:
 *      - one volunteer with a Confirmed signup spanning days [-21..-15] and a
 *        gap on day -10 (no signup, not blocked, not on camp set-up).
 *      - one volunteer with `EventParticipation.Status = Attending` and
 *        `GeneralAvailability.AvailableDayOffsets = [-20..-1]` and zero signups.
 *      - one volunteer with a Confirmed signup spanning the whole build period
 *        (used by the self-service flow — they need at least one signup so
 *        the "Days you can't volunteer" panel renders).
 *      Expose them with stable display names so the selectors below resolve.
 *   2. Ensure Docker daemon is reachable (or run against an existing preview
 *      env via `BASE_URL=https://{pr}.n.burn.camp`). Drop the `test.fixme()`.
 */

test.describe('Volunteer Tracking (feature 47)', () => {
  test('boundary: volunteer cannot access /ShiftDashboard/VolunteerTracking', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/ShiftDashboard/VolunteerTracking');
  });

  test('US-47.1 / US-47.3: VC marks camp set-up, blocks/unblocks day, sees unbooked cohort', async ({ page }) => {
    test.fixme(true, 'blocked on local Playwright/Docker setup + dev seeder extension');

    await loginAsVolunteerCoordinator(page);

    // 1. Navigate Shifts → Shift Dashboard → Volunteer Tracking.
    await page.goto('/Shifts');
    await page.getByRole('link', { name: /shift dashboard/i }).click();
    await page.getByRole('link', { name: /volunteer tracking/i }).click();
    await expect(page).toHaveURL(/\/ShiftDashboard\/VolunteerTracking/);
    await expect(page.getByRole('heading', { name: /volunteer tracking/i })).toBeVisible();

    // 2. Locate seeded volunteer with a known gap.
    const gapRow = page.locator('tr', { hasText: /VolTrack-Gap-Volunteer/i });
    await expect(gapRow).toBeVisible();
    const gapBadgeBefore = gapRow.locator('.badge.bg-danger');
    await expect(gapBadgeBefore).toContainText(/gaps/i);
    const gapsBefore = parseInt((await gapBadgeBefore.textContent())?.match(/\d+/)?.[0] ?? '0', 10);
    expect(gapsBefore).toBeGreaterThan(0);

    // 3. Click the gap cell, then "Mark went to camp set-up from this day".
    const gapCell = gapRow.locator('td.vt-cell.vt-cell-gap').first();
    await gapCell.click();
    await page.getByRole('button', { name: /mark went to camp set-up/i }).click();

    // Row should turn blue from that day; gap badge updates or vanishes.
    await expect(gapRow.locator('td.vt-cell.vt-cell-camp-setup').first()).toBeVisible();
    const gapBadgeAfter = gapRow.locator('.badge.bg-danger');
    if (await gapBadgeAfter.count() > 0) {
      const gapsAfter = parseInt((await gapBadgeAfter.textContent())?.match(/\d+/)?.[0] ?? '0', 10);
      expect(gapsAfter).toBeLessThan(gapsBefore);
    }

    // 4. Clear set-up date — row reverts.
    const blueCell = gapRow.locator('td.vt-cell.vt-cell-camp-setup').first();
    await blueCell.click();
    await page.getByRole('button', { name: /clear set-up date/i }).click();
    await expect(gapRow.locator('td.vt-cell.vt-cell-camp-setup')).toHaveCount(0);

    // 5. Block an empty-window cell (an "Expected" or "Outside" cell).
    const emptyCell = gapRow.locator('td.vt-cell.vt-cell-gap').nth(1);
    await emptyCell.click();
    await page.getByRole('button', { name: /^block this day$/i }).click();
    await expect(emptyCell).toHaveClass(/vt-cell-blocked/);

    // Gap count drops by 1.
    const gapBadgeAfterBlock = gapRow.locator('.badge.bg-danger');
    const gapsAfterBlock = (await gapBadgeAfterBlock.count()) > 0
      ? parseInt((await gapBadgeAfterBlock.textContent())?.match(/\d+/)?.[0] ?? '0', 10)
      : 0;
    expect(gapsAfterBlock).toBe(gapsBefore - 1);

    // 6. Unblock — cell reverts to red.
    await emptyCell.click();
    await page.getByRole('button', { name: /^unblock this day$/i }).click();
    await expect(emptyCell).toHaveClass(/vt-cell-gap/);

    // 7. Scroll to "Declared participating, not booked yet" section.
    await page.getByRole('heading', { name: /declared participating, not booked yet/i }).scrollIntoViewIfNeeded();
    const unbookedSection = page.locator('section', {
      has: page.getByRole('heading', { name: /declared participating, not booked yet/i }),
    });
    await expect(unbookedSection.locator('tr', { hasText: /VolTrack-Unbooked-Volunteer/i })).toBeVisible();
  });

  test('US-47.2: volunteer self-blocks days, VC sees them yellow', async ({ page, browser }) => {
    test.fixme(true, 'blocked on local Playwright/Docker setup + dev seeder extension');

    // 1. Sign in as a regular volunteer with a build-period signup.
    await loginAsVolunteer(page);
    await page.goto('/Shifts/Mine');

    const blockedPanel = page.locator('section', {
      has: page.getByRole('heading', { name: /days you can't volunteer/i }),
    });
    await expect(blockedPanel).toBeVisible();

    // 2. Toggle two day-offset checkboxes and submit.
    const checkboxes = blockedPanel.locator('input[type="checkbox"][name="DayOffsets"]');
    const firstCb = checkboxes.nth(0);
    const secondCb = checkboxes.nth(1);
    const firstOffset = await firstCb.getAttribute('value');
    const secondOffset = await secondCb.getAttribute('value');
    await firstCb.check();
    await secondCb.check();

    await blockedPanel.getByRole('button', { name: /save blocked days/i }).click();

    // 3. TempData success message + persistence on reload.
    await expect(page.getByText(/blocked days saved/i)).toBeVisible();
    await page.reload();
    await expect(page.locator(`input[type="checkbox"][value="${firstOffset}"]`)).toBeChecked();
    await expect(page.locator(`input[type="checkbox"][value="${secondOffset}"]`)).toBeChecked();

    // 4. Sign in as VC in a fresh context, navigate to tracking, confirm yellow cells.
    const vcContext = await browser.newContext();
    const vcPage = await vcContext.newPage();
    await loginAsVolunteerCoordinator(vcPage);
    await vcPage.goto('/ShiftDashboard/VolunteerTracking');

    // The same volunteer should have yellow (blocked) cells for the two offsets.
    const volunteerRow = vcPage.locator('tr', { hasText: /VolTrack-SelfBlock-Volunteer/i });
    await expect(volunteerRow).toBeVisible();
    const blockedCells = volunteerRow.locator('td.vt-cell.vt-cell-blocked');
    await expect(blockedCells).toHaveCount(2);

    await vcContext.close();
  });
});
