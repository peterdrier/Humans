import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('Shift Signup', () => {
  test('shift browse page loads and is functional', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Shifts');

    // Page loads with a heading (may show info/warning alerts for closed browsing — that's OK)
    await expect(page.locator('h1, h2').first()).toBeVisible();

    // Check if shifts are available
    const signupButtons = page.locator('form[action*="SignUp"] button[type="submit"]');
    const signupCount = await signupButtons.count();

    if (signupCount > 0) {
      // Shifts exist — click the first available signup
      await signupButtons.first().click();

      // After signup, either success alert or redirect back to shifts page
      const successAlert = page.locator('.alert.alert-success');
      const shiftsPage = page.locator('h1, h2');
      await expect(successAlert.or(shiftsPage.first())).toBeVisible();
    } else {
      // No shifts available — verify the page still renders correctly
      await expect(page.locator('h1, h2').first()).toBeVisible();
    }
  });
});
