import { test, expect } from '@playwright/test';
import { loginAsBoard, loginAsVolunteer, expectBlocked } from '../helpers/auth';

test.describe('Board (09-administration + 18-board-voting)', () => {
  test('US-9.1: board dashboard loads with quick actions', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Board');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Board');
  });

  test('US-9.2: humans list loads at /Human/Admin', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/Human/Admin');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    expect(page.url()).toContain('/Human/Admin');
  });

  test('US-18.1: voting dashboard loads', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/OnboardingReview/BoardVoting');

    await expect(page.locator('h1, h2').first()).toBeVisible();
    // May show voting table or "no pending applications" — either is valid
    expect(page.url()).not.toContain('/Error');
  });

  test('nav: board sees Board link, not Admin link', async ({ page }) => {
    await loginAsBoard(page);
    await page.goto('/');

    const nav = page.locator('nav');
    await expect(nav.getByRole('link', { name: 'Board' })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Admin' })).not.toBeVisible();
  });

  test('boundary: volunteer cannot access /Board', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Board');
  });
});
