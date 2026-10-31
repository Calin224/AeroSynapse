import { test, expect, Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { ids, towerFixture } from '../src/app/features/control-tower/tower.testing';

const evidence = resolve('..', 'artifacts', 'experience');
const towerUrl = `/control-tower?scenarioId=${ids.scenario}&runId=${ids.run}`;
const errors = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const messages: string[] = [];
  errors.set(page, messages);
  page.on('pageerror', (error) => messages.push(error.message));
  await mkdir(evidence, { recursive: true });
});
test.afterEach(({ page }) => expect(errors.get(page)).toEqual([]));

async function workspace(page: Page, role: 'Operator' | 'Viewer' | null = 'Operator') {
  const fixture = towerFixture();
  await page.addInitScript(() => {
    const original = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function (
      this: HTMLCanvasElement,
      kind: string,
      ...args: any[]
    ) {
      return kind.includes('webgl') ? null : (original as any).call(this, kind, ...args);
    } as typeof original;
  });
  await page.route('**/intelligence/health', (route) =>
    route.fulfill({ json: { status: 'Healthy' } }),
  );
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/session')
      return role
        ? route.fulfill({
            json: {
              username: 'operator.demo',
              roles: [role],
              tenantId: 'demo',
              expiresAtUtc: '2099-01-01T00:00:00Z',
            },
          })
        : route.fulfill({ status: 401 });
    if (path === '/api/auth/csrf')
      return route.fulfill({ json: { requestToken: 'test-only-csrf' } });
    if (path === '/api/auth/login') return route.fulfill({ status: 401 });
    if (path === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    if (path === '/api/scenarios') return route.fulfill({ json: [fixture.scenario.scenario] });
    if (path === `/api/scenarios/${ids.scenario}`) return route.fulfill({ json: fixture.scenario });
    if (path === `/api/scenarios/${ids.scenario}/runs`)
      return route.fulfill({ json: [fixture.run] });
    if (path === `/api/scenarios/${ids.scenario}/runs/${ids.run}`)
      return route.fulfill({ json: fixture.run });
    if (path === `/api/scenes/${ids.scene}/layout-versions/${ids.layout}`)
      return route.fulfill({ json: fixture.layout });
    if (path === '/api/scenes') return route.fulfill({ json: [fixture.layout.layout.scene] });
    if (path === `/api/scenarios/${ids.scenario}/operations`)
      return route.fulfill({
        json: {
          scenarioId: ids.scenario,
          sequence: 0,
          resetRequired: false,
          runs: [],
          run: null,
          optimization: null,
        },
      });
    return route.fulfill({ status: 404, json: { title: 'No fixture for this endpoint' } });
  });
  return fixture;
}
async function checkAccessibility(page: Page) {
  await page.evaluate(async () => {
    await Promise.all(
      document
        .getAnimations()
        .filter((animation) => Number.isFinite(animation.effect?.getComputedTiming().endTime))
        .map((animation) => animation.finished.catch(() => undefined)),
    );
  });
  const scan = await new AxeBuilder({ page }).analyze();
  expect(
    scan.violations.map(({ id, nodes }) => ({
      id,
      nodes: nodes.map(({ target, failureSummary }) => ({ target, failureSummary })),
    })),
  ).toEqual([]);
}
async function ready(page: Page) {
  await page.goto(towerUrl);
  await expect(page.getByTestId('tower-processed')).toHaveText('12');
  await expect(page.locator('app-tower-trends input[type=range]')).toBeVisible();
}

