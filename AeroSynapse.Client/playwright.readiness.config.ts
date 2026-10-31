import { defineConfig } from '@playwright/test';

const phase = process.env['READINESS_PHASE'] ?? 'prepare';
if (!['prepare', 'engine-down', 'backend-down', 'recovered'].includes(phase))
  throw new Error('Unknown isolated readiness phase.');
const baseURL = process.env['CRITICAL_WEB_URL'];
if (!baseURL || new URL(baseURL).hostname !== '127.0.0.1')
  throw new Error('Readiness only accepts its owned loopback stack.');
export default defineConfig({
  testDir: './e2e/readiness',
  testMatch: `${phase}.spec.ts`,
  timeout: 180000,
  globalTimeout: phase === 'prepare' ? 480000 : 120000,
  expect: { timeout: 20000 },
  workers: 1,
  retries: 0,
  forbidOnly: true,
  outputDir: `/artifacts/${phase}/failures`,
  reporter: [
    ['list'],
    ['json', { outputFile: `/artifacts/${phase}/results.json` }],
    ['junit', { outputFile: `/artifacts/${phase}/results.xml` }],
  ],
  use: {
    baseURL,
    browserName: 'chromium',
    headless: true,
    serviceWorkers: 'block',
    trace: 'off',
    screenshot: 'only-on-failure',
    viewport: { width: 1366, height: 768 },
  },
});
