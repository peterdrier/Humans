import { test, expect } from '@playwright/test';
import { loginAsVolunteer, loginAsAdmin } from '../helpers/auth';

test.describe('Community calendar', () => {
  test('logged-in volunteer can view the month view', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Calendar');

    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await expect(page.locator('text=All times shown in')).toBeVisible();
  });

  test('admin can open the create-event form', async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto('/Calendar/Event/Create');

    await expect(page.getByRole('heading', { name: 'New event' })).toBeVisible();
    await expect(page.locator('input[name="Title"]')).toBeVisible();
    await expect(page.locator('select[name="OwningTeamId"]')).toBeVisible();
  });

  test('agenda view renders', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Calendar/Agenda');

    await expect(page.getByRole('heading', { name: 'Agenda' })).toBeVisible();
  });
});
