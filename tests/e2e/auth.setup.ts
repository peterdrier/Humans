import { test as setup, expect } from '@playwright/test';
import { PERSONAS, liveLoginAndSave } from './helpers/auth';

// One serial test: seed + log in every persona once, capturing storageState.
// Single test = single worker = no login herd during seeding. Failures are
// collected so one broken persona doesn't hide the others.
setup('authenticate all personas', async ({ browser, baseURL }) => {
  // Worst-case: 2 attempts × 90 s + 5 s inter-attempt pause per persona,
  // so the test always reaches the aggregate "personas failed" message rather
  // than timing out mid-list when multiple personas are slow.
  setup.setTimeout(PERSONAS.length * (90_000 * 2 + 5_000));
  const failures: string[] = [];
  for (const slug of PERSONAS) {
    // Try each persona up to twice. QA cold-start or SeedLock contention on the
    // first persona can push the response past the waitForSelector ceiling; a 5 s
    // pause then a second attempt recovers without forcing Playwright to retry all
    // 19 personas from scratch (baseURL isn't applied to ad-hoc contexts — only
    // test page/context fixtures get use.* options — so pass it through here).
    let lastError: Error | null = null;
    for (let attempt = 0; attempt <= 1; attempt++) {
      if (attempt > 0) await new Promise(r => setTimeout(r, 5_000));
      const context = await browser.newContext({ baseURL });
      try {
        await liveLoginAndSave(context, slug);
        lastError = null;
        break;
      } catch (e) {
        lastError = e as Error;
      } finally {
        await context.close();
      }
    }
    if (lastError !== null) {
      failures.push(`${slug}: ${lastError.message.split('\n')[0]}`);
    }
  }
  expect(failures, `personas failed to seed/login:\n${failures.join('\n')}`).toEqual([]);
});
