import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  retries: 2,
  use: {
    baseURL: process.env.BASE_URL || 'https://humans.n.burn.camp',
    screenshot: 'only-on-failure',
    trace: 'retain-on-first-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { browserName: 'chromium' },
    },
  ],
  reporter: [['html', { open: 'never' }]],
});
