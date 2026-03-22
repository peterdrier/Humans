import { type Page } from '@playwright/test';

export async function loginAsVolunteer(page: Page): Promise<void> {
  await page.goto('/dev/login/volunteer');
  await page.waitForSelector('[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)');
}

export async function loginAsAdmin(page: Page): Promise<void> {
  await page.goto('/dev/login/admin');
  await page.waitForSelector('[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)');
}

export async function loginAsBoard(page: Page): Promise<void> {
  await page.goto('/dev/login/board');
  await page.waitForSelector('[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)');
}

export async function loginAsConsentCoordinator(page: Page): Promise<void> {
  await page.goto('/dev/login/consent-coordinator');
  await page.waitForSelector('[data-testid="user-nav"], .navbar .dropdown:has(.profile-dropdown-menu)');
}
