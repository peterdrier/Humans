import { type Page, expect } from '@playwright/test';

const NAV_SELECTOR = '[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)';

async function loginAs(page: Page, slug: string): Promise<void> {
  await page.goto(`/dev/login/${slug}`);
  await page.waitForSelector(NAV_SELECTOR);
}

export const loginAsVolunteer = (page: Page) => loginAs(page, 'volunteer');
export const loginAsAdmin = (page: Page) => loginAs(page, 'admin');
export const loginAsBoard = (page: Page) => loginAs(page, 'board');
export const loginAsConsentCoordinator = (page: Page) => loginAs(page, 'consent-coordinator');
export const loginAsTeamsAdmin = (page: Page) => loginAs(page, 'teams-admin');
export const loginAsCampAdmin = (page: Page) => loginAs(page, 'camp-admin');
export const loginAsTicketAdmin = (page: Page) => loginAs(page, 'ticket-admin');
export const loginAsNoInfoAdmin = (page: Page) => loginAs(page, 'no-info-admin');
export const loginAsVolunteerCoordinator = (page: Page) => loginAs(page, 'volunteer-coordinator');

/**
 * Assert that a URL is blocked for the current user.
 * Handles four blocking mechanisms: redirect, 404, 403/Access Denied, or Forbid page.
 */
export async function expectBlocked(page: Page, url: string): Promise<void> {
  await page.goto(url);
  const path = new URL(url, 'https://placeholder').pathname;
  const isRedirected = !page.url().includes(path);
  const is404 = await page.getByText('Page Not Found').isVisible().catch(() => false);
  const isAccessDenied = await page.getByText('Access Denied').isVisible().catch(() => false);
  const isForbid = await page.locator('.status-code-page, [data-status="403"]').isVisible().catch(() => false);
  expect(isRedirected || is404 || isAccessDenied || isForbid).toBeTruthy();
}