test('login, password visibility, failure recovery and reduced motion', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await workspace(page, null);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/login');
  await expect(page.getByRole('heading', { name: 'Autentificare', exact: true })).toBeVisible();
  await page.getByLabel('Utilizator', { exact: true }).fill('operator.demo');
  await page.getByLabel('Parolă', { exact: true }).fill('not-a-real-password');
  await page.getByRole('button', { name: 'Arată parola', exact: true }).click();
  await expect(page.locator('#password')).toHaveAttribute('type', 'text');
  await page.getByRole('button', { name: 'Intră în panou', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Autentificarea nu a reușit');
  await expect(page.locator('#password')).toHaveValue('');
  await expect(page.locator('#password')).toHaveAttribute('type', 'password');
  expect(
    await page
      .locator('.flow-path')
      .evaluate((el) => parseFloat(getComputedStyle(el).animationDuration)),
  ).toBeLessThan(0.01);
  await checkAccessibility(page);
  await page.screenshot({ path: resolve(evidence, 'login-desktop.png'), fullPage: true });
});

test('search supports keyboard, accents, empty results, escape and navigation', async ({
  page,
}) => {
  await workspace(page);
  await ready(page);
  const trigger = page.getByRole('button', { name: 'Caută o pagină Ctrl K' });
  await trigger.click();
  const search = page.getByRole('combobox', { name: 'Caută pagini și instrumente' });
  await expect(search).toBeFocused();
  await search.fill('rulari scenarii');
  await expect(page.getByRole('listbox', { name: 'Destinații' }).getByRole('option')).toHaveCount(
    1,
  );
  await expect(page.getByRole('listbox', { name: 'Destinații' }).getByRole('option')).toContainText(
    'Scenarii și rulări',
  );
  await search.fill('zzzzzz');
  await expect(page.getByRole('heading', { name: 'Nicio destinație găsită' })).toBeVisible();
  await search.press('Enter');
  await expect(page).toHaveURL(towerUrl);
  await page.getByRole('button', { name: 'Șterge căutarea' }).click();
  await expect(search).toBeFocused();
  await search.press('ArrowDown');
  await expect(search).toHaveAttribute('aria-activedescendant', 'destination-1');
  await checkAccessibility(page);
  await page.screenshot({ path: resolve(evidence, 'command-palette.png') });
  await search.fill('raport');
  await search.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(trigger).toBeFocused();
  await page.keyboard.press('Control+k');
  await expect(search).toBeVisible();
  await search.fill('rulari scenarii');
  await search.press('Enter');
  await expect(page).toHaveURL(`/operations?scenarioId=${ids.scenario}&runId=${ids.run}`);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.keyboard.press('Control+k');
  await expect(page.locator('.results-caption')).toContainText('Vizitate recent');
});

test('timeline inspection preserves run KPIs and result links preserve context', async ({ page }) => {
  await workspace(page);
  await ready(page);
  const slider = page.getByRole('slider', { name: 'Explorează cronologia' });
  await slider.focus();
  await slider.press('Home');
  await expect(slider).toHaveAttribute('aria-valuetext', 'Minutul 0: 20 în coadă, 0 procesați');
  await slider.press('ArrowRight');
  await expect(slider).toHaveAttribute('aria-valuetext', 'Minutul 2: 16 în coadă, 4 procesați');
  await expect(page.getByTestId('tower-processed')).toHaveText('12');
  await slider.press('End');
  await expect(slider).toHaveValue('3');
  await expect(page.locator('.quick-guide')).toHaveCount(0);
  await expect(page.getByTestId('tower-connection')).toHaveCount(0);
});

test('Viewer has no editing commands and direct routes remain guarded', async ({ page }) => {
  await workspace(page, 'Viewer');
  await ready(page);
  await expect(page.getByRole('button', { name: 'Pornește simularea', exact: true })).toBeHidden();
  await page.keyboard.press('Control+k');
  await page
    .getByRole('combobox', { name: 'Caută pagini și instrumente' })
    .fill('Creează un scenariu');
  await expect(page.getByRole('listbox', { name: 'Destinații' }).getByRole('option')).toHaveCount(
    0,
  );
  await page.goto('/scenario-studio');
  await expect(page).toHaveURL(/access-denied$/);
});

test('loading, retry, empty and partial data remain usable', async ({ page }) => {
  const fixture = await workspace(page);
  let release!: () => void;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  let fail = true;
  await page.route('**/api/scenarios', async (route) => {
    await pending;
    return fail
      ? route.fulfill({ status: 503, json: { title: 'Unavailable' } })
      : route.fulfill({ json: [] });
  });
  await page.goto('/control-tower');
  await expect(page.locator('.loading[role=status]')).toBeVisible();
  await expect(page.locator('#tower-scenario')).toBeDisabled();
  release();
  await expect(page.locator('.notice[role=alert]')).toBeVisible();
  fail = false;
  await page.getByRole('button', { name: 'Reîncearcă', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Niciun scenariu disponibil' })).toBeVisible();
  const createScenario = page.getByRole('link', { name: 'Deschide Scenario Studio', exact: true });
  await expect(createScenario).toBeVisible();
  await expect(createScenario).toHaveAttribute('href', '/scenario-studio');
  await page.route('**/api/scenarios', (route) =>
    route.fulfill({ json: [fixture.scenario.scenario] }),
  );
  await page.route(`**/api/scenarios/${ids.scenario}/runs?*`, (route) =>
    route.fulfill({ json: [] }),
  );
  await page.reload();
  await expect(page.getByTestId('tower-processed')).toHaveText('—');
  await expect(page.locator('app-tower-trends')).toContainText(
    'Nicio observație completă.',
  );
  await page.screenshot({ path: resolve(evidence, 'partial-data.png'), fullPage: true });
});

for (const [width, height] of [
  [1440, 900],
  [390, 844],
  [320, 800],
  [1366, 768],
]) {
  test(`workspace reflows and remains accessible at ${width}x${height}`, async ({ page }) => {
    await page.setViewportSize({ width, height });
    await workspace(page);
    await ready(page);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      width,
    );
    if (width < 1101) {
      const menu = page.getByRole('button', { name: 'Deschide meniul' });
      await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
      await expect(menu).toBeInViewport();
      await menu.click();
      await expect(menu).toHaveAttribute('aria-expanded', 'true');
      await expect(page.getByRole('dialog', { name: 'Meniu de navigare' })).toBeVisible();
      expect(await page.locator('body').evaluate(el => getComputedStyle(el).overflow)).toBe('hidden');
      await checkAccessibility(page);
      for (let i = 0; i < 12; i++) {
        await page.keyboard.press('Tab');
        expect(await page.evaluate(() => !!document.activeElement?.closest('dialog[open]'))).toBe(
          true,
        );
      }
      await page.keyboard.press('Escape');
      await expect(menu).toBeFocused();
      await expect(menu).toHaveAttribute('aria-expanded', 'false');
      await menu.click();
      await page.getByRole('button', { name: 'Caută o pagină', exact: true }).click();
      await expect(
        page.getByRole('combobox', { name: 'Caută pagini și instrumente' }),
      ).toBeFocused();
      await page.keyboard.press('Escape');
      await expect(menu).toBeFocused();
    }
    if (width === 1440 || width === 390) {
      await checkAccessibility(page);
      await page.screenshot({
        path: resolve(evidence, `tower-${width}x${height}.png`),
        fullPage: true,
      });
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      width,
    );
  });
}
