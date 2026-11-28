import { test, expect } from '@playwright/test';
import { loginAs, expectBlocked } from '../helpers/auth';

test.describe('Tickets (24-ticket-vendor-integration)', () => {
  test('ticket admin can access dashboard', async ({ page }) => {
    await loginAs(page, 'tickets-admin');
    await page.goto('/Tickets');

    // Admin-shell pages render no visible h1/h2: the top bar names the section and the
    // selected tab names the page. The error page has no tabs, so this can't pass vacuously.
    await expect(page.getByRole('navigation', { name: 'Section pages' }).locator('[aria-current="page"]'))
      .toHaveText('Tickets');
    expect(page.url()).toContain('/Tickets');
  });

  test('orders page loads at /Tickets/Orders', async ({ page }) => {
    await loginAs(page, 'tickets-admin');
    await page.goto('/Tickets/Orders');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Tickets/Orders');
  });

  test('boundary: volunteer cannot access /Tickets', async ({ page }) => {
    await loginAs(page, 'volunteer');
    await expectBlocked(page, '/Tickets');
  });
});
