import assert from "node:assert/strict";
import { test } from "node:test";
import { mkdtemp, writeFile, unlink, rmdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import {
  publicFiles,
  secretKeys,
  password,
  parseSecrets,
  redact,
  validateManifest,
  verifyFiles,
  hash,
  run,
} from "./lib.mjs";

test("release inventory refuses traversal, missing runtime files and mutable image tags", () => {
  const value = {
    schemaVersion: 1,
    releaseId: "abc123",
    commit: "a".repeat(40),
    sourceDirty: false,
    files: Object.fromEntries(
      publicFiles.map((file) => [file, "b".repeat(64)]),
    ),
    images: Object.fromEntries(
      ["postgres", "api", "intelligence", "frontend"].map((name) => [
        name,
        { id: "sha256:" + "c".repeat(64), platform: "linux/amd64" },
      ]),
    ),
  };
  assert.equal(validateManifest(value), value);
  for (const change of [
    (v) => {
      v.files["../.env"] = "b".repeat(64);
    },
    (v) => {
      delete v.files["lib.mjs"];
    },
    (v) => {
      v.images.api.id = "api:latest";
    },
  ]) {
    const invalid = structuredClone(value);
    change(invalid);
    assert.throws(() => validateManifest(invalid));
  }
});

test("private configuration accepts only generated server secrets and rejects Compose interpolation", () => {
  const lines = secretKeys.map((key) => `${key}=${password()}`);
  const values = parseSecrets(lines.join("\n"));
  assert.equal(Object.keys(values).length, 4);
  assert.equal(new Set(Object.values(values)).size, 4);
  assert.throws(() => parseSecrets(lines.slice(1).join("\n")));
  assert.throws(() =>
    parseSecrets(lines.join("\n") + "\nRELEASE_API_IMAGE=attacker:latest"),
  );
  assert.throws(() =>
    parseSecrets(lines.join("\n").replace(/=[^\n]+/, "=${HOST_SECRET}")),
  );
  assert(
    !redact(Object.values(values).join(" "), Object.values(values)).includes(
      values.POSTGRES_PASSWORD,
    ),
  );
  assert.equal(
    redact("Authorization: Bearer private-token"),
    "Authorization: Bearer [REDACTED]",
  );
});

test("diagnostics retain both process streams with credentials redacted", async () => {
  const secret = password();
  const result = await run(
    process.execPath,
    [
      "-e",
      "console.log('output'); console.error('error ' + process.env.DEMO_SECRET)",
    ],
    {
      env: { ...process.env, DEMO_SECRET: secret },
      secrets: [secret],
      quiet: true,
      captureStderr: true,
    },
  );
  assert.match(result.text, /output/);
  assert.match(result.text, /error \[REDACTED\]/);
  assert(!result.text.includes(secret));
});

test("file integrity refuses a changed artifact", async () => {
  const directory = await mkdtemp(
    path.join(tmpdir(), "aerosynapse-integrity-"),
  );
  const file = path.join(directory, "artifact");
  try {
    await writeFile(file, "reviewed");
    const inventory = { artifact: await hash(file) };
    await verifyFiles(directory, inventory);
    await writeFile(file, "corrupted");
    await assert.rejects(
      verifyFiles(directory, inventory),
      /Checksum mismatch/,
    );
  } finally {
    await unlink(file);
    await rmdir(directory);
  }
});
