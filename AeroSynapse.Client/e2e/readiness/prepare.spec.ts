import { expect, test } from '@playwright/test';
import {
  audit,
  capture,
  denyExternal,
  openReplay,
  type Capture,
  keyboardReach,
  login,
  read,
  reflow,
  save,
} from './support';
import {
  backToIncident,
  decide,
  posted,
  readEvaluation,
  saveCongestionScenario,
} from '../support/operator-workflow';

test.describe.configure({ mode: 'serial' });

test('timed Flux de decizie with internet denied, calculated evidence, modest load and render budget', async ({
  page,
  context,
}) => {
  test.setTimeout(300000);
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  const externalAttempts = await denyExternal(context);
  await login(page);
  const cookies = await context.cookies();
  const sessionCookie = cookies.find((cookie) => cookie.name === 'AeroSynapse.Session');
  expect(sessionCookie?.httpOnly).toBe(true);
  expect(sessionCookie?.sameSite).toBe('Strict');
  // This isolated stack is HTTP loopback/Testing. Production HTTPS cookie policy is covered by xUnit.
  expect(sessionCookie?.secure).toBe(false);
  const loginAudit = await context.request.get('/runtime-config.js');
  const runtime = await loginAudit.text();
  for (const role of ['OPERATOR', 'VIEWER'])
    expect(
      runtime.includes(process.env[`CRITICAL_${role}_PASSWORD`]!),
      'Runtime configuration must omit generated credentials.',
    ).toBe(false);
  const scenario = await saveCongestionScenario(page);
  expect(scenario.scenario.isSynthetic).toBe(true);
  const scenarioId: string = scenario.scenario.id;
  await page.goto(`/operator-workflow?scenarioId=${scenarioId}`);
  await expect(
    page.getByRole('heading', { name: '1. Configurare incident', exact: true }),
  ).toBeVisible();
  await audit(page, 'accessibility-incident');
  const measurements = [];
  let command: any;
  page.on('request', (request) => {
    if (
      request.method() === 'POST' &&
      /\/scenarios\/[a-f0-9-]+\/simulations$/.test(new URL(request.url()).pathname)
    ) {
      const match = new URL(request.url()).pathname.match(
        /\/scenarios\/([a-f0-9-]+)\/simulations$/,
      )!;
      command = {
        key: request.headers()['idempotency-key'],
        scenarioId: match[1],
        body: request.postDataJSON(),
      };
    }
  });
  let first: Capture | undefined;
  // A second, stronger surge on another flight must leave the first calculated evaluation intact.
  for (const [flightIndex, intensity] of [
    [0, 70],
    [1, 90],
  ]) {
    if (first) await backToIncident(page);
    const start = Date.now();
    const saved = posted(page, `scenarios/${scenarioId}/events`).then(() => Date.now());
    const ids = await decide(page, scenarioId, flightIndex, intensity);
    const totalMs = Date.now() - start;
    const savedMs = (await saved) - start;
    const value: Capture = {
      ...(await readEvaluation(page, ids.evaluationId)),
      ...ids,
      isSynthetic: true,
    };
    const left = value.baseline,
      right = value.candidate;
    expect(left.run.seed).toBe(42);
    expect(right.run.seed).toBe(42);
    expect(left.run.id).not.toBe(right.run.id);
    expect(value.comparison.comparability?.populationMatches).toBe(true);
    if (first) {
      expect(left.run.id).not.toBe(first.baseline.run.id);
      expect(left.run.provenance.inputHash).not.toBe(first.baseline.run.provenance.inputHash);
      const original = await readEvaluation(page, first.evaluationId);
      expect(original.baseline.run.provenance.resultHash).toBe(
        first.baseline.run.provenance.resultHash,
      );
      expect(original.candidate.run.provenance.resultHash).toBe(
        first.candidate.run.provenance.resultHash,
      );
    } else first = value;
    measurements.push({
      intensity,
      savedMs,
      totalMs,
      baselineRunId: left.run.id,
      candidateRunId: right.run.id,
      inputHash: left.run.provenance.inputHash,
      resultHash: left.run.provenance.resultHash,
      engineBuild: left.run.provenance.engineBuild,
      verdict: value.assessment.status,
    });
    expect(totalMs).toBeLessThanOrEqual(Number(process.env['QA_RUN_BUDGET_MS'] ?? 120000));
    await save('capture', value);
    await save('command', { ...command, runId: ids.diagnosticRunId });
  }
  await audit(page, 'accessibility-report');
  await reflow(page, 'presentation-reflow');
  await page.screenshot({ path: '/artifacts/report-synthetic.png', fullPage: true });
  const value = await capture();
  // Before and after at the same simulated minute and zone, read from the persisted replays.
  const before = await openReplay(page, value.baseline);
  const after = await openReplay(page, value.candidate, before.zoneId);
  const replayProof = {
    baselineRunId: before.runId,
    candidateRunId: after.runId,
    minute: before.minute,
    zoneId: before.zoneId,
    before: before.queued,
    after: after.queued,
  };
  // Visual budgets affect only rendering; the same captured KPI values remain selected.
  await page.locator('app-digital-twin').scrollIntoViewIfNeeded();
  await expect
    .poll(
      async () =>
        page.evaluate(() => {
          const measurement = document.querySelector<HTMLElement>('[data-testid=twin-performance]');
          return (
            !!document.querySelector('app-terminal-map') ||
            Number(measurement?.dataset['fps']) >= 30
          );
        }),
      { timeout: 35000 },
    )
    .toBe(true);
  const renderer = await page.evaluate(() => {
    const canvas = document.querySelector<HTMLCanvasElement>('.canvas-host canvas');
    const gl = canvas?.getContext('webgl2');
    const info = gl?.getExtension('WEBGL_debug_renderer_info');
    return {
      measurement: {
        ...document.querySelector<HTMLElement>('[data-testid=twin-performance]')?.dataset,
      },
      fallback2d: !!document.querySelector('app-terminal-map'),
      gpu: gl && info ? gl.getParameter(info.UNMASKED_RENDERER_WEBGL) : null,
      browser: navigator.userAgent,
      limitation:
        'Container Chromium renderer; native presentation GPU requires a separate measurement.',
    };
  });
  const delays: number[] = [];
  let count = 0;
  const runId = value.baseline.run.id;
  const loadStart = Date.now();
  await Promise.all(
    Array.from({ length: 5 }, async () => {
      for (let index = 0; index < 8; index++) {
        const start = Date.now();
        const response = await page.request.get(`/api/runs/${runId}`);
        expect(response.status()).toBe(200);
        expect((await response.json()).id).toBe(runId);
        delays.push(Date.now() - start);
        count++;
      }
    }),
  );
  delays.sort((a, b) => a - b);
  const load = {
    requests: count,
    concurrency: 5,
    milliseconds: Date.now() - loadStart,
    p50Ms: delays[Math.ceil(count * 0.5) - 1],
    p95Ms: delays[Math.ceil(count * 0.95) - 1],
    maxMs: delays.at(-1),
    errors: 0,
    scope: '40 reads of one owned synthetic run; no network scan or capacity certification',
  };
  expect(load.p95Ms).toBeLessThanOrEqual(Number(process.env['QA_READ_P95_MS'] ?? 2000));
  expect(externalAttempts).toEqual([]);
  expect(errors).toEqual([]);
  await save('measurements', {
    measurements,
    replayProof,
    renderer,
    load,
    externalAttempts,
    errors,
    cookies: {
      httpOnly: true,
      sameSite: 'Strict',
      secure: false,
      context: 'isolated HTTP loopback Testing',
    },
  });
});

