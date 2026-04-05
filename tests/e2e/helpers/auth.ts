import { type Page, type APIResponse, expect } from '@playwright/test';

const NAV_SELECTOR = '[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)';

async function loginAs(page: Page, slug: string): Promise<void> {
  await page.goto(`/dev/login/${slug}`, { waitUntil: 'domcontentloaded' });
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
export const loginAsHumanAdmin = (page: Page) => loginAs(page, 'human-admin');
export const loginAsFinanceAdmin = (page: Page) => loginAs(page, 'finance-admin');
export const loginAsFeedbackAdmin = (page: Page) => loginAs(page, 'feedback-admin');

/**
 * Login as a team coordinator. This persona is coordinator of "Dev Test Department"
 * (slug: dev-test-department) and member of its sub-team "Dev Test SubTeam"
 * (slug: dev-test-subteam). NOT a coordinator of any other department.
 */
export const loginAsCoordinator = (page: Page) => loginAs(page, 'coordinator');

/**
 * Extract an antiforgery token from the current page.
 * Requires the page to already be on an authenticated page (the layout includes
 * _LanguageChooser and FeedbackWidget which both render @Html.AntiForgeryToken()).
 */
export async function getAntiForgeryToken(page: Page): Promise<string> {
  const token = await page
    .locator('input[name="__RequestVerificationToken"]')
    .first()
    .inputValue()
    .catch(() => '');
  return token;
}

/**
 * POST a form with a valid antiforgery token. Returns the raw response.
 * The caller must already be on a page (to extract the CSRF token).
 */
export async function postWithCsrf(
  page: Page,
  url: string,
  formData: string,
): Promise<APIResponse> {
  const token = await getAntiForgeryToken(page);
  return page.request.post(url, {
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    data: `__RequestVerificationToken=${encodeURIComponent(token)}&${formData}`,
  });
}

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
