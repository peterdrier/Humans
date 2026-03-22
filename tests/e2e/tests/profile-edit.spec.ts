import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('Profile Edit', () => {
  test('edit page loads with interactive form fields', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/Edit');

    // Page loads with the edit heading
    await expect(page.locator('h1')).toContainText('Edit Profile');

    // Key form fields are present and interactive
    const burnerNameInput = page.locator('input[name="BurnerName"]');
    await expect(burnerNameInput).toBeVisible();
    await expect(burnerNameInput).toBeEditable();

    // Burner name has a value (seeded by DevAuth)
    const currentValue = await burnerNameInput.inputValue();
    expect(currentValue.length).toBeGreaterThan(0);

    // Can type into the field (interactive)
    await burnerNameInput.fill('E2E interactivity test');
    await expect(burnerNameInput).toHaveValue('E2E interactivity test');

    // Bio textarea is present
    const bioInput = page.locator('textarea[name="Bio"]');
    await expect(bioInput).toBeVisible();

    // Save Changes button exists
    await expect(page.getByRole('button', { name: 'Save Changes' })).toBeVisible();

    // No server errors
    await expect(page.locator('.alert-danger:not(.d-none)')).not.toBeVisible();
  });
});
