import { expect, test } from '@playwright/test';
import {
  backToIncident,
  decide,
  readEvaluation,
  saveCongestionScenario,
} from '../support/operator-workflow';

test('Flux de decizie runs real CP-SAT/SimPy and preserves verified results after changing surge', async ({
  page,
}) => {
  test.setTimeout(150_000);
  const password = process.env['CRITICAL_OPERATOR_PASSWORD'];
  if (!password) throw new Error('Use the isolated critical test runner.');
  await page.goto('/login');
  await page.getByLabel('Utilizator', { exact: true }).fill('operator.demo');
  await page.getByLabel('Parolă', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Intră în panou', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Control Tower', exact: true })).toBeVisible();

  const scenarioId: string = (await saveCongestionScenario(page)).scenario.id;

  await page.goto(`/operator-workflow?scenarioId=${scenarioId}`);
  await expect(
    page.getByRole('heading', { name: '1. Configurare incident', exact: true }),
  ).toBeVisible();
  const firstIds = await decide(page, scenarioId, 0, 70);
  const first = await readEvaluation(page, firstIds.evaluationId),
    left = first.baseline,
    right = first.candidate;
  expect(left.run.seed).toBe(42);
  expect(right.run.seed).toBe(42);
  expect(left.run.id).not.toBe(right.run.id);
  expect(left.captured.scenario.inputs.staffAllocations).toHaveLength(1);
  expect(right.captured.scenario.inputs.staffAllocations.length).toBeGreaterThan(1);
  const comparability = first.comparison.comparability;
  if (!comparability) throw new Error('Population comparability evidence is missing.');
  expect(comparability.populationMatches).toBe(true);
  expect(comparability.baselinePopulationHash).toBe(comparability.candidatePopulationHash);
  expect(first.assessment.status).toBe('improved');
  expect(first.evaluation.recommendedRunId).toBe(right.run.id);
  const diagnostic = await page.request.get(`/api/runs/${firstIds.diagnosticRunId}`);
  expect(diagnostic.ok()).toBe(true);
  // The evaluation's baseline repeats the diagnostic run's inputs and seed, so its result must match.
  expect((await diagnostic.json()).provenance.resultHash).toBe(left.run.provenance.resultHash);
  for (const run of [left.run, right.run]) {
    const stored = await page.request.get(`/api/runs/${run.id}`);
    expect(stored.ok()).toBe(true);
    expect((await stored.json()).provenance.resultHash).toBe(run.provenance.resultHash);
  }
  const shares = (data: typeof left) =>
    data.captured.scenario.inputs.events.map(
      (e: { payloadJson: string }) => JSON.parse(e.payloadJson).share,
    );
  expect(shares(left)).toEqual([0.7]);

  await backToIncident(page);
  const secondIds = await decide(page, scenarioId, 1, 90);
  const second = await readEvaluation(page, secondIds.evaluationId),
    updated = second.baseline;
  expect(secondIds.evaluationId).not.toBe(firstIds.evaluationId);
  expect(updated.run.id).not.toBe(left.run.id);
  expect(updated.run.provenance.resultHash).not.toBe(left.run.provenance.resultHash);
  expect(shares(updated)).toEqual([0.7, 0.9]);
  const original = await readEvaluation(page, firstIds.evaluationId);
  expect(original.baseline.run.provenance.resultHash).toBe(left.run.provenance.resultHash);
  expect(original.candidate.run.provenance.resultHash).toBe(right.run.provenance.resultHash);
  expect(shares(original.baseline)).toEqual([0.7]);
  await test.info().attach('operator-workflow-evidence', {
    contentType: 'application/json',
    body: JSON.stringify({
      scenarioId,
      first: { ...firstIds, assessment: first.assessment.status },
      second: { ...secondIds, assessment: second.assessment.status },
    }),
  });
});