test('WebGL refusal preserves persisted Time Travel, keyboard access and reflow', async ({
  browser,
}) => {
  const context = await browser.newContext({
    serviceWorkers: 'allow',
    viewport: { width: 1366, height: 768 },
    baseURL: process.env['CRITICAL_WEB_URL'],
  });
  await context.addInitScript(() => {
    const original = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function (
      this: HTMLCanvasElement,
      type: string,
      ...args: any[]
    ) {
      return type.includes('webgl') ? null : (original as any).call(this, type, ...args);
    } as typeof original;
  });
  const page = await context.newPage();
  const value = await capture();
  await login(page);
  const mutations: string[] = [];
  page.on('request', (request) => {
    if (request.method() !== 'GET' && request.method() !== 'HEAD')
      mutations.push(new URL(request.url()).pathname);
  });
  const proof = await openReplay(page, value.baseline);
  await expect(page.locator('app-terminal-map svg')).toBeVisible();
  await page.locator('app-digital-twin').getByRole('button', { name: '3D', exact: true }).click();
  await expect(page.locator('.fallback-status')).toContainText('Harta 2D');
  const toggle = page.locator('app-replay-player button.play');
  await keyboardReach(page, toggle);
  await page.keyboard.press('Enter');
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  const focus = await toggle.evaluate((element) => ({
    width: getComputedStyle(element).outlineWidth,
  }));
  expect(parseFloat(focus.width)).toBeGreaterThanOrEqual(2);
  await page.getByRole('button', { name: 'Pauză', exact: true }).click();
  const expand = page.getByRole('button', { name: 'Extinde', exact: true });
  await expand.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('dialog')).toBeVisible();
  for (let i = 0; i < 15; i++) {
    await page.keyboard.press('Tab');
    expect(await page.evaluate(() => !!document.activeElement?.closest('[role=dialog]'))).toBe(
      true,
    );
  }
  await page.keyboard.press('Escape');
  await expect(expand).toBeFocused();
  await audit(page, 'accessibility-2d-replay');
  await reflow(page, 'replay-reflow');
  expect(mutations).toEqual([]);
  await save('replay-accessibility', {
    passed: true,
    webglUnavailable: true,
    mutations,
    proof,
  });
  await context.close();
});

