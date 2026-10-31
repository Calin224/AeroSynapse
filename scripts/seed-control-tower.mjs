import assert from 'node:assert/strict';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { setTimeout } from 'node:timers/promises';

// Add demo scenarios through the same validation and simulation APIs as Scenario Studio.
// Existing scenarios and runs are retained; stable codes make reruns additive and resumable.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const base = new URL(process.env.CONTROL_TOWER_API_URL ?? 'http://127.0.0.1:4200/api/');
assert(['127.0.0.1', 'localhost', '[::1]'].includes(base.hostname), 'Use a local demo API.');
const cookies = new Map();
let csrf;
async function call(route, method = 'GET', value, headers = {}) {
  const response = await fetch(new URL(route, base), {
    method,
    headers: {
      Cookie: [...cookies].map(([key, val]) => `${key}=${val}`).join('; '),
      ...(csrf ? { 'X-CSRF-TOKEN': csrf } : {}),
      ...(value === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...headers,
    },
    body: value === undefined ? undefined : JSON.stringify(value),
    signal: AbortSignal.timeout(30000),
  });
  for (const cookie of response.headers.getSetCookie()) {
    const first = cookie.split(';')[0], index = first.indexOf('=');
    cookies.set(first.slice(0, index), first.slice(index + 1));
  }
  const text = await response.text();
  const result = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(`HTTP ${response.status} ${route}: ${JSON.stringify(result)}`);
  return result;
}

try {
  const env = await readFile(path.join(root, '.env'), 'utf8');
  let password = process.env.IDENTITY_DEMO_OPERATOR_PASSWORD ??
    env.match(/^IDENTITY_DEMO_OPERATOR_PASSWORD=(.*)$/m)?.[1]?.trim();
  if (password?.startsWith('"') || password?.startsWith("'")) password = password.slice(1, -1);
  assert(password, 'Local operator demo password is missing.');
  csrf = (await call('auth/csrf')).requestToken;
  await call('auth/login', 'POST', { username: 'operator.demo', password });
  csrf = (await call('auth/csrf')).requestToken;
  const existing = await call('scenarios');
  const source = existing.find(row => row.id === 'fce201d7-6af3-4ac8-bb88-c84c8f7f2ac1') ?? existing[0];
  assert(source, 'Create or publish a demo layout first.');
  const layout = await call(`scenes/${source.sceneId}/layout-versions/${source.layoutVersionId}`);
  assert(layout.layout.scene.isSynthetic, 'The selected layout must be synthetic.');
  if (process.argv.includes('--inspect')) {
    console.log(JSON.stringify({ scenarios: existing.map(({ id, code, name }) => ({ id, code, name })),
      layout: { id: layout.version.id, gates: layout.layout.gates.length,
        checkpoints: layout.layout.checkpoints.length, staff: layout.layout.staff.length,
        checkInAreas: layout.layout.checkInAreas?.length ?? 0 } }, null, 2));
  } else {
    // Reuse the application's maintained preset generator, including its layout references.
    const require = createRequire(path.join(root, 'AeroSynapse.Client/package.json'));
    const ts = require('typescript');
    const sourceText = await readFile(path.join(root,
      'AeroSynapse.Client/src/app/features/scenario-studio/studio.models.ts'), 'utf8');
    const compiled = ts.transpileModule(sourceText, { compilerOptions: {
      target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022,
    } }).outputText;
    const { makeDraft } = await import('data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'));
    const specs = [
      ['CT-DEMO-CALM', 'Control Tower · Trafic redus', 'calm'],
      ['CT-DEMO-NORMAL', 'Control Tower · Operațiuni standard', 'principal'],
      ['CT-DEMO-PEAK', 'Control Tower · Vârf de trafic', 'peak'],
      ['CT-DEMO-BOTTLENECK', 'Control Tower · Blocaj la securitate', 'bottleneck'],
      ['CT-DEMO-STAFF', 'Control Tower · Securitate cu personal suplimentar', 'bottleneck'],
      ['CT-DEMO-WORKDAY', 'Control Tower · Zi de lucru de 8 ore', 'workday'],
      ...(layout.layout.checkInAreas?.length
        ? [['CT-DEMO-CHECKIN', 'Control Tower · Check-in cu personal redus', 'peak']]
        : [['CT-DEMO-SURGE', 'Control Tower · Val concentrat de sosiri', 'peak']]),
    ];
    const drafts = specs.map(([code, name, preset]) => {
      const draft = makeDraft(layout, preset, new Date('2026-10-30T06:00:00Z'));
      Object.assign(draft, { code, name });
      if (code === 'CT-DEMO-STAFF') {
        const first = draft.inputs.staffAllocations[0];
        const cp = layout.layout.checkpoints.find(row => row.id === first.checkpointId);
        const extra = layout.layout.staff.find(row => row.id !== first.staffMemberId && row.terminalId === cp.terminalId);
        assert(extra, 'Two staff members are required for the staffing comparison.');
        draft.inputs.staffAllocations.push({ staffMemberId: extra.id, checkpointId: cp.id });
        draft.description = 'Date sintetice: aceeași populație de 600 pasageri și același seed ca la blocaj, cu doi operatori la securitate.';
      }
      if (code === 'CT-DEMO-CHECKIN') {
        assert(draft.inputs.checkIn, 'A check-in area is required for this scenario.');
        draft.inputs.checkIn.staffCount = 2;
        draft.inputs.checkIn.holdBaggageShare = 0.7;
        draft.description = 'Date sintetice: 900 pasageri, două ghișee check-in deservite și 70% pasageri cu bagaj de cală.';
      }
      if (code === 'CT-DEMO-SURGE') {
        for (const flow of draft.inputs.flows) flow.endsAtUtc = new Date(
          Date.parse(flow.startsAtUtc) + 15 * 60000).toISOString();
        draft.description = 'Date sintetice: 900 pasageri sosesc în primele 15 minute, cu personalul standard la securitate.';
      }
      return draft;
    });
    // Validate every new draft before persisting any of them.
    for (const draft of drafts) if (!existing.some(row => row.code === draft.code))
      await call('scenarios/validate', 'POST', draft);
    const report = [];
    for (const draft of drafts) {
      let summary = existing.find(row => row.code === draft.code);
      const created = !summary;
      if (!summary) summary = (await call('scenarios', 'POST', draft)).scenario;
      console.log(`${created ? 'Created' : 'Retained'}: ${summary.name}`);
      const runs = await call(`scenarios/${summary.id}/runs?limit=100`);
      let run = runs.find(row => row.status === 'completed' && row.resultsValid) ??
        runs.find(row => ['queued', 'pending', 'running', 'submitted', 'reconciling'].includes(row.status));
      if (!run) {
        const execution = await call(`scenarios/${summary.id}/simulations`, 'POST',
          { scenarioRevision: summary.revision }, { 'Idempotency-Key': randomUUID() });
        assert(execution.runId, 'Simulation did not return a run ID.');
        run = { id: execution.runId };
      }
      report.push({ id: summary.id, code: summary.code, name: summary.name, created,
        runId: run.id, url: `http://127.0.0.1:4200/control-tower?scenarioId=${summary.id}&runId=${run.id}` });
    }
    const deadline = Date.now() + 240000;
    while (true) {
      for (const row of report) {
        if (row.status === 'completed' || row.status === 'failed') continue;
        const details = await call(`scenarios/${row.id}/runs/${row.runId}`);
        Object.assign(row, { status: details.status, resultsValid: details.resultsValid,
          failureReason: details.failureReason ?? details.error?.message });
      }
      if (report.every(row => ['completed', 'failed', 'cancelled'].includes(row.status)) || Date.now() > deadline) break;
      await setTimeout(3000);
    }
    const verified = await call('scenarios');
    assert(report.every(row => verified.some(item => item.id === row.id)), 'Catalog verification failed.');
    await mkdir(path.join(root, 'artifacts/control-tower'), { recursive: true });
    await writeFile(path.join(root, 'artifacts/control-tower/seed-report.json'), JSON.stringify(report, null, 2) + '\n');
    console.log(JSON.stringify(report, null, 2));
    assert(report.every(row => row.status === 'completed' && row.resultsValid), 'Some simulations did not complete with valid results. See seed-report.json.');
  }
} finally {
  if (csrf && cookies.size) await call('auth/logout', 'POST').catch(() => {});
}
