import { test, expect, Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import type { StudioLayout, StudioDraft } from '../src/app/features/scenario-studio/studio.models';
import type { ScenarioDetails } from '../src/app/features/operations/operations.models';

const evidence = resolve('..', 'artifacts', 'scenario-creation');
const layout: StudioLayout = {
  version: { id: 'layout', sceneId: 'scene', version: 1, createdAtUtc: '2026-10-27T06:00:00Z' },
  layout: {
    scene: {
      id: 'scene',
      code: 'AUR',
      name: 'Aeroportul Aurora · demonstrație',
      isSynthetic: true,
    },
    gates: ['G1', 'G2', 'G3'].map((code) => ({
      id: code,
      code,
      name: `Poarta ${code}`,
      terminalId: 'T1',
      capacity: 200,
    })),
    checkpoints: [
      {
        id: 'CP1',
        code: 'CP1',
        name: 'Control de securitate',
        capacityPerMinute: 6,
        terminalId: 'T1',
      },
    ],
    staff: ['A', 'B', 'C'].map((code) => ({
      id: code,
      code,
      displayName: `Operator ${code}`,
      terminalId: 'T1',
      checkpointId: 'CP1',
    })),
    routes: ['G1', 'G2', 'G3'].map((gate) => ({
      id: `CP1-${gate}`,
      code: `CP1-${gate}`,
      terminalId: 'T1',
      fromCheckpointId: 'CP1',
      toGateId: gate,
    })),
  },
};
async function fixture(page: Page, role = 'Operator') {
  let saved: ScenarioDetails | null = null;
  const requests: StudioDraft[] = [];
  const state = {
    sceneStatus: 200,
    libraryStatus: 200,
    saveStatus: 200,
    validationStatus: 200,
    empty: false,
  };
  await page.route('**/intelligence/health', (route) =>
    route.fulfill({ json: { status: 'Healthy' } }),
  );
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const method = route.request().method();
    if (path === '/api/auth/session')
      return route.fulfill({
        json: { username: 'operator.demo', roles: [role], expiresAtUtc: '2099-01-01T00:00:00Z' },
      });
    if (path === '/api/auth/csrf') return route.fulfill({ json: { requestToken: 'test-only' } });
    if (path === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    if (path === '/api/scenes')
      return route.fulfill({
        status: state.sceneStatus,
        json:
          state.sceneStatus === 200
            ? state.empty
              ? []
              : [layout.layout.scene]
            : { title: 'Aeroporturi indisponibile' },
      });
    if (path === '/api/scenes/scene/layout-versions') return route.fulfill({ json: layout });
    if (path === '/api/scenes/scene/layout-versions/layout') return route.fulfill({ json: layout });
    if (path === '/api/scenarios/validate')
      return route.fulfill({
        status: state.validationStatus,
        json:
          state.validationStatus === 200
            ? { isValid: true }
            : {
                errors: {
                  'inputs.flights[0].boardingClosesAtUtc': [
                    'Boarding must close strictly before departure.',
                  ],
                },
              },
      });
    if (path === '/api/scenarios' && method === 'POST') {
      requests.push(route.request().postDataJSON());
      if (state.saveStatus !== 200)
        return route.fulfill({ status: state.saveStatus, json: { code: 'resource_conflict' } });
      const draft = route.request().postDataJSON() as StudioDraft;
      saved = {
        scenario: {
          id: 'created',
          code: draft.code,
          name: draft.name,
          description: draft.description,
          sceneId: draft.sceneId,
          layoutVersionId: draft.layoutVersionId,
          revision: 1,
          isSynthetic: true,
        },
        inputs: draft.inputs,
      };
      return route.fulfill({ status: 201, json: saved });
    }
    if (path === '/api/scenarios')
      return route.fulfill({
        status: state.libraryStatus,
        json:
          state.libraryStatus === 200
            ? saved
              ? [saved.scenario]
              : []
            : { title: 'Biblioteca indisponibilă' },
      });
    if (path === '/api/scenarios/created') return route.fulfill({ json: saved });
    if (path === '/api/scenarios/created/runs') return route.fulfill({ json: [] });
    if (path.endsWith('/operations'))
      return route.fulfill({
        json: {
          scenarioId: 'created',
          sequence: 0,
          resetRequired: false,
          runs: [],
          run: null,
          optimization: null,
        },
      });
    if (path.endsWith('/flight-schedule'))
      return route.fulfill({
        json: {
          scenarioId: 'created',
          revision: 1,
          startsAtUtc: saved!.inputs.startsAtUtc,
          durationMinutes: 90,
          flights: [],
          gates: [],
          isSynthetic: true,
        },
      });
    return route.fulfill({ status: 404, json: { title: 'Endpoint outside this test' } });
  });
  return { state, requests };
}
async function accessibility(page: Page) {
  const scan = await new AxeBuilder({ page }).include('app-scenario-studio').analyze();
  expect(scan.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) }))).toEqual(
    [],
  );
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
}
async function start(page: Page) {
  await page.goto('/scenario-studio');
  await page.getByRole('button', { name: /O zi obișnuită/ }).click();
  await expect(page.getByRole('heading', { name: 'Dă un nume scenariului' })).toBeVisible();
}
async function review(page: Page) {
  await page.getByRole('button', { name: 'Continuă la zboruri' }).click();
  await page.getByRole('button', { name: 'Continuă la resurse' }).click();
  await page.getByRole('button', { name: 'Verifică scenariul', exact: true }).click();
}
test.beforeEach(async () => mkdir(evidence, { recursive: true }));
for (const viewport of [
  { width: 1440, height: 900 },
  { width: 390, height: 844 },
]) {
  test(`guided creation, linked traffic and completion ${viewport.width}`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    const { requests } = await fixture(page);
    await page.goto('/scenario-studio');
    await expect(page.getByRole('heading', { name: 'Cu ce vrei să începi?' })).toBeVisible();
    await accessibility(page);
    await page.screenshot({
      path: resolve(evidence, `start-${viewport.width}.png`),
      fullPage: true,
    });
    const preset = page.getByRole('button', { name: /O zi obișnuită/ });
    await preset.focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('#step-heading')).toBeFocused();
    await page
      .getByLabel('Numele scenariului', { exact: true })
      .fill('Dimineață aglomerată · verificarea primului scenariu');
    await page.getByRole('button', { name: 'Continuă la zboruri' }).click();
    const flight = page.getByRole('article', { name: 'Zbor 1', exact: true });
    await flight.getByLabel('Pasageri', { exact: true }).fill('145');
    await flight.getByLabel('Plecare după (minute)', { exact: true }).fill('30');
    await flight
      .getByRole('combobox', { name: 'Poartă de îmbarcare', exact: true })
      .selectOption('G3');
    await accessibility(page);
    await page.screenshot({
      path: resolve(evidence, `flights-${viewport.width}.png`),
      fullPage: true,
    });
    await page.getByRole('button', { name: 'Continuă la resurse' }).click();
    await accessibility(page);
    await page.getByRole('button', { name: 'Verifică scenariul', exact: true }).click();
    await expect(page.getByText('Configurația a trecut verificarea.')).toBeVisible();
    await page.getByRole('button', { name: 'Salvează scenariul', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Scenariul este salvat' })).toBeVisible();
    const request = requests[0];
    expect(request.inputs.flows[0].passengerCount).toBe(145);
    expect(request.inputs.populations[0].count).toBe(145);
    expect(request.inputs.flows[0].routeId).toBe('CP1-G3');
    expect(request.inputs.flights[0].gateId).toBe('G3');
    expect(
      Date.parse(request.inputs.flows[0].endsAtUtc) - Date.parse(request.inputs.startsAtUtc),
    ).toBe(28 * 60000);
    await accessibility(page);
    await page.screenshot({
      path: resolve(evidence, `saved-${viewport.width}.png`),
      fullPage: true,
    });
    await page.getByRole('link', { name: 'Continuă la simulare' }).click();
    await expect(page).toHaveURL(/scenario\?scenarioId=created/);
    await expect(page.getByRole('combobox', { name: 'Scenariul ales' })).toHaveValue('created');
    await expect(page.getByText('Pregătit pentru prima simulare')).toBeVisible();
    await expect(
      page.getByRole('button', { name: 'Pornește simularea', exact: true }),
    ).toBeVisible();
    const runAccessibility = await new AxeBuilder({ page }).include('app-scenario').analyze();
    expect(
      runAccessibility.violations.map(({ id, nodes }) => ({
        id,
        targets: nodes.map((node) => node.target),
      })),
    ).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await page.screenshot({
      path: resolve('..', 'artifacts', 'simulation-journey', `scenario-${viewport.width}.png`),
      fullPage: true,
    });
  });
}
test('recommended example is clear and alternative traffic levels remain available', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/scenario-studio');
  await expect(page.getByRole('button', { name: /O zi obișnuită/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Trafic redus/ })).toHaveCount(0);
  await page.getByRole('button', { name: 'Vezi și alte exemple' }).click();
  await page.getByRole('button', { name: /Trafic redus/ }).click();
  await expect(page.getByText('250 pasageri pregătiți')).toBeVisible();
});
test('validation links, failed save, recovery and draft protection', async ({ page }) => {
  const { state, requests } = await fixture(page);
  await start(page);
  await page.getByLabel('Numele scenariului', { exact: true }).fill('');
  await page.getByRole('button', { name: 'Continuă la zboruri' }).click();
  await expect(page.getByRole('heading', { name: 'Dă un nume scenariului' })).toBeVisible();
  await page.getByLabel('Numele scenariului', { exact: true }).fill('Ciornă păstrată');
  state.validationStatus = 400;
  await review(page);
  await page.getByRole('button', { name: 'Zbor 1 → Închidere îmbarcare' }).click();
  await expect(
    page.getByLabel('Închidere îmbarcare (minut)', { exact: true }).first(),
  ).toBeVisible();
  state.validationStatus = 200;
  await page.getByRole('button', { name: '4 Verificare', exact: true }).click();
  state.saveStatus = 409;
  await page.getByRole('button', { name: 'Salvează scenariul', exact: true }).click();
  await expect(page.getByText(/Salvarea a fost refuzată/)).toBeVisible();
  await page.screenshot({ path: resolve(evidence, 'save-conflict.png'), fullPage: true });
  state.saveStatus = 200;
  await page.getByRole('button', { name: 'Salvează scenariul', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Scenariul este salvat' })).toBeVisible();
  expect(requests[0]).toEqual(requests[1]);
  await page.getByRole('button', { name: '1 Detalii', exact: true }).click();
  await page.getByLabel('Numele scenariului', { exact: true }).fill('Editare nesalvată');
  await page.getByRole('button', { name: 'Schimbă exemplul', exact: true }).click();
  await expect(page.getByText(/Ai modificări nesalvate/)).toBeVisible();
  await page.getByRole('button', { name: 'Continuă editarea', exact: true }).click();
  await expect(page.getByLabel('Numele scenariului', { exact: true })).toHaveValue(
    'Editare nesalvată',
  );
});
test('loading, empty, error and partial library leave a usable recovery path', async ({ page }) => {
  const { state } = await fixture(page);
  let release!: () => void;
  const pending = new Promise<void>((resolve) => (release = resolve));
  await page.route('**/api/scenes', async (route) => {
    await pending;
    await route.fallback();
  });
  await page.goto('/scenario-studio');
  await expect(page.getByText(/Se încarcă aeroporturile/)).toBeVisible();
  await page.screenshot({ path: resolve(evidence, 'loading.png'), fullPage: true });
  state.empty = true;
  release();
  await expect(page.getByText(/Nu există un aeroport disponibil/)).toBeVisible();
  await page.screenshot({ path: resolve(evidence, 'empty.png'), fullPage: true });
  state.empty = false;
  state.sceneStatus = 503;
  await page.reload();
  await expect(
    page.locator('app-scenario-studio').getByRole('button', { name: /Încearcă din nou/ }),
  ).toBeVisible();
  state.sceneStatus = 200;
  await page
    .locator('app-scenario-studio')
    .getByRole('button', { name: /Încearcă din nou/ })
    .click();
  await expect(page.getByRole('button', { name: /O zi obișnuită/ })).toBeVisible();
  state.libraryStatus = 503;
  await page.reload();
  await page.getByText('Deschide un scenariu salvat', { exact: true }).click();
  await expect(
    page.locator('.library').getByRole('button', { name: /Încearcă din nou/ }),
  ).toBeVisible();
  await expect(page.getByRole('button', { name: /O zi obișnuită/ })).toBeEnabled();
  await page.screenshot({ path: resolve(evidence, 'partial-library.png'), fullPage: true });
});
test('viewer cannot open creation', async ({ page }) => {
  await fixture(page, 'Viewer');
  await page.goto('/scenario-studio');
  await expect(page.getByRole('heading', { name: 'Acces restricționat' })).toBeVisible();
  await page.screenshot({ path: resolve(evidence, 'permission.png'), fullPage: true });
});
