import assert from 'node:assert/strict';
import { readFileSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { Box3, Texture, Vector3 } from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { MeshoptDecoder } from 'three/addons/libs/meshopt_decoder.module.js';
const root = fileURLToPath(new URL('../public/', import.meta.url));
const manifest = JSON.parse(readFileSync(root + 'models/aerosynapse/manifest.json', 'utf8'));
const groups = {};
const byId = new Map(manifest.entities.map((e) => [e.entityId, e]));
assert.equal(manifest.synthetic, true);
assert.equal(manifest.model, 'user-supplied-airport');
assert.equal(manifest.source.file, 'airport-transparent-roof-no-lounge.blend');
assert.match(manifest.source.sha256, /^[a-f0-9]{64}$/);
assert.equal(manifest.source.sourceLicense, 'unverified');
const architecture = manifest.preservedGeometry.filter((entry) =>
  entry.source.startsWith('07.Modulo'),
);
assert.equal(architecture.length, 8, 'Four original modules plus their four separated roofs');
assert.equal(
  architecture.reduce((sum, entry) => sum + entry.faces, 0),
  37114,
);
assert.equal(manifest.units, 'meters');
assert.equal(manifest.gltfUp, 'Y');
assert.match(manifest.disclaimer, /nu reproduce.*Craiova/i);
let total = 0;
for (const [key, asset] of Object.entries(manifest.assets)) {
  const buffer = readFileSync(root + asset.url);
  assert.equal(buffer.readUInt32LE(0), 0x46546c67);
  assert.equal(buffer.readUInt32LE(4), 2);
  assert.equal(buffer.readUInt32LE(8), buffer.length);
  const jsonLength = buffer.readUInt32LE(12);
  const gltf = JSON.parse(buffer.toString('utf8', 20, 20 + jsonLength));
  assert.ok(!gltf.buffers.some((b) => b.uri), 'GLB must be self contained');
  assert.ok(gltf.extensionsRequired.includes('EXT_meshopt_compression'));
  const binaryOffset = 20 + jsonLength + 8;
  for (const texture of gltf.images) {
    assert.equal(texture.mimeType, 'image/png');
    assert.equal(texture.uri, undefined, 'Original palette is embedded');
    const view = gltf.bufferViews[texture.bufferView];
    const png = buffer.subarray(
      binaryOffset + (view.byteOffset ?? 0),
      binaryOffset + (view.byteOffset ?? 0) + view.byteLength,
    );
    assert.equal(png.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
    assert.equal(png.readUInt32BE(16), 256);
    assert.equal(png.readUInt32BE(20), 256);
  }
  const triangles = gltf.meshes.reduce(
    (n, m) => n + m.primitives.reduce((a, p) => a + gltf.accessors[p.indices].count / 3, 0),
    0,
  );
  assert.equal(triangles, asset.triangles);
  assert.ok(triangles < manifest.performance.maxTriangles);
  assert.equal(statSync(root + asset.url).size, asset.bytes);
  assert.ok(asset.bytes < manifest.performance.maxAssetBytes);
  total += asset.bytes;
  const array = buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength);
  // Node has no DOM image decoder. Geometry is decoded by the actual Three.js loader;
  // embedded palette bytes are checked above and their appearance is checked in-browser.
  const parsed = await new GLTFLoader()
    .setMeshoptDecoder(MeshoptDecoder)
    .register(() => ({
      name: 'NodePaletteVerification',
      loadTexture: () => Promise.resolve(new Texture()),
    }))
    .parseAsync(array, '');
  groups[key] = parsed.scene;
  parsed.scene.updateMatrixWorld(true);
  const codes = new Set();
  parsed.scene.traverse((node) => {
    if (!node.userData.clickable) return;
    const data = node.userData,
      expected = byId.get(data.entityId);
    assert.ok(expected, `Manifest mapping missing for ${node.name}`);
    assert.equal(data.synthetic, true);
    assert.equal(data.status, 'synthetic');
    assert.equal(data.zoneId, expected.databaseMapping.id);
    assert.ok(!codes.has(data.code), `Duplicate code ${data.code}`);
    codes.add(data.code);
    node
      .getWorldPosition(new Vector3())
      .toArray()
      .forEach((v, i) =>
        assert.ok(
          Math.abs(v - expected.positionThreeMeters[i]) < 0.0001,
          `${data.code}: transform mismatch`,
        ),
      );
  });
  if (key === 'terminal') {
    for (let n = 1; n <= 3; n++) assert.ok(codes.has(`CP-${n}`));
    for (let n = 1; n <= 5; n++) assert.ok(codes.has(`G-${n}`));
    const originalShell = new Box3();
    parsed.scene.traverse((node) => {
      if (node.isMesh && node.userData.sourceObject?.startsWith('07.Modulo')) {
        node.geometry.computeBoundingBox();
        originalShell.union(node.geometry.boundingBox.clone().applyMatrix4(node.matrixWorld));
      }
      assert.ok(!node.userData.sourceObject?.startsWith('13.Cafe'), 'Lounge/cafe removed');
    });
    const bounds = originalShell.getSize(new Vector3());
    const dimensions = manifest.terminalDimensionsMeters;
    // Meshopt's exponential filter quantizes vertices; metre scale and original placement remain.
    assert.ok(
      Math.abs(bounds.x - dimensions.width) < 0.1,
      'Original width retained within codec precision',
    );
    assert.ok(
      Math.abs(bounds.z - dimensions.depth) < 0.1,
      'Original depth retained within codec precision',
    );
    assert.ok(
      Math.abs(bounds.y - dimensions.roofHeight) < 0.1,
      'Original height converted Z-up to Y-up',
    );
    let roof;
    parsed.scene.traverse((n) => {
      if (n.userData.kind === 'roof') roof = n;
    });
    assert.ok(roof);
    let transparent = false;
    roof.traverse((n) => {
      if (n.isMesh)
        transparent ||= n.material.transparent && Math.abs(n.material.opacity - 0.08) < 0.001;
    });
    assert.ok(transparent, 'Roof transparency survives export');
  }
  console.log(
    `${key}: ${buffer.length} bytes, ${triangles} triangles; Three.js import + IDs + axes verified`,
  );
}
for (const gate of manifest.entities.filter((entity) => entity.zoneType === 'gate'))
  assert.ok(
    gate.sourceObjects.some((name) => name.startsWith('11.BoardingAreaDesk')),
    'Gate tags use the existing boarding desks',
  );
for (const control of manifest.entities.filter((entity) => entity.zoneType === 'checkpoint'))
  assert.deepEqual(control.sourceObjects, ['09.SecurityCheck_AIRPORT_0']);
const ids = (group) => {
  const found = [];
  group.traverse((n) => {
    if (n.userData.clickable) found.push(n.userData.entityId);
  });
  return found.sort();
};
assert.deepEqual(
  ids(groups.exteriorLow),
  ids(groups.exteriorDetail),
  'Exterior IDs must survive LOD changes',
);
assert.ok(total < manifest.performance.maxTotalBytes);
for (const e of manifest.entities.filter((e) => ['checkpoint', 'gate'].includes(e.zoneType))) {
  if (e.code.startsWith('BRIDGE')) continue;
  const n = Number(e.code.split('-')[1]);
  assert.equal(
    e.zoneId,
    `60000000-0000-0000-0000-${String((e.zoneType === 'checkpoint' ? 100 : 200) + n).padStart(12, '0')}`,
  );
}
console.log(
  `PASS: ${manifest.entities.length} stable entities; ${(total / 1024).toFixed(1)} KiB total; exterior LOD identities match.`,
);
