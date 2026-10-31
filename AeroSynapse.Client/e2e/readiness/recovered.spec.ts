import { expect, test } from '@playwright/test';
import { capture, login, read, save } from './support';

test('restart preserves calculated runs and retrying the same accepted command creates no duplicate', async ({
  page,
}) => {
  await login(page);
  const command = await read('command'),
    value = await capture();
  const csrf = (await (await page.request.get('/api/auth/csrf')).json()).requestToken;
  const before = await (
    await page.request.get(`/api/scenarios/${command.scenarioId}/runs?limit=100`)
  ).json();
  const retry = await page.request.post(`/api/scenarios/${command.scenarioId}/simulations`, {
    data: command.body,
    headers: { 'Idempotency-Key': command.key, 'X-CSRF-TOKEN': csrf },
  });
  expect(retry.status()).toBe(202);
  expect((await retry.json()).runId).toBe(command.runId);
  const after = await (
    await page.request.get(`/api/scenarios/${command.scenarioId}/runs?limit=100`)
  ).json();
  expect(after.length).toBe(before.length);
  for (const { run } of [value.baseline, value.candidate]) {
    const stored = await (await page.request.get(`/api/runs/${run.id}`)).json();
    expect(stored.resultsValid).toBe(true);
    expect(stored.provenance.resultHash).toBe(run.provenance.resultHash);
  }
  expect((await page.request.get('/intelligence/health')).ok()).toBe(true);
  expect((await page.request.get('/api/health?qa_token=readiness-log-probe')).ok()).toBe(true);
  await save('restart-recovery', {
    passed: true,
    actualServicesRestarted: ['api', 'intelligence'],
    sameCommandSameRun: true,
    runId: command.runId,
    beforeCount: before.length,
    afterCount: after.length,
    persistedHashesUnchanged: true,
    queryTokenProbeSent: true,
  });
});
