import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e/critical',
  timeout: 60_000,
  globalTimeout: 240_000,
  expect: { timeout: 20_000 },
  workers: 1,
  retries: 0,
  forbidOnly: true,
  outputDir: '/artifacts/failures',
  reporter: [
    ['list'],
    ['json', { outputFile: '/artifacts/results.json' }],
    ['junit', { outputFile: '/artifacts/results.xml' }],
  ],
  use: {
    baseURL: process.env['CRITICAL_WEB_URL'],
    browserName: 'chromium',
    headless: true,
    serviceWorkers: 'block',
    // Authentication runs through the real form; traces would retain credentials/cookies.
    trace: 'off',
    screenshot: 'only-on-failure',
    viewport: { width: 1440, height: 1000 },
  },
});
