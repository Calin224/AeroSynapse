import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import {
  cp,
  mkdir,
  mkdtemp,
  readFile,
  stat,
  truncate,
  writeFile,
  appendFile,
  rm,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { setTimeout } from "node:timers/promises";
import { parseSecrets, saveJson } from "./local-release/lib.mjs";

const bundleIndex = process.argv.indexOf("--bundle");
if (bundleIndex < 0 || !process.argv[bundleIndex + 1])
  throw new Error(
    "Usage: node scripts/test-release.mjs --bundle <release-directory>",
  );
const source = path.resolve(process.argv[bundleIndex + 1]);
const sandbox = await mkdtemp(
  path.join(tmpdir(), "aerosynapse-release-smoke-"),
);
const bundle = path.join(sandbox, "portable");
const reports = path.resolve(
  "artifacts/release-smoke",
  new Date().toISOString().replace(/[:.]/g, "-"),
);
await mkdir(reports, { recursive: true });
await cp(source, bundle, { recursive: true });
const runtime = await import(pathToFileURL(path.join(bundle, "release.mjs")));
const live = [];
const evidence = {
  passed: false,
  checks: [],
  sourceRelease: (await runtime.verifyBundle(bundle)).releaseId,
};
const record = (name) => {
  evidence.checks.push(name);
  console.log(`[release-smoke] ${name}`);
};

async function client(target) {
  const base = (await runtime.status(target)).url;
  assert(base);
  const cookies = new Map();
  let csrf;
  async function call(route, method = "GET", value, extra = {}) {
    const response = await fetch(base + route, {
      method,
      headers: {
        ...(value === undefined ? {} : { "Content-Type": "application/json" }),
        Cookie: [...cookies].map(([key, val]) => `${key}=${val}`).join("; "),
        ...(csrf ? { "X-CSRF-TOKEN": csrf } : {}),
        ...extra,
      },
      body: value === undefined ? undefined : JSON.stringify(value),
      signal: AbortSignal.timeout(15_000),
    });
    for (const cookie of response.headers.getSetCookie()) {
      const first = cookie.split(";")[0],
        separator = first.indexOf("=");
      cookies.set(first.slice(0, separator), first.slice(separator + 1));
    }
    const result = await response.json();
    assert(response.ok, `HTTP ${response.status} at ${route}`);
    return result;
  }
  csrf = (await call("/api/auth/csrf")).requestToken;
  const secrets = parseSecrets(
    await readFile(path.join(target.state, ".env"), "utf8"),
  );
  await call("/api/auth/login", "POST", {
    username: "operator.demo",
    password: secrets.IDENTITY_DEMO_OPERATOR_PASSWORD,
  });
  csrf = (await call("/api/auth/csrf")).requestToken;
  return { call, base, secrets };
}

try {
  await assert.rejects(
    runtime.instance(bundle, path.join(bundle, "private-state"), {
      create: true,
    }),
    /outside the public release/,
  );
  const original = await runtime.instance(
    bundle,
    path.join(sandbox, "original"),
    { create: true, port: 0 },
  );
  live.push(original);
  await original.docker(
    ["image", "load", "--input", path.join(bundle, "images.tar")],
    { timeoutMs: 600_000 },
  );
  record("Archived images import locally without registry access");
  await runtime.up(original);
  const networkId = (
    await original.docker(
      [
        "network",
        "inspect",
        original.value.project + "_default",
        "--format",
        "{{.Internal}}",
      ],
      { quiet: true },
    )
  ).text.trim();
  assert.equal(networkId, "true");
  for (const service of ["api", "intelligence", "postgres"]) {
    const attached = JSON.parse(
      (
        await original.docker(
          [
            "inspect",
            `${original.value.project}-${service}-1`,
            "--format",
            "{{json .NetworkSettings.Networks}}",
          ],
          { quiet: true },
        )
      ).text,
    );
    assert.deepEqual(Object.keys(attached), [
      original.value.project + "_default",
    ]);
  }
  record(
    "Portable bundle starts without checkout or package installs, using an internal offline network",
  );
  // Fresh synthetic data may be reset repeatedly without hand-written SQL.
  for (let i = 0; i < 2; i++) {
    await original.compose(["stop", "frontend", "api", "intelligence"]);
    try {
      await original.compose([
        "run",
        "--rm",
        "--no-deps",
        "api",
        "--reset-synthetic",
      ]);
    } finally {
      await runtime.up(original);
    }
  }
  record("Fresh seed reset succeeds twice");
  let api = await client(original);
  const layout = await api.call(
    "/api/scenes/60000000-0000-0000-0000-000000000001/layout-versions",
    "POST",
  );
  const request = JSON.parse(
    await readFile(path.join(bundle, "demo-scenario.json"), "utf8"),
  );
  request.code = "release-" + randomUUID();
  request.layoutVersionId = layout.version.id;
  const saved = await api.call("/api/scenarios", "POST", request);
  const key = randomUUID();
  await api.call(
    `/api/scenarios/${saved.scenario.id}/simulations`,
    "POST",
    { scenarioRevision: 1 },
    { "Idempotency-Key": key },
  );
  let receipt;
  for (let i = 0; i < 120; i++) {
    receipt = await api.call("/api/simulations/" + key);
    if (receipt.status === "completed" || receipt.status === "failed") break;
    await setTimeout(250);
  }
  assert.equal(receipt.status, "completed");
  const before = await api.call("/api/runs/" + receipt.runId);
  assert.equal(before.resultsValid, true);
  assert.equal(before.id, receipt.runId);
  for (const key of ["inputHash", "resultHash", "engineBuild"])
    assert.equal(typeof before.provenance[key], "string", `Missing ${key}`);
  const beforeSnapshots = await api.call(
    "/api/runs/" + receipt.runId + "/snapshots",
  );
  assert.equal(beforeSnapshots.isValid, true);
  assert(beforeSnapshots.items.length > 0);
  const browserConfig = await (
    await fetch(api.base + "/runtime-config.js")
  ).text();
  for (const secret of Object.values(api.secrets))
    assert(
      !browserConfig.includes(secret),
      "Browser configuration contains a server credential.",
    );
  record(
    "Offline API-Python-DB simulation completes and browser configuration contains no credentials",
  );
  const checkpoint = path.join(sandbox, "private-checkpoint");
  await assert.rejects(
    runtime.backup(original, path.join(bundle, "private-backup")),
    /cannot be saved in the public release/,
  );
  record(
    "Private state and checkpoint paths inside the public artifact are refused",
  );
  await runtime.backup(original, checkpoint);
  await runtime.logs(original, path.join(reports, "diagnostics"));
  const diagnosticText = await readFile(
    path.join(reports, "diagnostics/services.log"),
    "utf8",
  );
  for (const secret of Object.values(api.secrets))
    assert(
      !diagnosticText.includes(secret),
      "Diagnostics contain a credential.",
    );
  record(
    "Quiesced PostgreSQL and Identity/key-volume checkpoint and redacted diagnostics created",
  );
  const dump = path.join(checkpoint, "database.dump");
  const dumpSize = (await stat(dump)).size;
  await appendFile(dump, "corruption");
  try {
    await assert.rejects(
      runtime.restore(bundle, path.join(sandbox, "rejected"), checkpoint, 0),
      /Checksum mismatch/,
    );
  } finally {
    await truncate(dump, dumpSize);
  }
  const exampleFile = path.join(bundle, "demo-scenario.json"),
    example = await readFile(exampleFile);
  await appendFile(exampleFile, "corruption");
  try {
    await assert.rejects(runtime.verifyBundle(bundle), /Checksum mismatch/);
  } finally {
    await writeFile(exampleFile, example);
  }
  const manifestFile = path.join(bundle, "manifest.json"),
    manifestBytes = await readFile(manifestFile);
  const modifiedManifest = JSON.parse(manifestBytes.toString());
  modifiedManifest.createdAtUtc = "2000-01-01T00:00:00.000Z";
  await writeFile(manifestFile, JSON.stringify(modifiedManifest));
  try {
    await assert.rejects(runtime.verifyBundle(bundle), /Checksum mismatch/);
  } finally {
    await writeFile(manifestFile, manifestBytes);
  }
  record(
    "Corrupted release files, manifest and backups are refused before restore",
  );
  await original.compose(["down", "--timeout", "15"]);
  const recoveredState = path.join(sandbox, "recovered");
  // Register the new instance for cleanup even if restore fails halfway through.
  let recovered;
  try {
    recovered = await runtime.restore(bundle, recoveredState, checkpoint, 0);
  } finally {
    try {
      live.push(await runtime.instance(bundle, recoveredState));
    } catch {}
  }
  api = await client(recovered);
  const after = await api.call("/api/runs/" + receipt.runId);
  assert.equal(after.resultsValid, true);
  assert.equal(after.id, before.id);
  for (const key of ["inputHash", "resultHash", "engineBuild"])
    assert.equal(after.provenance[key], before.provenance[key]);
  const afterSnapshots = await api.call(
    "/api/runs/" + receipt.runId + "/snapshots",
  );
  assert.equal(afterSnapshots.isValid, true);
  assert.deepEqual(afterSnapshots.items, beforeSnapshots.items);
  assert.deepEqual(after.metrics, before.metrics);
  record(
    "Rollback to matching images and a fresh restored instance preserves Identity login, runId, hashes and snapshots",
  );
  evidence.runId = receipt.runId;
  evidence.inputHash = after.provenance.inputHash;
  evidence.resultHash = after.provenance.resultHash;
  evidence.passed = true;
} catch (error) {
  evidence.error = error.message;
  console.error(`[release-smoke] ${error.message}`);
} finally {
  for (const target of live.reverse()) {
    try {
      await target.compose([
        "down",
        "--volumes",
        "--remove-orphans",
        "--timeout",
        "10",
      ]);
    } catch {
      evidence.passed = false;
      evidence.cleanupFailed = true;
    }
  }
  if (evidence.cleanupFailed) {
    evidence.retainedStateDirectory = sandbox;
    evidence.projects = live.map((target) => target.value.project);
    console.error(
      `[release-smoke] Cleanup failed; private state retained at ${sandbox}`,
    );
  }
  await saveJson(path.join(reports, "report.json"), evidence);
  if (
    path.dirname(sandbox) !== path.resolve(tmpdir()) ||
    !path.basename(sandbox).startsWith("aerosynapse-release-smoke-")
  )
    throw new Error("Unsafe temporary cleanup path.");
  if (!evidence.cleanupFailed) await rm(sandbox, { recursive: true }); // Only the mkdtemp-owned sandbox, after container/volume teardown.
}
console.log(`[release-smoke] ${evidence.passed ? "PASS" : "FAIL"}; ${reports}`);
process.exitCode = evidence.passed ? 0 : 1;
