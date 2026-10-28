import { test, expect } from '@playwright/test';
import { loginAs, expectBlocked } from '../helpers/auth';

test.describe('Tickets (24-ticket-vendor-integration)', () => {
  test('ticket admin can access dashboard', async ({ page }) => {
    await loginAs(page, 'ticket-admin');
    await page.goto('/Tickets');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Tickets');
  });

  test('orders page loads at /Tickets/Orders', async ({ page }) => {
    await loginAs(page, 'ticket-admin');
    await page.goto('/Tickets/Orders');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Tickets/Orders');
  });

  test('boundary: volunteer cannot access /Tickets', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await expectBlocked(page, '/Tickets');
  });
});
