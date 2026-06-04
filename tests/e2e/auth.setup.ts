import { test as setup, expect } from '@playwright/test';
import { PERSONAS, liveLoginAndSave } from './helpers/auth';

// One serial test: seed + log in every persona once, capturing storageState.
// Single test = single worker = no login herd during seeding. Failures are
// collected so one broken persona doesn't hide the others.
setup('authenticate all personas', async ({ browser }) => {
  setup.setTimeout(PERSONAS.length * 60_000);
  const failures: string[] = [];
  for (const slug of PERSONAS) {
    const context = await browser.newContext();
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
