import { expect, type BrowserContext, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { readFile, writeFile } from 'node:fs/promises';
import type { readEvaluation } from '../support/operator-workflow';

/** Calculated evidence saved by the prepare phase and reused after the crashes. */
export type Capture = Awaited<ReturnType<typeof readEvaluation>> & {
  isSynthetic: true;
  diagnosticRunId: string;
  evaluationId: string;
};

export const save = (name: string, value: unknown) =>
  writeFile(`/artifacts/${name}.json`, JSON.stringify(value, null, 2));
export const read = async <T = any>(name: string): Promise<T> =>
  JSON.parse(await readFile(`/artifacts/${name}.json`, 'utf8'));
export async function capture(): Promise<Capture> {
  return read('capture');
}
export async function login(page: Page, role: 'operator' | 'viewer' = 'operator') {
  const password = process.env[`CRITICAL_${role.toUpperCase()}_PASSWORD`];
  if (!password) throw new Error('Use scripts/validate-demo.mjs for isolated credentials.');
  await page.goto('/login');
  await page.getByLabel('Utilizator', { exact: true }).fill(`${role}.demo`);
  await page.getByLabel('Parolă', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Intră în panou', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Control Tower', exact: true })).toBeVisible();
}
export async function openReplay(
  page: Page,
  data: Capture['baseline'],
  zoneId: string = data.captured.scenario.inputs.staffAllocations[0].checkpointId,
) {
  await page.goto('/replay?runId=' + data.run.id);
  const player = page.locator('section[aria-label="Time Travel și replay"]');
  await expect(player).toHaveAttribute('data-run-id', data.run.id);
  await page.getByLabel('Minutul replay-ului', { exact: true }).fill('10.5');
  await expect(player).toHaveAttribute('data-snapshot-minute', '10');
  const sample = data.samples.find(
    (row: { minute: number; zoneId: string }) => row.minute === 10 && row.zoneId === zoneId,
  );
  const kpis = page.getByRole('table', {
    name: 'KPI din aceeași captură de timp · minutul 10',
    exact: true,
  });
  await expect(kpis.locator('tr[data-zone-id="' + zoneId + '"] [data-kpi=queued]')).toHaveText(
    String(sample.queuedPassengers),
  );
  return { runId: data.run.id, minute: 10.5, zoneId, queued: sample.queuedPassengers };
}
export async function audit(page: Page, name: string) {
  const result = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'])
    .analyze();
  await save(name, {
    tool: 'axe-core',
    version: result.testEngine.version,
    violations: result.violations,
    incomplete: result.incomplete,
    passes: result.passes.map((rule) => rule.id),
    limitation: 'Automated checks only; manual screen-reader/user testing remains required.',
  });
  expect(
    result.violations,
    `Accessibility violations in ${name}; see complete local report`,
  ).toEqual([]);
}
export async function keyboardReach(page: Page, target: ReturnType<Page['locator']>) {
  for (let index = 0; index < 160; index++) {
    if (await target.evaluate((el) => el === document.activeElement)) return;
    await page.keyboard.press('Tab');
  }
  throw new Error('The required presentation control is not keyboard reachable.');
}
export async function reflow(page: Page, name: string) {
  const sizes = [
    [1366, 768],
    [1280, 720],
    [390, 844],
    [320, 800],
  ];
  const proof = [];
  for (const [width, height] of sizes) {
    await page.setViewportSize({ width, height });
    const actual = await page.evaluate(() => document.documentElement.scrollWidth);
    proof.push({ width, height, scrollWidth: actual });
    expect(
      actual,
      `Page overflow at ${width}px; tables may scroll inside their own containers`,
    ).toBeLessThanOrEqual(width);
  }
  await page.setViewportSize({ width: 1366, height: 768 });
  await save(name, proof);
}
export async function denyExternal(context: BrowserContext) {
  const attempts: string[] = [];
  await context.route('**/*', (route) => {
    const url = new URL(route.request().url());
    if (
      ['http:', 'https:'].includes(url.protocol) &&
      !['127.0.0.1', 'localhost'].includes(url.hostname)
    ) {
      attempts.push(url.origin);
      return route.abort();
    }
    return route.continue();
  });
  return attempts;
}
