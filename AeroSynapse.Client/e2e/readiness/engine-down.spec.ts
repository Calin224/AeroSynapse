import { expect, test } from '@playwright/test';
import { capture, login, openReplay, save } from './support';

test('actual FastAPI stop returns a bounded error while persisted UI and replay still work', async ({
  page,
}) => {
  await login(page);
  const start = Date.now();
  const response = await page.request.get('/intelligence/health');
  expect(response.status()).toBe(502);
  expect((await response.json()).code).toBe('intelligence_unavailable');
  expect(Date.now() - start).toBeLessThan(15000);
  expect((await page.request.get('/api/health/ready')).ok()).toBe(true);
  const proof = await openReplay(page, (await capture()).baseline);
  await save('engine-fallback', {
    passed: true,
    actualServiceStopped: 'intelligence',
    errorStatus: 502,
    apiStillHealthy: true,
    capturedReplay: proof,
  });
});
