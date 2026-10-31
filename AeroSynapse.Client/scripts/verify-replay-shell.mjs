import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile, readdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { runInNewContext } from 'node:vm';

const root = resolve(process.env.REPLAY_DIST_PATH ?? 'dist/AeroSynapse.Client/browser');
const manifest = JSON.parse(await readFile(join(root, 'ngsw.json'), 'utf8'));
const shell = manifest.assetGroups.find((group) => group.name === 'app-shell');
assert.equal(shell?.installMode, 'prefetch');
assert.ok(shell.urls.includes('/index.html'));
assert.ok(shell.urls.includes('/runtime-config.js'));
assert.equal(
  manifest.dataGroups.length,
  0,
  'Business data must come from the explicit replay copy.',
);
for (const name of await readdir(root)) {
  if (
    /\.(css|js)$/.test(name) &&
    !['ngsw-worker.js', 'safety-worker.js', 'worker-basic.min.js'].includes(name)
  )
    assert.ok(shell.urls.includes('/' + name), `Missing offline bundle: ${name}`);
}
for (const url of shell.urls) {
  const bytes = await readFile(join(root, url.slice(1)));
  assert.equal(
    createHash('sha1').update(bytes).digest('hex'),
    manifest.hashTable[url],
    `Stale offline integrity entry: ${url}`,
  );
}
assert.ok((await readFile(join(root, 'ngsw-worker.js'), 'utf8')).length > 10000);
const visuals = manifest.assetGroups.find((group) => group.name === 'airport-visuals');
assert.equal(visuals?.installMode, 'lazy');
for (const path of ['/replay', '/replay?runId=demo', '/compare'])
  assert.ok(
    manifest.navigationUrls.some((rule) => rule.positive && new RegExp(rule.regex).test(path)),
  );
assert.ok(
  manifest.navigationUrls.some(
    (rule) => !rule.positive && new RegExp(rule.regex).test('/api/runs/demo/replay'),
  ),
);
if (process.env.EXPECT_RUNTIME_API) {
  const context = { window: {} };
  runInNewContext(await readFile(join(root, 'runtime-config.js'), 'utf8'), context);
  assert.equal(context.window.__AEROSYNAPSE_CONFIG__.apiBaseUrl, process.env.EXPECT_RUNTIME_API);
}
console.log(
  JSON.stringify({
    offlineShell: true,
    prefetchedFiles: shell.urls.length,
    lazyVisuals: visuals.urls.length,
    runtimeConfigIntegrity: true,
  }),
);
