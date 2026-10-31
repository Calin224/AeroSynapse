import { expect, test } from '@playwright/test';

const retired = ['/external-data', '/audit', '/admin/ai-governance', '/admin/users'];

for (const role of ['Viewer', 'Operator', 'Admin']) {
  test(`administration is removed from navigation and direct URLs for ${role}`, async ({
    page,
  }) => {
    const requests: string[] = [];
    await page.route('**/intelligence/health', (route) =>
      route.fulfill({ json: { status: 'Healthy' } }),
    );
    await page.route('**/api/**', (route) => {
      const path = new URL(route.request().url()).pathname;
      if (/^\/api\/(admin|audit|adapters)(\/|$)/.test(path)) requests.push(path);
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
      if (path === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
      if (path === '/api/scenarios') return route.fulfill({ json: [] });
      return route.fulfill({ status: 404, json: { title: 'No fixture data' } });
    });

    await page.goto('/operations');
    await expect(
      page.getByRole('heading', { name: 'Scenarii și rulări', exact: true }),
    ).toBeVisible();
    await expect(
      page.locator('.nav-group summary').filter({ hasText: 'Administrare' }),
    ).toHaveCount(0);
    for (const path of retired) await expect(page.locator(`a[href="${path}"]`)).toHaveCount(0);

    for (const path of retired) {
      await page.goto(path);
      await expect(
        page.getByRole('heading', { name: 'Pagina nu există', exact: true }),
      ).toBeVisible();
    }
    expect(requests).toEqual([]);
  });
}
