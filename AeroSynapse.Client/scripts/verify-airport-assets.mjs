import assert from 'node:assert/strict';
import { readFileSync, statSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { Box3, Raycaster, Texture, Vector3 } from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { MeshoptDecoder } from 'three/addons/libs/meshopt_decoder.module.js';
import { HDRLoader } from 'three/addons/loaders/HDRLoader.js';
import ts from 'typescript';
const root = fileURLToPath(new URL('../public/', import.meta.url));
const manifest = JSON.parse(readFileSync(root + 'models/aerosynapse/manifest.json', 'utf8'));
const groups = {};
const byId = new Map(manifest.entities.map((e) => [e.entityId, e]));
const fallbackSource = readFileSync(
  fileURLToPath(new URL('../src/app/features/digital-twin/terminal-zones.ts', import.meta.url)),
  'utf8',
);
const fallbackModule = ts.transpileModule(fallbackSource, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
}).outputText;
const { TERMINAL_MAP_ZONES } = await import(
  `data:text/javascript;base64,${Buffer.from(fallbackModule).toString('base64')}`
);
assert.equal(TERMINAL_MAP_ZONES.length, 11);
for (const zone of TERMINAL_MAP_ZONES) {
  const original = byId.get(zone.entityId);
  assert.ok(original, `2D fallback entity missing from GLB manifest: ${zone.code}`);
  for (const key of ['zoneId', 'zoneType', 'code']) assert.equal(zone[key], original[key]);
}
console.log(
  '2D fallback: 11 entities match the 3D manifest',
);
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
assert.equal(manifest.operationalLayout.version, 1);
assert.equal(manifest.operationalLayout.floorElevation, 0.45);
assert.equal(manifest.materialRefinement.version, 1);
assert.equal(manifest.materialRefinement.textures.textures.length, 4);
assert.ok(
  manifest.materialRefinement.textures.textures.every((asset) => asset.license === 'CC0-1.0'),
);
function imageSize(bytes, mimeType) {
  if (mimeType === 'image/png') {
    assert.equal(bytes.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
    return [bytes.readUInt32BE(16), bytes.readUInt32BE(20)];
  }
  assert.equal(mimeType, 'image/jpeg');
  assert.equal(bytes.readUInt16BE(0), 0xffd8);
  let offset = 2;
  while (offset < bytes.length) {
    assert.equal(bytes[offset++], 0xff);
    while (bytes[offset] === 0xff) offset++;
    const marker = bytes[offset++];
    const length = bytes.readUInt16BE(offset);
    if ([0xc0, 0xc1, 0xc2].includes(marker))
      return [bytes.readUInt16BE(offset + 5), bytes.readUInt16BE(offset + 3)];
    offset += length;
  }
  throw new Error('JPEG has no frame dimensions');
}
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
  let originalPalette = false;
  for (const texture of gltf.images) {
    assert.equal(texture.uri, undefined, 'Original palette is embedded');
    const view = gltf.bufferViews[texture.bufferView];
    const image = buffer.subarray(
      binaryOffset + (view.byteOffset ?? 0),
      binaryOffset + (view.byteOffset ?? 0) + view.byteLength,
    );
    const [width, height] = imageSize(image, texture.mimeType);
    assert.ok(
      width > 0 && height > 0 && width <= 1024 && height <= 1024,
      'Texture resolution budget',
    );
    originalPalette ||= texture.mimeType === 'image/png' && width === 256 && height === 256;
  }
  assert.ok(originalPalette, 'Original embedded palette retained');
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
    const contactMaterials = gltf.materials
      .map((m, i) => ({ m, i }))
      .filter(({ m }) => m.name.includes('contact AO'));
    assert.equal(contactMaterials.length, 5, 'Five actual seating AO bakes');
    for (const { m, i } of contactMaterials) {
      assert.equal(m.occlusionTexture.texCoord, 1, 'Contact maps use an independent UV set');
      const primitives = gltf.meshes
        .flatMap((mesh) => mesh.primitives)
        .filter((p) => p.material === i);
      assert.ok(
        primitives.length && primitives.every((p) => p.attributes.TEXCOORD_1 !== undefined),
      );
    }
    const texturedMaterials = gltf.materials.filter((m) => m.name.startsWith('Architectural'));
    assert.ok(
      texturedMaterials.filter((m) => m.normalTexture).length >= 4,
      'Physical surface normal maps must survive export',
    );
    assert.ok(
      texturedMaterials.filter((m) => m.pbrMetallicRoughness?.metallicRoughnessTexture).length >= 4,
      'Roughness maps must survive export',
    );
    assert.ok(
      texturedMaterials.filter((m) => m.pbrMetallicRoughness?.baseColorTexture).length >= 5,
      'Stone, wood, plaster, carpet and display textures must survive export',
    );
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
    parsed.scene.traverse((node) => {
      if (node.userData.viewMode)
        node.visible = ['interior', 'both'].includes(node.userData.viewMode);
    });
    const operationalMeshes = [];
    const zones = new Map();
    parsed.scene.traverseVisible((node) => {
      if (node.isMesh) operationalMeshes.push(node);
      if (node.userData.clickable) zones.set(node.userData.code, node);
    });
    assert.ok(zones.has('ENTRY') && zones.has('CHECK-IN') && zones.has('CORRIDOR'));
    assert.ok(zones.has('T-LAYOUT'));
    // Passengers pass check-in before security: both are labelled in the overview, with a caption.
    for (const code of ['CHECK-IN', 'CP-1', 'CP-2', 'CP-3']) {
      const { labelAnchorBlender, labelCaption } = zones.get(code).userData;
      assert.ok(Array.isArray(labelAnchorBlender), `${code} needs an overview label`);
      assert.ok(labelCaption, `${code} needs an overview label caption`);
    }
    assert.ok(!zones.has('T-DEMO'), 'Original shell must not obscure the operational interior');
    let previousGateX;
    for (const code of ['CP-1', 'CP-2', 'CP-3', 'G-1', 'G-2', 'G-3', 'G-4', 'G-5']) {
      const zone = zones.get(code);
      assert.ok(zone, `${code} must be visible in the interior`);
      assert.ok(Array.isArray(zone.userData.labelAnchorBlender), `${code} needs an overview label`);
      const equipment = zone.children.find((n) => n.userData.sourceObject);
      const equipmentBounds = new Box3().setFromObject(equipment);
      assert.ok(
        Math.abs(equipmentBounds.min.y - 0.45) < 0.05,
        `${code} grounded on the common floor`,
      );
      const p = zone.getWorldPosition(new Vector3());
      if (code.startsWith('G-')) {
        if (previousGateX !== undefined) assert.ok(Math.abs(p.x - previousGateX - 14) < 0.001);
        previousGateX = p.x;
        p.z = -36;
      }
      const hits = new Raycaster(
        new Vector3(p.x, 100, p.z),
        new Vector3(0, -1, 0),
      ).intersectObjects(operationalMeshes, false);
      assert.ok(hits.length, `${code} needs a clickable visible surface`);
      let owner = hits[0].object;
      while (owner && !owner.userData.clickable) owner = owner.parent;
      assert.equal(
        owner?.userData.code,
        code,
        `${code} must be selectable from above without a ceiling intercepting it`,
      );
    }
    const visibleTriangles = operationalMeshes.reduce(
      (sum, n) => sum + (n.geometry.index?.count ?? n.geometry.attributes.position.count) / 3,
      0,
    );
    assert.ok(
      visibleTriangles < manifest.performance.maxVisibleInteriorTriangles,
      'Architectural interior should stay within its visible geometry budget',
    );
    console.log(
      `Operational interior: ${visibleTriangles} visible triangles, ${operationalMeshes.length} meshes; 8 unobstructed zone picks`,
    );
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
for (const key of ['exteriorLow', 'exteriorDetail']) {
  const group = groups[key];
  const stands = [];
  group.traverse((node) => {
    if (node.userData.resource === 'stands' && node.userData.clickable) stands.push(node);
  });
  assert.equal(stands.length, 5, 'Five selectable synthetic stands in each LOD');
  for (const stand of stands) {
    assert.equal(stand.userData.status, 'synthetic');
    assert.ok(
      manifest.entities.some(
        (entity) => entity.zoneType === 'gate' && entity.zoneId === stand.userData.gateId,
      ),
    );
    const position = stand.getWorldPosition(new Vector3());
    // Pick the full parking envelope, away from the narrow markings and aircraft.
    const ray = new Raycaster(
      new Vector3(position.x + 12, 100, position.z + 22),
      new Vector3(0, -1, 0),
    );
    const hit = ray.intersectObject(stand, true)[0];
    assert.ok(hit, `${key}/${stand.userData.code}: stand surface must be pickable`);
  }
  assert.ok(manifest.assets[key].drawCallsUpperBound <= 80, 'Exterior mesh draw-call budget');
}
assert.ok(total < manifest.performance.maxTotalBytes);
const sky = readFileSync(root + manifest.environment.url);
assert.equal(sky.length, manifest.environment.bytes);
assert.equal(createHash('sha256').update(sky).digest('hex'), manifest.environment.sha256);
assert.equal(manifest.environment.license, 'CC0-1.0');
const hdr = new HDRLoader().parse(
  sky.buffer.slice(sky.byteOffset, sky.byteOffset + sky.byteLength),
);
assert.equal(hdr.width, 1024);
assert.equal(hdr.height, 512);
assert.ok(total + sky.length < manifest.performance.maxTotalBytes);
console.log(
  `Daylight HDRI: ${sky.length} bytes; decoded ${hdr.width} x ${hdr.height}; total transfer budget verified`,
);
const seed = readFileSync(
  fileURLToPath(
    new URL(
      '../../backend/src/AeroSynapse.Infrastructure/Storage/SyntheticScenarioSeeder.cs',
      import.meta.url,
    ),
  ),
  'utf8',
);
assert.match(seed, /60000000-0000-0000-0000-/);
// The check-in desks are the simulated zone the seeder creates as twin.check_in_areas ID 901.
const checkIn = manifest.entities.find((e) => e.code === 'CHECK-IN');
assert.deepEqual([checkIn.zoneId, checkIn.zoneType, checkIn.databaseMapping.table], ['60000000-0000-0000-0000-000000000901', 'checkIn', 'check_in_areas']);
assert.match(seed, /CheckInAreaId = Id\(901\)/);
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
