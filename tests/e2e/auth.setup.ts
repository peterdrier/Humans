import { test as setup, expect } from '@playwright/test';
import { PERSONAS, liveLoginAndSave } from './helpers/auth';

// One serial test: seed + log in every persona once, capturing storageState. A single test
// means a single worker, so the server seeds one persona at a time. Failures are collected so
// one broken persona doesn't hide the others; a cold target that times out is healed by
// Playwright's own retry of this test.
setup('authenticate all personas', async ({ browser, baseURL }) => {
  setup.setTimeout(PERSONAS.length * 90_000);
  const failures: string[] = [];
  for (const slug of PERSONAS) {
    // baseURL isn't applied to ad-hoc contexts (only test fixtures get use.* options), so pass it through.
    const context = await browser.newContext({ baseURL });
    try {
      await liveLoginAndSave(context, slug);
    } catch (e) {
      failures.push(`${slug}: ${(e as Error).message.split('\n')[0]}`);
    } finally {
      await context.close();
    }
  }
  expect(failures, `personas failed to seed/login:\n${failures.join('\n')}`).toEqual([]);
});
