import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('Shift Preference Wizard (33-shift-preference-wizard)', () => {
  test('page loads with wizard step 1', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    await expect(page.locator('#stepTitle')).toHaveText('What are you good at?');
    await expect(page.locator('#stepLabel')).toHaveText('Step 1 of 3');
    await expect(page.locator('.progress-dot.active')).toHaveCount(1);
  });

  test('can navigate through all 3 steps', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    // Step 1 -> Step 2
    await page.click('#nextBtn');
    await expect(page.locator('#stepTitle')).toHaveText('How do you like to work?');
    await expect(page.locator('#stepLabel')).toHaveText('Step 2 of 3');

    // Step 2 -> Step 3
    await page.click('#nextBtn');
    await expect(page.locator('#stepTitle')).toHaveText('Which languages do you speak?');
    await expect(page.locator('#stepLabel')).toHaveText('Step 3 of 3');
    await expect(page.locator('#saveBtn')).toBeVisible();
    await expect(page.locator('#nextBtn')).not.toBeVisible();
  });

  test('can go back from step 3 to step 1', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    await page.click('#nextBtn');
    await page.click('#nextBtn');
    await page.click('#backBtn');
    await expect(page.locator('#stepTitle')).toHaveText('How do you like to work?');

    await page.click('#backBtn');
    await expect(page.locator('#stepTitle')).toHaveText('What are you good at?');
    await expect(page.locator('#backBtn')).not.toBeVisible();
  });

  test('selecting a chip toggles it', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    const chip = page.locator('.chip', { hasText: 'Bartending' });
    await chip.click();
    await expect(chip).toHaveClass(/selected/);

    await chip.click();
    await expect(chip).not.toHaveClass(/selected/);
  });

  test('save submits and redirects back', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    // Select a skill
    await page.locator('.chip', { hasText: 'Sound' }).click();

    // Navigate to step 3
    await page.click('#nextBtn');
    await page.click('#nextBtn');

    // Save
    await page.click('#saveBtn');

    // Should redirect back to same page with success message
    await expect(page).toHaveURL(/\/Profile\/ShiftInfo/);
  });
});