test('SignalR falls back to bounded reads and reconnects without mutations; basic access checks', async ({
  page,
  context,
  request,
}) => {
  const command = await read('command');
  const value = await capture();
  expect((await request.get(`/api/runs/${command.runId}`)).status()).toBe(401);
  await login(page);
  let refuse = true;
  const recoveryReads: string[] = [],
    mutations: string[] = [];
  await context.route('**/api/hubs/operations**', (route) =>
    refuse ? route.abort() : route.continue(),
  );
  page.on('request', (r) => {
    const path = new URL(r.url()).pathname;
    if (path.endsWith('/operations') && !path.includes('/hubs/')) recoveryReads.push(path);
    if (!['GET', 'HEAD'].includes(r.method()) && !path.includes('/hubs/')) mutations.push(path);
  });
  await page.goto(`/control-tower?scenarioId=${command.scenarioId}&runId=${command.runId}`);
  await expect(page.locator('[data-testid=tower-processed]')).not.toContainText('—');
  await expect(page.getByRole('region', { name: 'Status simulare' })).toHaveAttribute('data-connection', /polling|reconnecting/);
  const shown = await page.getByTestId('tower-processed').innerText();
  await expect.poll(() => recoveryReads.length).toBeGreaterThan(0);
  const resume = Date.now();
  refuse = false;
  await expect(page.getByRole('region', { name: 'Status simulare' })).toHaveAttribute('data-connection', 'live', {
    timeout: 30000,
  });
  await expect(page.getByTestId('tower-processed')).toHaveText(shown);
  await context.setOffline(true);
  await expect(page.locator('app-response-status [role=alert]')).toContainText('offline');
  await expect(
    page.getByRole('button', { name: 'Pornește simularea', exact: true }),
  ).toBeDisabled();
  await context.setOffline(false);
  await page.reload();
  await expect(page.getByRole('region', { name: 'Status simulare' })).toHaveAttribute('data-connection', 'live', {
    timeout: 30000,
  });
  await expect(page.getByTestId('tower-processed')).toHaveText(shown);
  expect(mutations).toEqual([]);
  await save('signalr-reconnection', {
    passed: true,
    recoveryMilliseconds: Date.now() - resume,
    recoveryReads: recoveryReads.length,
    mutations,
    runId: command.runId,
    noKpiRegression: true,
  });
  const csrf = (await (await page.request.get('/api/auth/csrf')).json()).requestToken;
  const noCsrf = await page.request.post(`/api/scenarios/${command.scenarioId}/simulations`, {
    data: { scenarioRevision: 1 },
  });
  expect(noCsrf.status()).toBe(400);
  await login(page, 'viewer');
  const viewerCsrf = (await (await page.request.get('/api/auth/csrf')).json()).requestToken;
  expect(
    (
      await page.request.post(`/api/scenarios/${command.scenarioId}/simulations`, {
        data: { scenarioRevision: 1 },
        headers: { 'X-CSRF-TOKEN': viewerCsrf },
      })
    ).status(),
  ).toBe(403);
  const invalid = await page.request.post('/api/contracts/v1/simulation/validate', {
    data: { unknownCommand: 'invalid' },
  });
  expect(invalid.status()).toBe(422);
  // Never store cookies, CSRF values or passwords in the proof.
  expect(csrf).toBeTruthy();
  expect(value.isSynthetic).toBe(true);
  await save('security', {
    anonymous: 401,
    viewerWrite: 403,
    csrfMissing: 400,
    invalidCommand: 422,
    scope: 'owned localhost only; basic checks, not a penetration test',
  });
});
