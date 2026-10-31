import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import type { BatchDetails } from '../src/app/features/batches/batches.models';
import type { RunComparison } from '../src/app/features/operations/comparison.models';
import type {
  ScenarioDetails,
  ScenarioSummary,
} from '../src/app/features/operations/operations.models';
import type { StudioLayout } from '../src/app/features/scenario-studio/studio.models';

const evidence = resolve('..', 'artifacts', 'simulation-journey');
const ids = {
  baseline: '10000000-0000-4000-8000-000000000001',
  candidate: '10000000-0000-4000-8000-000000000002',
  scene: '20000000-0000-4000-8000-000000000001',
  layout: '20000000-0000-4000-8000-000000000002',
  checkpoint: '20000000-0000-4000-8000-000000000003',
  originalRun: '30000000-0000-4000-8000-000000000001',
  candidateRun: '30000000-0000-4000-8000-000000000002',
};
const scenarios: ScenarioSummary[] = [
  {
    id: ids.baseline,
    code: 'ORIGINAL',
    name: 'O dimineață aglomerată',
    revision: 3,
    description: 'O singură persoană la control.',
    sceneId: ids.scene,
    layoutVersionId: ids.layout,
    isSynthetic: true,
  },
  {
    id: ids.candidate,
    code: 'ALTERNATIVA',
    name: 'Cu un operator în plus',
    revision: 1,
    description: 'Aceiași pasageri, doi operatori.',
    sceneId: ids.scene,
    layoutVersionId: ids.layout,
    isSynthetic: true,
  },
];
const layout: StudioLayout = {
  version: { id: ids.layout, sceneId: ids.scene, version: 1, createdAtUtc: '2026-10-27T06:00:00Z' },
  layout: {
    scene: { id: ids.scene, name: 'Terminal demonstrativ', code: 'DEMO', isSynthetic: true },
    checkpoints: [
      {
        id: ids.checkpoint,
        code: 'CP1',
        name: 'Control de securitate',
        capacityPerMinute: 4,
        terminalId: 'T1',
      },
    ],
    staff: ['s1', 's2'].map((id) => ({
      id,
      code: id,
      displayName: `Operator ${id}`,
      terminalId: 'T1',
      checkpointId: id === 's1' ? ids.checkpoint : null,
    })),
    gates: [],
    routes: [],
  },
};
const source: ScenarioDetails = {
  scenario: scenarios[0],
  inputs: {
    startsAtUtc: '2026-10-27T06:00:00Z',
    durationMinutes: 90,
    seed: 42,
    timeZone: 'Europe/Bucharest',
    flights: [],
    populations: [],
    flows: [],
    events: [],
    staffAllocations: [{ staffMemberId: 's1', checkpointId: ids.checkpoint }],
  },
};
const provenance = {
  hashVersion: 'sha256-tree-v1',
  inputHash: 'a'.repeat(64),
  resultHash: 'b'.repeat(64),
  engineVersion: '1',
  engineBuild: 'build',
  engineMilliseconds: 1,
  executionMilliseconds: 2,
  correlationId: 'trace',
};
const comparison: RunComparison = {
  baseline: {
    id: ids.originalRun,
    scenarioId: ids.baseline,
    scenarioRevision: 3,
    status: 'completed',
    resultsValid: true,
    provenance,
    failureReason: null,
  },
  candidate: {
    id: ids.candidateRun,
    scenarioId: ids.candidate,
    scenarioRevision: 1,
    status: 'completed',
    resultsValid: true,
    provenance,
    failureReason: null,
  },
  inputsMatch: false,
  versionsMatch: true,
  resultsMatch: false,
  metricsTruncated: false,
  comparability: {
    comparable: true,
    populationMatches: true,
    baselinePopulationHash: 'same',
    candidatePopulationHash: 'same',
    issues: [],
  },
  metrics: [
    {
      name: 'meanWaitMinutes',
      zoneId: ids.checkpoint,
      unit: 'minutes',
      baseline: 10,
      candidate: 5,
      delta: -5,
      percentChange: -50,
      baselineRecordedAtUtc: '2026-10-27T07:30:00Z',
      candidateRecordedAtUtc: '2026-10-27T07:30:00Z',
    },
    {
      name: 'processedPassengers',
      zoneId: ids.checkpoint,
      unit: 'passengers',
      baseline: 100,
      candidate: 110,
      delta: 10,
      percentChange: 10,
      baselineRecordedAtUtc: '2026-10-27T07:30:00Z',
      candidateRecordedAtUtc: '2026-10-27T07:30:00Z',
    },
  ],
};
const stats = {
  count: 5,
  mean: 9,
  standardDeviation: 1,
  minimum: 7,
  maximum: 11,
  median: 9,
  p05: 7,
  p95: 11,
};
function batch(id: string, seeds: number[] = [42], selected = scenarios): BatchDetails {
  const variants = selected.map((row, position) => ({
    id: `v${position}`,
    position,
    scenarioId: row.id,
    scenarioRevision: row.revision,
    name: row.name,
    layoutVersionId: row.layoutVersionId,
    engineVersion: '1',
  }));
  return {
    contractVersion: '1.0',
    isSynthetic: true,
    metricsTruncated: false,
    seeds,
    batch: {
      id,
      name: 'Comparație de personal',
      status: 'completed',
      revision: 1,
      createdAtUtc: '2026-10-27T06:00:00Z',
      deadlineAtUtc: '2026-10-27T06:05:00Z',
      startedAtUtc: '2026-10-27T06:00:00Z',
      finishedAtUtc: '2026-10-27T06:00:01Z',
      cancellationRequested: false,
      timeLimitSeconds: 60,
      replayOfBatchId: null,
      failureReason: null,
      engineBuild: 'build',
    },
    variants,
    replications: variants.flatMap((variant, position) =>
      seeds.map((seed, seedIndex) => ({
        id: `${position}-${seedIndex}`,
        variantId: variant.id,
        seedIndex,
        seed,
        runId: position === 0 ? ids.originalRun : ids.candidateRun,
        status: 'completed',
        runStatus: 'completed',
        resultsValid: true,
        failureReason: null,
        provenance,
        remoteCancellationPending: false,
      })),
    ),
    metrics: variants.map((variant) => ({
      variantId: variant.id,
      zoneId: ids.checkpoint,
      name: 'meanWaitMinutes',
      unit: 'minutes',
      values: { ...stats, count: seeds.length },
      missingOrUnsuccessfulCount: 0,
    })),
    comparisons: [],
  };
}
type SubmittedBatch = {
  seeds: number[];
  baseline: { scenarioId: string; revision: number };
  candidates: { scenarioId: string; revision: number }[];
  commandId: string;
};
async function apiFixture(page: Page, comparable = true, empty = false) {
  const requests: SubmittedBatch[] = [];
  const batches = new Map<string, BatchDetails>();
  await page.route('**/intelligence/health', (route) =>
    route.fulfill({ json: { status: 'Healthy' } }),
  );
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url()),
      path = url.pathname,
      method = route.request().method();
    if (path === '/api/auth/session')
      return route.fulfill({
        json: {
          username: 'operator.demo',
          roles: ['Operator'],
          expiresAtUtc: '2099-01-01T00:00:00Z',
        },
      });
    if (path === '/api/auth/csrf') return route.fulfill({ json: { requestToken: 'test-only' } });
    if (path === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    if (path === '/api/scenarios' && method === 'GET')
      return route.fulfill({ json: empty ? [] : scenarios });
    if (path === `/api/scenarios/${ids.baseline}` && method === 'GET')
      return route.fulfill({ json: source });
    if (path === `/api/scenes/${ids.scene}/layout-versions/${ids.layout}`)
      return route.fulfill({ json: layout });
    if (path === '/api/simulation-batches' && method === 'GET') return route.fulfill({ json: [] });
    if (path === `/api/scenarios/${ids.baseline}/what-if` && method === 'POST') {
      const body = route.request().postDataJSON() as { commandId: string };
      const value = batch(body.commandId);
      batches.set(body.commandId, value);
      return route.fulfill({ json: value });
    }
    if (path === '/api/simulation-batches' && method === 'POST') {
      const body = route.request().postDataJSON() as SubmittedBatch;
      requests.push(body);
      const selected = [body.baseline, ...body.candidates].map((reference) => {
        const scenario = scenarios.find((row) => row.id === reference.scenarioId);
        if (!scenario) throw new Error('Unknown scenario in submitted batch');
        return { ...scenario, revision: reference.revision };
      });
      const value = batch(body.commandId, body.seeds, selected);
      batches.set(body.commandId, value);
      return route.fulfill({ json: value });
    }
    if (path.startsWith('/api/simulation-batches/') && method === 'GET') {
      const result = batches.get(path.split('/').at(-1)!);
      return result ? route.fulfill({ json: result }) : route.fulfill({ status: 404 });
    }
    if (path === `/api/runs/${ids.originalRun}/compare/${ids.candidateRun}`)
      return route.fulfill({
        json: comparable
          ? comparison
          : { ...comparison, comparability: { ...comparison.comparability, comparable: false } },
      });
    return route.fulfill({ status: 404, json: { title: `Unexpected test endpoint: ${path}` } });
  });
  return { requests };
}
async function accessibility(page: Page, target: string) {
  const scan = await new AxeBuilder({ page }).include(target).analyze();
  expect(
    scan.violations.map(({ id, nodes }) => ({ id, targets: nodes.map((node) => node.target) })),
  ).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
}
async function mobileSummaryFits(page: Page, name: string) {
  const region = page.getByRole('region', { name, exact: true });
  const dimensions = await region.evaluate((el) => {
    const bounds = el.getBoundingClientRect();
    const overflow = Array.from(el.querySelectorAll('*'))
      .map((child) => {
        const rect = child.getBoundingClientRect();
        const style = getComputedStyle(child);
        return {
          tag: child.tagName,
          className: child.className,
          right: rect.right,
          left: rect.left,
          width: rect.width,
          position: style.position,
          display: style.display,
          text: child.textContent?.trim().slice(0, 50),
        };
      })
      .filter((item) => item.right > bounds.right + 1 || item.left < bounds.left - 1);
    return {
      clientWidth: el.clientWidth,
      scrollWidth: el.scrollWidth,
      left: bounds.left,
      right: bounds.right,
      overflow,
    };
  });
  expect(dimensions.scrollWidth, JSON.stringify(dimensions)).toBeLessThanOrEqual(
    dimensions.clientWidth,
  );
  const bounds = await region.boundingBox();
  expect(bounds).not.toBeNull();
  const cells = region.locator('tbody tr:first-child td');
  await expect(cells).toHaveCount(3);
  for (const cell of await cells.all()) {
    const cellBounds = await cell.boundingBox();
    expect(cellBounds).not.toBeNull();
    expect(cellBounds!.x).toBeGreaterThanOrEqual(bounds!.x);
    expect(cellBounds!.x + cellBounds!.width).toBeLessThanOrEqual(bounds!.x + bounds!.width + 1);
  }
}
test.beforeEach(async () => {
  await mkdir(evidence, { recursive: true });
});
for (const viewport of [
  { width: 1440, height: 900 },
  { width: 390, height: 844 },
]) {
  test(`comparison to repeated tests, ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    const { requests } = await apiFixture(page);
    await page.goto(`/compare?scenarioId=${ids.baseline}`);
    await expect(page.getByRole('heading', { name: 'Alege ce modifici' })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Scenariu', exact: true })).toHaveValue(
      ids.baseline,
    );
    await expect(page.getByRole('spinbutton', { name: 'Operatori în varianta nouă' })).toHaveValue(
      '2',
    );
    await accessibility(page, 'app-compare-workbench');
    await page.screenshot({
      path: resolve(evidence, `compare-setup-${viewport.width}.png`),
      fullPage: true,
    });
    await page.getByRole('button', { name: 'Pornește comparația' }).click();
    const summary = page.getByRole('region', { name: 'Ce s-a schimbat' });
    await expect(summary).toBeVisible();
    await expect(summary).toContainText('Așteptare medie');
    await expect(summary).toContainText('Control de securitate');
    await expect(summary).toContainText('-5');
    await accessibility(page, 'app-compare-workbench');
    if (viewport.width === 390) await mobileSummaryFits(page, 'Rezultatele comparației');
    await page.screenshot({
      path: resolve(evidence, `compare-result-${viewport.width}.png`),
      fullPage: true,
    });
    await expect(page.locator('.next-action')).toHaveCount(0);
    await page.goto(`/batches?scenarioId=${ids.baseline}&candidateId=${ids.candidate}`);
    await expect(page).toHaveURL(
      new RegExp(`/batches\\?scenarioId=${ids.baseline}&candidateId=${ids.candidate}`),
    );
    await expect(
      page.getByRole('combobox', { name: '1. Scenariul pe care îl testezi' }),
    ).toHaveValue(ids.baseline);
    await expect(page.locator('.candidate-options')).toHaveCount(0);
    await page
      .getByRole('combobox', { name: /De câte ori îl simulăm/ })
      .selectOption({ label: '5 repetări' });
    await expect(page.getByText('5 simulări în total')).toBeVisible();
    await accessibility(page, 'app-batches');
    await page.screenshot({
      path: resolve(evidence, `repeat-setup-${viewport.width}.png`),
      fullPage: true,
    });
    await page.getByRole('button', { name: 'Pornește testele' }).click();
    await expect(
      page.getByRole('heading', { name: /Comparație de personal · Finalizat/ }),
    ).toBeVisible();
    expect(requests).toHaveLength(1);
    expect(requests[0].seeds).toEqual([42, 43, 44, 45, 46]);
    expect(requests[0].baseline).toEqual({ scenarioId: ids.baseline, revision: 3 });
    expect(requests[0].candidates).toEqual([]);
    await expect(page.getByText(/5 din 5 simulări finalizate/)).toBeVisible();
    await expect(
      page.getByText(/ultima valoare salvată pentru fiecare simulare reușită/),
    ).toBeVisible();
    await expect(page.getByText(/Intervalul minim–maxim nu garantează/)).toBeVisible();
    await accessibility(page, 'app-batches');
    if (viewport.width === 390) await mobileSummaryFits(page, 'Rezultatele testelor repetate');
    await page.screenshot({
      path: resolve(evidence, `repeat-result-${viewport.width}.png`),
      fullPage: true,
    });
  });
}
test('incomparable simulations do not show a false calculated difference', async ({ page }) => {
  await apiFixture(page, false);
  await page.goto(`/compare?scenarioId=${ids.baseline}`);
  await page.getByRole('button', { name: 'Pornește comparația' }).click();
  const summary = page.getByRole('region', { name: 'Ce s-a schimbat' });
  await expect(summary).toContainText('Datele nu permit o comparație corectă');
  await expect(summary).toContainText('Indisponibilă');
  await expect(summary).not.toContainText('-5');
});

test('empty comparison and repeat pages explain how to create a scenario', async ({ page }) => {
  await apiFixture(page, true, true);
  await page.goto('/compare');
  await expect(
    page.getByText('Ai nevoie de un scenariu salvat pentru a compara personalul.'),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: /Creează primul scenariu/ })).toHaveAttribute(
    'href',
    '/scenario-studio',
  );
  await page.goto('/batches');
  await expect(
    page.getByText('Ai nevoie de un scenariu salvat pentru a repeta simularea.'),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: /Creează primul scenariu/ })).toHaveAttribute(
    'href',
    '/scenario-studio',
  );
});
