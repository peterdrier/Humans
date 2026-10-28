import { type Page, type APIResponse, type BrowserContext, expect } from '@playwright/test';
import fs from 'fs';
import path from 'path';

const BASE_URL = process.env.BASE_URL || 'https://humans.n.burn.camp';
const NAV_SELECTOR = '[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)';

// Every persona the suite logs in (= the /dev/login/{slug} segment). auth.setup.ts captures each
// one's cookie once per run; loginAs() reuses it.
//
// No 'admin': nobodies-collective/Humans#1332 makes GET /dev/login/admin a 404 on any host
// that isn't Development or the in-process Testing host, so the E2E suite — which runs
// against QA (Staging) — can never mint a full-Admin session. AdminOnly routes are covered
// in-process instead, by tests/Humans.Integration.Tests (DevPersona.Admin).
export const PERSONAS = [
  'volunteer', 'board', 'consent-coordinator', 'teams-admin',
  'camp-admin', 'ticket-admin', 'no-info-admin', 'volunteer-coordinator',
  'human-admin', 'finance-admin', 'feedback-admin', 'city-planning',
  'coordinator', 'events-admin', 'store-admin', 'cantina-admin',
  'e-e-team-admin', 'barrio-1-lead',
] as const;
export type Persona = (typeof PERSONAS)[number];

// Resolve relative to THIS file (not cwd): tests run via --config from a different cwd.
const AUTH_DIR = path.join(__dirname, '..', 'playwright', '.auth');
export const authFile = (slug: string): string => path.join(AUTH_DIR, `${slug}.json`);

/**
 * Pre-seed the cookie-consent cookie so the banner never renders during tests.
 * The banner (<div id="cookieConsent">) otherwise intercepts clicks on page
 * elements (e.g. #nextBtn in the shift-preference wizard) and causes flaky
 * failures. Mirrors the name/value/sameSite set by wwwroot/js/site.js when a
 * user clicks "Accept".
 */
async function seedCookieConsent(page: Page): Promise<void> {
  await page.context().addCookies([
    { name: 'cookieConsent', value: 'accepted', url: BASE_URL, sameSite: 'Lax' },
  ]);
}

/**
 * Live /dev/login + persona seed, then capture storageState to disk.
 * Used ONLY by auth.setup.ts to mint the per-persona cookie once per run —
 * this is the suite's sole coverage of the real login + seeding path.
 */
export async function liveLoginAndSave(context: BrowserContext, slug: Persona): Promise<void> {
  const page = await context.newPage();
  await seedCookieConsent(page);
  await page.goto(`/dev/login/${slug}`, { waitUntil: 'domcontentloaded' });
  // 90 s: a cold target and seed-lock contention on the first persona can push the
  // response well past the default.
  await page.waitForSelector(NAV_SELECTOR, { timeout: 90_000 });
  // A non-Active persona still logs in and still renders the nav — it just lands on
  // the account-status wall (or the onboarding name gate) instead of the app. Fail the
  // run HERE with the persona named, instead of ~30 scattered /Gdpr/Status redirects
  // in the feature specs (#867).
  if (/\/Gdpr\/Status|\/OnboardingWidget/.test(page.url())) {
    throw new Error(`persona ${slug} is not Active — landed on ${page.url()}`);
  }
  await context.storageState({ path: authFile(slug) });
  await page.close();
}

/**
 * Attach the persona's once-captured cookies to this test's context and land on the
 * authenticated home — no /dev/login, no seeding, no SeedLock contention. The saved state
 * includes the cookie-consent cookie, so the banner stays suppressed.
 *
 * 'coordinator' coordinates "Dev Test Department" (slug dev-test-department) and is a member
 * of its sub-team "Dev Test SubTeam" (dev-test-subteam), and nothing else; both teams exist
 * on every host once the persona has logged in.
 */
export async function loginAs(page: Page, persona: Persona): Promise<void> {
  const state = JSON.parse(fs.readFileSync(authFile(persona), 'utf-8'));
  await page.context().addCookies(state.cookies);
  await page.goto('/');
}

/**
 * Extract an antiforgery token from the current page.
 * Requires the page to already be on an authenticated page (the layout includes
 * _LanguageChooser and the Help widget, which both render @Html.AntiForgeryToken()).
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
    maxRedirects: 0,
  });
}

/**
 * Assert that a URL is denied to the current user: cookie auth redirects a failed policy to
 * /Account/AccessDenied (or /Account/Login when signed out), API endpoints answer 403.
 * A 404 is not a denial — accepting one let a deleted route pass an authorization test.
 */
export async function expectBlocked(page: Page, url: string): Promise<void> {
  const response = await page.goto(url);
  const landed = new URL(page.url()).pathname;
  const denied = /^\/Account\/(AccessDenied|Login)/i.test(landed) || response?.status() === 403;
  expect(denied, `${url} should be denied; landed on ${page.url()} with status ${response?.status()}`).toBeTruthy();
}
