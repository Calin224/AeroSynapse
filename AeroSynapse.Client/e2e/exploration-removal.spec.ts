import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const retired = ['/passenger', '/evacuation-training', '/operation-bottleneck'];

for (const role of ['Viewer', 'Operator', 'Admin']) {
  test(`retired exploration pages are absent from menus, search and direct URLs for ${role}`, async ({
    page,
  }) => {
    const featureRequests: string[] = [];
    const errors: string[] = [];
    page.on('pageerror', (error) => errors.push(error.message));
    page.on('request', (request) => {
      const path = new URL(request.url()).pathname;
      if (/^\/(?:api\/passenger-feedback|intelligence\/evacuation)(?:\/|$)/.test(path))
        featureRequests.push(path);
    });
    await page.route('**/api/**', (route) => {
      const path = new URL(route.request().url()).pathname;
      if (path === '/api/auth/session')
        return route.fulfill({
          json: {
            username: 'workspace-test',
            roles: [role],
            tenantId: 'demo',
            expiresAtUtc: '2099-01-01T00:00:00Z',
          },
        });
      if (path === '/api/auth/csrf') return route.fulfill({ json: { requestToken: 'test-only' } });
      if (path === '/api/scenarios') return route.fulfill({ json: [] });
      return route.fulfill({ status: 404, json: { title: 'No fixture data' } });
    });

    await page.goto('/operations');
    await expect(
      page.getByRole('heading', { name: 'Scenarii și rulări', exact: true }),
    ).toBeVisible();
    for (const path of retired) await expect(page.locator(`a[href^="${path}"]`)).toHaveCount(0);
    await expect(page.locator('.nav-group summary').filter({ hasText: 'Explorare' })).toHaveCount(
      0,
    );

    await page.keyboard.press('Control+k');
    const search = page.getByRole('combobox', { name: 'Caută pagini și instrumente' });
    for (const query of ['Zborul meu', 'evacuare', 'Mai mulți operatori reduc coada']) {
      await search.fill(query);
      await expect(page.getByRole('heading', { name: 'Nicio destinație găsită' })).toBeVisible();
    }
    await search.press('Escape');

    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: 'Deschide meniul', exact: true }).click();
    const drawer = page.getByRole('dialog', { name: 'Meniu de navigare' });
    await expect(drawer).toBeVisible();
    for (const path of retired) await expect(drawer.locator(`a[href^="${path}"]`)).toHaveCount(0);
    await page.keyboard.press('Escape');

    for (const path of retired) {
      await page.goto(`${path}?scenarioId=retired&runId=retired`);
      await expect(
        page.getByRole('heading', { name: 'Pagina nu există', exact: true }),
      ).toBeVisible();
      await expect(
        page.getByRole('link', { name: 'Înapoi la Control Tower', exact: true }),
      ).toHaveAttribute('href', '/control-tower');
      const accessibility = await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
        .analyze();
      expect(accessibility.violations).toEqual([]);
    }
    expect(featureRequests).toEqual([]);
    expect(errors).toEqual([]);
  });
}
