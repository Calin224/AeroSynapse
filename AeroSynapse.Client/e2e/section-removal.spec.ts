import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { ids, replayData, replayEnvelope } from '../src/app/features/replay/replay.testing';

async function setup(page: Page) {
  const data = replayData();
  const envelope = await replayEnvelope(data);
  const requests: string[] = [];
  page.on('request', (request) => requests.push(request.url()));
  await page.route('**/intelligence/health', (route) =>
    route.fulfill({ json: { status: 'Healthy' } }),
  );
  await page.route('**/api/**', (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/auth/session')
      return route.fulfill({
        json: {
          username: 'operator.demo',
          roles: ['Operator'],
          tenantId: 'demo',
          expiresAtUtc: '2099-01-01T00:00:00Z',
        },
      });
    if (path === '/api/auth/csrf') return route.fulfill({ json: { requestToken: 'test-csrf' } });
    if (path === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    if (path === '/api/scenarios')
      return route.fulfill({ json: [data.captured.scenario.scenario] });
    if (path === `/api/scenarios/${ids.scenario}`)
      return route.fulfill({ json: data.captured.scenario });
    if (path === `/api/scenarios/${ids.scenario}/runs`) return route.fulfill({ json: [data.run] });
    if (path === `/api/scenes/${ids.scene}/layout-versions/${ids.layout}`)
      return route.fulfill({ json: data.captured.layout });
    if (path === `/api/runs/${ids.run}`) return route.fulfill({ json: data.run });
    if (path === `/api/runs/${ids.run}/replay`) return route.fulfill({ json: envelope });
    if (path === '/api/simulation-batches')
      return request.method() === 'GET'
        ? route.fulfill({ json: [] })
        : route.fulfill({ status: 503, json: { title: 'Test failure' } });
    return route.fulfill({ status: 404, json: { title: 'Outside this test' } });
  });
  return requests;
}

for (const width of [1440, 390]) {
  test(`removed sections stay absent while reports and setup work at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    const requests = await setup(page);
    await page.goto(`/operational-report?baselineRunId=${ids.run}`);
    await expect(page.locator('[data-summary-metric="queuedPassengers"]')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Rulări', exact: true })).toHaveCount(0);
    await expect(page.locator('.run-cards')).toHaveCount(0);
    await expect(page.getByText('Detalii despre rulări', { exact: true })).toHaveCount(0);
    expect(
      (await new AxeBuilder({ page }).include('app-operational-report').analyze()).violations,
    ).toEqual([]);

    await page.goto(`/compare?scenarioId=${ids.scenario}`);
    await expect(page.getByRole('button', { name: 'Pornește comparația' })).toBeVisible();
    await expect(page.locator('app-compare-workbench .history')).toHaveCount(0);
    expect(requests.filter((url) => url.includes('/simulation-batches?scenarioId='))).toEqual([]);

    await page.goto(`/batches?scenarioId=${ids.scenario}&candidateId=${ids.otherRun}`);
    await expect(page.getByRole('combobox', { name: 'Ce scenariu testezi?' })).toHaveValue(
      ids.scenario,
    );
    await expect(page.locator('.candidate-options, input[type="checkbox"]')).toHaveCount(0);
    await expect(
      page.getByText('Compară cu alte variante (opțional)', { exact: true }),
    ).toHaveCount(0);
    const submitted = page.waitForRequest(
      (request) =>
        request.method() === 'POST' &&
        new URL(request.url()).pathname === '/api/simulation-batches',
    );
    await page.getByRole('button', { name: 'Pornește testele' }).click();
    expect((await submitted).postDataJSON()).toMatchObject({
      baseline: { scenarioId: ids.scenario, revision: 1 },
      candidates: [],
      seeds: [42, 43, 44],
    });
  });
}
