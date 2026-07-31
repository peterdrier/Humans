import { test, expect } from '@playwright/test';
import {
  loginAsVolunteer,
  loginAsCampAdmin,
  loginAsBoard,
  loginAsCityPlanning,
  expectBlocked,
  postWithCsrf,
} from '../helpers/auth';

test.describe('City Planning (38-city-planning)', () => {
  test.describe('view access', () => {
    test('volunteer can view /CityPlanning', async ({ page }) => {
      await loginAsVolunteer(page);
      await page.goto('/CityPlanning');

      expect(page.url()).toContain('/CityPlanning');
      await expect(page.locator('#map-page, #map').first()).toBeVisible();
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
