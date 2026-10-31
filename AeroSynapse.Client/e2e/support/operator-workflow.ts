import { expect, type Page } from '@playwright/test';

export const posted = (page: Page, path: string) =>
  page.waitForResponse(
    (r) => r.request().method() === 'POST' && new URL(r.url()).pathname === '/api/' + path,
  );

/** Saves one crowded checkpoint with a single operator, so more staff has room to help. */
export async function saveCongestionScenario(page: Page) {
  await page.goto('/scenario-studio');
  await page.getByRole('button', { name: 'Configurații suplimentare', exact: true }).click();
  await page.getByRole('button', { name: /Congestie la securitate/ }).click();
  await page.getByRole('button', { name: 'Zboruri și pasageri', exact: true }).click();
  await page.getByRole('button', { name: 'Resurse și evenimente', exact: true }).click();
  await page.getByRole('button', { name: 'Validare scenariu', exact: true }).click();
  await expect(page.getByText('Configurație validată.', { exact: true })).toBeVisible();
  const created = posted(page, 'scenarios');
  await page.getByRole('button', { name: 'Salvează scenariul', exact: true }).click();
  return (await created).json();
}

/** Read persisted evidence through the authenticated API, independently of the browser's rendering. */
export async function readEvaluation(page: Page, evaluationId: string) {
  const get = async (path: string) => {
    const response = await page.request.get('/api/' + path);
    expect(response.ok(), path).toBe(true);
    return response.json();
  };
  const evaluation = await get(`staffing-evaluations/${evaluationId}`);
  expect(evaluation.batch.batch.status).toBe('completed');
  const variants = [...evaluation.variants].sort((a, b) => a.position - b.position);
  expect(variants).toHaveLength(2);
  const [baseline, candidate, comparison] = await Promise.all([
    get(`runs/${variants[0].runId}/replay`),
    get(`runs/${variants[1].runId}/replay`),
    get(`runs/${variants[0].runId}/compare/${variants[1].runId}`),
  ]);
  return {
    evaluation,
    assessment: variants[1].assessment,
    baseline: JSON.parse(baseline.payloadJson),
    candidate: JSON.parse(candidate.payloadJson),
    comparison,
  };
}

/** Saves a surge on one flight, simulates it and evaluates one CP-SAT staffing alternative in SimPy. */
export async function decide(
  page: Page,
  scenarioId: string,
  flightIndex: number,
  sharePercent: number,
) {
  await page.getByRole('button', { name: 'Adăugare incident de concentrare a sosirilor' }).click();
  await page
    .getByRole('combobox', { name: 'Zbor afectat', exact: true })
    .selectOption({ index: flightIndex + 1 });
  await page.getByLabel('Ponderea pasagerilor afectați (%)', { exact: true }).fill('101');
  const save = page.getByRole('button', { name: 'Confirmă și salvează incidentul', exact: true });
  await expect(save).toBeDisabled();
  await page
    .getByLabel('Ponderea pasagerilor afectați (%)', { exact: true })
    .fill(String(sharePercent));
  const saved = posted(page, `scenarios/${scenarioId}/events`);
  await save.click();
  expect((await saved).ok()).toBe(true);
  await expect(
    page.getByRole('heading', { name: '2. Simulare impact operațional', exact: true }),
  ).toBeVisible();
  const launched = posted(page, `scenarios/${scenarioId}/simulations`);
  await page.getByRole('button', { name: 'Confirmă și pornește simularea', exact: true }).click();
  const diagnostic: { runId: string } = await (await launched).json();
  const optimize = page.getByRole('button', { name: 'Optimizare alocări', exact: true });
  await expect(optimize).toBeVisible({ timeout: 60_000 });
  await optimize.click();
  await page
    .getByText('Setări avansate · criterii de acceptare și număr de variante', { exact: true })
    .click();
  await page.getByLabel('Candidați', { exact: true }).fill('1');
  const evaluated = posted(page, `scenarios/${scenarioId}/staffing/evaluations`);
  await page.getByRole('button', { name: /^(Lansare|Reluare) optimizare$/ }).click();
  const { commandId } = (await evaluated).request().postDataJSON() as { commandId: string };
  const compare = page.getByRole('button', { name: 'Comparație rezultate', exact: true });
  await expect(compare).toBeEnabled({ timeout: 90_000 });
  await compare.click();
  await expect(
    page.getByRole('heading', { name: '4. Comparație rezultate', exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole('button', { name: 'Confirmare verificare și acces la decizie' }),
  ).toBeEnabled();
  return { diagnosticRunId: diagnostic.runId, evaluationId: commandId };
}

/** Returns from the comparison to the first decision step, ready for another surge. */
export async function backToIncident(page: Page) {
  await page
    .getByRole('navigation', { name: 'Etapele deciziei' })
    .getByRole('button', { name: /Configurare incident/ })
    .click();
}
