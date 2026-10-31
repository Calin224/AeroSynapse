import { randomUUID } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';

async function login(page: Page, role: 'operator' | 'viewer') {
  const password = process.env[`CRITICAL_${role.toUpperCase()}_PASSWORD`];
  if (!password)
    throw new Error('Use node scripts/test-critical.mjs to provide isolated runtime credentials.');
  await page.goto('/login');
  await page.getByLabel('Utilizator', { exact: true }).fill(`${role}.demo`);
  await page.getByLabel('Parolă', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Intră în panou', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Control Tower', exact: true })).toBeVisible();
}

test('operator creates Bottleneck in Angular, launches FastAPI and reloads persisted run KPIs', async ({
  page,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await login(page, 'operator');
  await page.goto('/scenario-studio');
  await page.getByRole('button', { name: 'Configurații suplimentare', exact: true }).click();
  await page.getByRole('button', { name: /Congestie la securitate/ }).click();
  await expect(
    page.getByRole('heading', { name: 'Identificare și parametri generali' }),
  ).toBeVisible();
  await page.getByText('Data, codul și setările de reproducere', { exact: true }).click();
  await page.getByLabel('Cod', { exact: true }).fill(`e2e-${randomUUID()}`);
  await page.getByRole('button', { name: 'Zboruri și pasageri', exact: true }).click();
  await page.getByRole('button', { name: 'Resurse și evenimente', exact: true }).click();
  await page.getByRole('button', { name: 'Validare scenariu', exact: true }).click();
  await expect(page.getByText('Configurație validată.', { exact: true })).toBeVisible();
  const savedResponse = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === '/api/scenarios',
  );
  await page.getByRole('button', { name: 'Salvează scenariul', exact: true }).click();
  const saved = await savedResponse;
  expect(saved.status()).toBe(201);
  const scenario = await saved.json();
  expect(scenario.scenario.isSynthetic).toBe(true);
  expect(scenario.inputs.seed).toBe(42);
  await page.goto(`/control-tower?scenarioId=${scenario.scenario.id}`);
  const launch = page.getByRole('button', { name: 'Pornește simularea', exact: true });
  await expect(launch).toBeEnabled();
  const launchResponse = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST' &&
      response.url().endsWith(`/scenarios/${scenario.scenario.id}/simulations`),
  );
  await launch.click();
  const launched = await launchResponse;
  expect(launched.status()).toBe(202);
  const command = await launched.json();
  expect(command.status).toMatch(/pending|running|completed/);
  const key = launched.request().headers()['idempotency-key'];
  expect(key).toMatch(/^[a-f0-9-]{36}$/);
  await expect
    .poll(
      async () => {
        const response = await page.request.get(`/api/simulations/${key}`);
        expect(response.ok()).toBe(true);
        return (await response.json()).status;
      },
      { timeout: 45_000 },
    )
    .toBe('completed');
  await expect(page.locator('#tower-run')).toHaveValue(command.runId);
  await expect(page.getByTestId('tower-processed')).not.toContainText('—');
  await expect(page.locator('.kpi-grid')).toHaveAttribute('data-run-id', command.runId);
  const before = (await page.getByTestId('tower-processed').innerText()).trim();
  const stored = await page.request.get(`/api/runs/${command.runId}`);
  expect(stored.ok()).toBe(true);
  const run = await stored.json();
  expect(run.resultsValid).toBe(true);
  expect(run.provenance.inputHash).toMatch(/^[a-f0-9]{64}$/);
  expect(run.provenance.resultHash).toMatch(/^[a-f0-9]{64}$/);
  const engineResponse = await page.request.get(
    `${process.env['CRITICAL_ENGINE_URL']}/simulation/runs/${command.runId}`,
  );
  expect(engineResponse.ok()).toBe(true);
  const engine = await engineResponse.json();
  expect(engine.status).toBe('completed');
  expect(engine.diagnostics.resultHash).toBe(run.provenance.resultHash);
  expect(Number(before.replace(/[^0-9]/g, ''))).toBe(engine.result.kpiResult.processedCount);
  await page.reload();
  await expect(page.locator('#tower-run')).toHaveValue(command.runId);
  await expect(page.getByTestId('tower-processed')).toHaveText(before);
  expect(errors).toEqual([]);
  await test.info().attach('run-evidence', {
    contentType: 'application/json',
    body: JSON.stringify({
      scenarioId: scenario.scenario.id,
      runId: command.runId,
      inputHash: run.provenance.inputHash,
      resultHash: run.provenance.resultHash,
      engineBuild: run.provenance.engineBuild,
    }),
  });
});

test('anonymous and Viewer access respect the real Identity boundary', async ({ page }) => {
  const anonymous = await page.request.get('/api/scenarios');
  expect(anonymous.status()).toBe(401);
  await page.goto('/scenario-studio');
  await expect(page.getByRole('heading', { name: 'Autentificare', exact: true })).toBeVisible();
  await login(page, 'viewer');
  expect((await page.request.get('/api/scenes')).status()).toBe(200);
  expect(
    (
      await page.request.post(
        '/api/recommendation-reviews/00000000-0000-0000-0000-000000000001/approve',
        { data: {} },
      )
    ).status(),
  ).toBe(403);
  await page.goto('/scenario-studio');
  await expect(page).toHaveURL(/\/access-denied$/);
});
