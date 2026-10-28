import { test } from '@playwright/test';
import { loginAs, expectBlocked } from '../helpers/auth';

// Only the deny path runs here: the coordinator flows (feature 47) need seeded volunteers with
// known gaps, which no host provides yet.
test.describe('Volunteer Tracking (feature 47)', () => {
  test('boundary: volunteer cannot access /Shifts/Dashboard/VolunteerTracking', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await expectBlocked(page, '/Shifts/Dashboard/VolunteerTracking');
  });
});
