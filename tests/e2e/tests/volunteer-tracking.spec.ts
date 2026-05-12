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
  test('boundary: volunteer cannot access /Shifts/Dashboard/VolunteerTracking', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Shifts/Dashboard/VolunteerTracking');
  });

  test('US-47.1 / US-47.3: VC marks camp set-up, blocks/unblocks day, sees unbooked cohort', async ({ page }) => {
    test.fixme(true, 'blocked on local Playwright/Docker setup + dev seeder extension');

    await loginAsVolunteerCoordinator(page);

    // 1. Navigate Shifts → Shift Dashboard → Volunteer Tracking.
    await page.goto('/Shifts');
    await page.getByRole('link', { name: /shift dashboard/i }).click();
    await page.getByRole('link', { name: /volunteer tracking/i }).click();
    await expect(page).toHaveURL(/\/Shifts\/Dashboard\/VolunteerTracking/);
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

  test('Day-off: VC marks day off via popover, cell renders striped-grey, gap count drops', async ({ page }) => {
    test.fixme(true, 'blocked on dev seeder extension — needs a VolTrack-Gap volunteer with at least one Gap cell on the build window so the Mark-day-off form renders');

    await loginAsVolunteerCoordinator(page);
    await page.goto('/Shifts/Dashboard/VolunteerTracking');
    await expect(page.getByRole('heading', { name: /volunteer tracking/i })).toBeVisible();

    const gapRow = page.locator('tr', { hasText: /VolTrack-Gap-Volunteer/i });
    await expect(gapRow).toBeVisible();
    const gapBadgeBefore = gapRow.locator('.badge.bg-danger').first();
    const gapsBefore = parseInt((await gapBadgeBefore.textContent())?.match(/\d+/)?.[0] ?? '0', 10);
    expect(gapsBefore).toBeGreaterThan(0);

    // Open the popover on a gap cell and submit the Mark-day-off form.
    const gapCell = gapRow.locator('td.vt-cell.bg-danger').first();
    await gapCell.locator('button[data-vt-popover]').click();
    await page.locator('input[name="Reason"]').fill('doctor');
    await page.getByRole('button', { name: /mark day off/i }).click();

    // After redirect: that cell now renders with the day-off striped-grey class
    // and gap count drops by one (day-offs do not count as gaps).
    await expect(gapRow.locator('td.vt-cell.vt-dayoff').first()).toBeVisible();
    const gapBadgeAfter = gapRow.locator('.badge.bg-danger').first();
    const gapsAfter = (await gapBadgeAfter.count()) > 0
      ? parseInt((await gapBadgeAfter.textContent())?.match(/\d+/)?.[0] ?? '0', 10)
      : 0;
    expect(gapsAfter).toBe(gapsBefore - 1);
  });

  test('Day-off: VC opens popover on confirmed signup cell, sees blocked message and no Mark button', async ({ page }) => {
    test.fixme(true, 'blocked on dev seeder extension — needs a VolTrack-Confirmed volunteer with at least one Confirmed signup on the build window');

    await loginAsVolunteerCoordinator(page);
    await page.goto('/Shifts/Dashboard/VolunteerTracking');

    const confirmedRow = page.locator('tr', { hasText: /VolTrack-Confirmed-Volunteer/i });
    await expect(confirmedRow).toBeVisible();
    const confirmedCell = confirmedRow.locator('td.vt-cell.bg-success').first();
    await confirmedCell.locator('button[data-vt-popover]').click();

    // Muted "bail this signup before marking a day off" message renders;
    // the Mark-day-off form does NOT render.
    await expect(page.getByText(/bail this signup before marking a day off/i)).toBeVisible();
    await expect(page.getByRole('button', { name: /mark day off/i })).toHaveCount(0);
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
    await vcPage.goto('/Shifts/Dashboard/VolunteerTracking');

    // The same volunteer should have yellow (blocked) cells for the two offsets.
    const volunteerRow = vcPage.locator('tr', { hasText: /VolTrack-SelfBlock-Volunteer/i });
    await expect(volunteerRow).toBeVisible();
    const blockedCells = volunteerRow.locator('td.vt-cell.vt-cell-blocked');
    await expect(blockedCells).toHaveCount(2);

    await vcContext.close();
  });
});
