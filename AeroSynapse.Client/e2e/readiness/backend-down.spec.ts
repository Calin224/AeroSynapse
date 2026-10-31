import { expect, test } from '@playwright/test';
import { save } from './support';

test('retired demo route remains unavailable during an API and engine crash', async ({
  page,
  request,
}) => {
  const unavailable = (await request.get('/api/health')).status();
  expect([502, 504]).toContain(unavailable);
  const mutations: string[] = [];
  page.on('request', (r) => {
    if (!['GET', 'HEAD'].includes(r.method())) mutations.push(new URL(r.url()).pathname);
  });
  const start = Date.now();
  await page.goto('/demo-replay');
  await expect(page.getByRole('heading', { name: 'Pagina nu există', exact: true })).toBeVisible();
  await expect(page.getByLabel('Importă captura JSON')).toHaveCount(0);
  await expect(page.getByTestId('bottleneck-result')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Pornește exemplul', exact: true })).toHaveCount(0);
  expect(mutations).toEqual([]);
  await save('backend-unavailable', {
    passed: true,
    actualServicesStopped: ['api', 'intelligence'],
    gatewayStatus: unavailable,
    retiredRouteResponseMs: Date.now() - start,
    mutations,
    retiredRouteUnavailable: true,
  });
});
