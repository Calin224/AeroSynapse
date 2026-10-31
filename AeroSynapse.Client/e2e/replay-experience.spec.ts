import { test, expect, Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { ids, replayData, replayEnvelope } from '../src/app/features/replay/replay.testing';

const evidence = resolve('..', 'artifacts', 'time-travel');
async function setup(
  page: Page,
  options: {
    partial?: boolean;
    empty?: boolean;
    fail?: boolean;
    viewer?: boolean;
    loading?: boolean;
  } = {},
) {
  const data = replayData();
  data.captured.scenario.scenario.name = 'Operation Bottleneck · DEMO';
  data.captured.layout.layout.checkpoints[0].name = 'Control de securitate';
  data.captured.layout.layout.checkpoints[1].name = 'Control de frontieră';
  const event = data.captured.scenario.inputs.events[0];
  data.captured.scenario.inputs.events = Array.from({ length: 5 }, (_, index) => ({
    ...event,
    id: `60000000-0000-0000-0000-00000000000${index + 1}`,
  }));
  if (options.partial)
    data.samples = data.samples.filter(
      (sample) => !(sample.minute === 2 && sample.zoneId === ids.otherCp),
    );
  const envelope = await replayEnvelope(data);
  await page.route('**/intelligence/health', (route) =>
    route.fulfill({ json: { status: 'Healthy' } }),
  );
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/session')
      return route.fulfill({
        json: {
          username: 'operator.demo',
          roles: [options.viewer ? 'Viewer' : 'Operator'],
          tenantId: 'demo',
          expiresAtUtc: '2099-01-01T00:00:00Z',
        },
      });
    if (path === '/api/auth/csrf') return route.fulfill({ json: { requestToken: 'test-csrf' } });
    if (path === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    if (path === '/api/scenarios')
      return route.fulfill({ json: options.empty ? [] : [data.captured.scenario.scenario] });
    if (path === `/api/scenarios/${ids.scenario}/runs`) return route.fulfill({ json: [data.run] });
    if (path === `/api/runs/${ids.run}/replay`) {
      if (options.loading) await new Promise((resolve) => setTimeout(resolve, 2000));
      return options.fail
        ? route.fulfill({ status: 503, json: { title: 'Replay indisponibil' } })
        : route.fulfill({ json: envelope });
    }
    return route.fulfill({ status: 404, json: { title: 'Endpoint outside this test' } });
  });
  await mkdir(evidence, { recursive: true });
  return envelope;
}
async function ready(page: Page) {
  await page.goto(`/replay?runId=${ids.run}`);
  await expect(page.locator('.player')).toBeVisible();
  await page.getByLabel('Minutul replay-ului').fill('2.5');
  await expect(page.locator('.player')).toHaveAttribute('data-cursor', '2.5');
  await expect(page.locator('app-digital-twin .overlay')).toHaveCount(0);
}
async function accessibility(page: Page) {
  const result = await new AxeBuilder({ page }).include('app-replay-page').analyze();
  expect(
    result.violations.map(({ id, nodes }) => ({
      id,
      nodes: nodes.map((node) => ({ target: node.target, summary: node.failureSummary })),
    })),
  ).toEqual([]);
}
async function fits(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
}

test('desktop: server replay, coincident events and keyboard timeline', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.setViewportSize({ width: 1440, height: 1000 });
  await setup(page);
  await ready(page);
  await expect(page.locator('.event-row')).toHaveCount(5);
  await expect(page.locator('.event-track').first().locator('button')).toHaveCount(1);
  await page.locator('.event-row').nth(3).click();
  await expect(page.locator('.event-row').nth(3)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('replay-queued')).toHaveText('16');
  await expect(page.getByTestId('replay-wait')).toHaveText('1.25min');
  const timeline = page.getByLabel('Timeline comun pentru animație și KPI');
  await timeline.focus();
  await timeline.press('ArrowRight');
  await expect(page.locator('.player')).toHaveAttribute('data-cursor', '2.51');
  await page.getByRole('button', { name: '16x', exact: true }).click();
  await page.getByRole('button', { name: 'Redă replay', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Pauză', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  await page.getByRole('button', { name: 'Pauză', exact: true }).click();
  await expect(page.locator('.bookmarks-panel, .bookmark-marker, .rail-footer')).toHaveCount(0);
  await page.getByLabel('Minutul replay-ului').fill('2.5');
  await fits(page);
  await accessibility(page);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: resolve(evidence, 'desktop.png'), fullPage: true });
  await page.reload();
  await expect(page.locator('.player')).toBeVisible();
  await expect(page.locator('.offline-tools, .run-context')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('mobile: narrow reflow, reduced motion and no removed controls', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await setup(page);
  await ready(page);
  await expect(page.locator('.bookmarks-panel, .offline-trigger, .manual')).toHaveCount(0);
  await fits(page);
  await accessibility(page);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: resolve(evidence, 'mobile.png'), fullPage: true });
  await expect(page.locator('input[type=file]')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /export/i })).toHaveCount(0);
  await page.setViewportSize({ width: 320, height: 800 });
  await fits(page);
});

test('partial data stays unknown; Viewer can inspect but cannot annotate', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await setup(page, { partial: true, viewer: true });
  await ready(page);
  await expect(page.getByTestId('replay-queued')).toHaveText('—');
  await expect(page.getByTestId('replay-wait')).toHaveText('—min');
  await expect(page.locator('.gap')).toBeVisible();
  await expect(page.locator('.bookmark-form')).toHaveCount(0);
  await expect(
    page
      .locator('[data-zone-id]')
      .filter({ has: page.locator('[data-kpi="queued"]') })
      .last(),
  ).toContainText('—');
  await accessibility(page);
  await page.screenshot({ path: resolve(evidence, 'partial-viewer.png'), fullPage: true });
});

test('empty, loading and failed fetch retain a recovery path without file-transfer tools', async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await setup(page, { empty: true, fail: true, loading: true });
  await page.goto('/replay');
  await expect(page.locator('.replay-empty')).toBeVisible();
  await expect(page.locator('.manual, .offline-tools')).toHaveCount(0);
  await page.goto(`/replay?runId=${ids.run}`);
  await expect(
    page.getByRole('status').filter({ hasText: 'Se pregătește replay-ul' }),
  ).toBeVisible();
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Reîncearcă', exact: true })).toBeEnabled();
  await fits(page);
  await accessibility(page);
  await page.screenshot({ path: resolve(evidence, 'error-empty.png'), fullPage: true });
});
