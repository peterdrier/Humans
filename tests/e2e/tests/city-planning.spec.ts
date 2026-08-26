import { test, expect, type Page } from '@playwright/test';
import {
  loginAsVolunteer,
  loginAsCampAdmin,
  loginAsBoard,
  loginAsCityPlanning,
  expectBlocked,
  postWithCsrf,
} from '../helpers/auth';

// The container screens are keyed by season year, and QA's year moves. Read it off the
// landing page's own container-map link rather than hard-coding it, so the spec follows
// the season instead of being edited every rollover.
async function containerMapYear(page: Page): Promise<string> {
  await page.goto('/CityPlanning');
  const href = await page
    .locator('a[href*="/CityPlanning/ContainerMap/"]')
    .first()
    .getAttribute('href');
  const year = href?.match(/\/ContainerMap\/(\d+)/)?.[1];
  expect(year, 'a map admin should see the container-map link on /CityPlanning').toBeTruthy();
  return year!;
}

test.describe('City Planning (38-city-planning)', () => {
  test.describe('view access', () => {
    test('volunteer can view /CityPlanning', async ({ page }) => {
      await loginAsVolunteer(page);
      await page.goto('/CityPlanning');

      expect(page.url()).toContain('/CityPlanning');
      await expect(page.locator('#map-page, #map').first()).toBeVisible();
    });

    // #map alone would also match /CityPlanning's own map, and the re-executed error
    // view would match neither — data-user-camp-season-id is rendered only by BarrioMap.
    test('volunteer can view /CityPlanning/BarrioMap', async ({ page }) => {
      await loginAsVolunteer(page);
      await page.goto('/CityPlanning/BarrioMap');

      expect(page.url()).toContain('/CityPlanning/BarrioMap');
      await expect(page.locator('#map[data-user-camp-season-id]')).toBeVisible();
    });
  });

  test.describe('container screens — positive', () => {
    test('city-planning team member can view the container map', async ({ page }) => {
      await loginAsCityPlanning(page);
      const year = await containerMapYear(page);
      await page.goto(`/CityPlanning/ContainerMap/${year}`);

      expect(page.url()).toContain(`/CityPlanning/ContainerMap/${year}`);
      await expect(page.locator('#container-sidebar')).toBeVisible();
    });

    test('city-planning team member can view the org container list', async ({ page }) => {
      await loginAsCityPlanning(page);
      const year = await containerMapYear(page);
      await page.goto(`/CityPlanning/BarrioMap/Admin/Containers/${year}`);

      expect(page.url()).toContain(`/CityPlanning/BarrioMap/Admin/Containers/${year}`);
      await expect(page.locator('a[href*="/CityPlanning/ContainerMap/"]').first()).toBeVisible();
    });
  });

  test.describe('container screens — boundary', () => {
    // Both guards run before the year is used, so any year exercises the deny path.
    const anyYear = new Date().getUTCFullYear();

    test('volunteer cannot access the container map', async ({ page }) => {
      await loginAsVolunteer(page);
      await expectBlocked(page, `/CityPlanning/ContainerMap/${anyYear}`);
    });

    test('volunteer cannot access the org container list', async ({ page }) => {
      await loginAsVolunteer(page);
      await expectBlocked(page, `/CityPlanning/BarrioMap/Admin/Containers/${anyYear}`);
    });
  });

  test.describe('admin access — positive', () => {
    // Assert an admin-page-specific control, not just a heading. The app uses
    // UseStatusCodePagesWithReExecute("/Home/Error/{0}"), which renders
    // Views/Home/Error.cshtml *at the original URL* — and that view has its own
    // h1 + h2. So `url contains ...` + `h1, h2` visible would still pass on a
    // 403/404/500, which is exactly the vacuous behaviour the route correction
    // was meant to remove. The admin forms only exist on the real admin page.
    const adminForm = 'form[action*="/CityPlanning/BarrioMap/Admin/"]';

    test('camp-admin can access /CityPlanning/BarrioMap/Admin', async ({ page }) => {
      await loginAsCampAdmin(page);
      await page.goto('/CityPlanning/BarrioMap/Admin');

      expect(page.url()).toContain('/CityPlanning/BarrioMap/Admin');
      await expect(page.locator(adminForm).first()).toBeVisible();
    });

    test('city-planning team member can access /CityPlanning/BarrioMap/Admin', async ({ page }) => {
      await loginAsCityPlanning(page);
      await page.goto('/CityPlanning/BarrioMap/Admin');

      expect(page.url()).toContain('/CityPlanning/BarrioMap/Admin');
      await expect(page.locator(adminForm).first()).toBeVisible();
    });
  });

  test.describe('admin access — boundary', () => {
    test('volunteer cannot access /CityPlanning/BarrioMap/Admin', async ({ page }) => {
      await loginAsVolunteer(page);
      await expectBlocked(page, '/CityPlanning/BarrioMap/Admin');
    });

    test('board member cannot access /CityPlanning/BarrioMap/Admin', async ({ page }) => {
      await loginAsBoard(page);
      await expectBlocked(page, '/CityPlanning/BarrioMap/Admin');
    });
  });

  test.describe('admin POST actions — boundary', () => {
    test('volunteer cannot POST OpenPlacement', async ({ page }) => {
      await loginAsVolunteer(page);
      await page.goto('/CityPlanning');
      const response = await postWithCsrf(page, '/CityPlanning/BarrioMap/Admin/OpenPlacement', '');
      expect([302, 403]).toContain(response.status());
      // If 302, it should NOT redirect to the admin page (should be access denied or home)
      if (response.status() === 302) {
        const location = response.headers()['location'] ?? '';
        expect(location).not.toContain('/CityPlanning/BarrioMap/Admin');
      }
    });

    test('volunteer cannot POST ClosePlacement', async ({ page }) => {
      await loginAsVolunteer(page);
      await page.goto('/CityPlanning');
      const response = await postWithCsrf(page, '/CityPlanning/BarrioMap/Admin/ClosePlacement', '');
      expect([302, 403]).toContain(response.status());
    });

    test('volunteer cannot POST UploadLimitZone', async ({ page }) => {
      await loginAsVolunteer(page);
      await page.goto('/CityPlanning');
      const response = await postWithCsrf(page, '/CityPlanning/BarrioMap/Admin/UploadLimitZone', '');
      expect([302, 403]).toContain(response.status());
    });

    test('volunteer cannot POST UploadOfficialZones', async ({ page }) => {
      await loginAsVolunteer(page);
      await page.goto('/CityPlanning');
      const response = await postWithCsrf(page, '/CityPlanning/BarrioMap/Admin/UploadOfficialZones', '');
      expect([302, 403]).toContain(response.status());
    });

    test('board member cannot POST OpenPlacement', async ({ page }) => {
      await loginAsBoard(page);
      await page.goto('/CityPlanning');
      const response = await postWithCsrf(page, '/CityPlanning/BarrioMap/Admin/OpenPlacement', '');
      expect([302, 403]).toContain(response.status());
    });
  });

  test.describe('API auth — boundary', () => {
    test('volunteer cannot access export endpoint', async ({ page }) => {
      await loginAsVolunteer(page);
      const response = await page.request.get('/api/city-planning/export.geojson');
      expect([401, 403]).toContain(response.status());
    });
  });
});
