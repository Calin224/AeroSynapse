import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  testIgnore: ['**/critical/**', '**/readiness/**'],
  timeout: 45_000,
  expect: { timeout: 15_000 },
  workers: 1,
  retries: 0,
  reporter: [['list'], ['json', { outputFile: 'test-results/console-results.json' }]],
  use: {
    baseURL: process.env.CONSOLE_URL ?? 'http://127.0.0.1:4200',
    channel: process.env.PLAYWRIGHT_CHANNEL ?? 'chrome',
    serviceWorkers: 'block',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
});
