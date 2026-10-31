import { randomBytes } from "node:crypto";
import { copyFile, mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { hash, publicFiles, run, saveJson } from "./local-release/lib.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const args = process.argv.slice(2);
if (
  args.some(
    (arg, index) =>
      !["--out", "--allow-dirty"].includes(arg) && args[index - 1] !== "--out",
  )
)
  throw new Error(
    "Usage: node scripts/prepare-release.mjs --out <new-directory> [--allow-dirty]",
  );
const output = args.indexOf("--out");
if (output < 0 || !args[output + 1])
  throw new Error("--out must name a new release directory.");
const out = path.resolve(args[output + 1]);
const commit = (
  await run("git", ["rev-parse", "HEAD"], { cwd: root, quiet: true })
).text.trim();
const sourceDirty = Boolean(
  (
    await run("git", ["status", "--porcelain"], { cwd: root, quiet: true })
  ).text.trim(),
);
if (sourceDirty && !args.includes("--allow-dirty"))
  throw new Error(
    "Commit changes before packaging, or explicitly use --allow-dirty for a development bundle.",
  );
const releaseId = `${commit.slice(0, 12)}${sourceDirty ? "-dirty" : ""}-${randomBytes(4).toString("hex")}`;
await mkdir(path.dirname(out), { recursive: true });
await mkdir(out); // Never overwrite another release or its images archive.
console.log(`Preparing ${releaseId}; sourceDirty=${sourceDirty}`);
await run("docker", ["compose", "version"]);
const images = {};
const tags = [];
for (const [service, context, dockerfile] of [
  ["api", ".", "backend/Dockerfile"],
  ["intelligence", ".", "intelligence/Dockerfile"],
  ["frontend", "AeroSynapse.Client", "AeroSynapse.Client/Dockerfile"],
]) {
  const tag = `aerosynapse-release/${service}:${releaseId}`;
  await run(
    "docker",
    [
      "build",
      "--target",
      "runtime",
      "--label",
      `org.opencontainers.image.revision=${commit}`,
      "-f",
      dockerfile,
      "-t",
      tag,
      context,
    ],
    { cwd: root, timeoutMs: 1_200_000 },
  );
  tags.push(tag);
}
// Resolve the base once while online; the bundle records and runs its immutable ID.
await run("docker", ["pull", "postgres:17-alpine"], { timeoutMs: 300_000 });
tags.unshift("postgres:17-alpine");
const inspected = JSON.parse(
  (await run("docker", ["image", "inspect", ...tags], { quiet: true })).text,
);
for (const [index, service] of [
  "postgres",
  "api",
  "intelligence",
  "frontend",
].entries()) {
  const value = inspected[index];
  images[service] = {
    id: value.Id,
    platform: `${value.Os}/${value.Architecture}`,
  };
}
await run(
  "docker",
  ["image", "save", "--output", path.join(out, "images.tar"), ...tags],
  { timeoutMs: 600_000 },
);
for (const [source, destination] of [
  ["scripts/local-release/runtime.mjs", "release.mjs"],
  ["scripts/local-release/lib.mjs", "lib.mjs"],
  ["scripts/local-release/compose.yaml", "compose.yaml"],
  ["scripts/local-release/README.md", "README.md"],
  [".env.example", ".env.example"],
  ["backend/examples/scenario-request.json", "demo-scenario.json"],
])
  await copyFile(path.join(root, source), path.join(out, destination));
const files = {};
for (const file of publicFiles) files[file] = await hash(path.join(out, file));
const manifest = {
  schemaVersion: 1,
  releaseId,
  commit,
  sourceDirty,
  createdAtUtc: new Date().toISOString(),
  images,
  files,
};
await saveJson(path.join(out, "manifest.json"), manifest);
await writeFile(
  path.join(out, "SHA256SUMS"),
  [
    ...Object.entries(files).map(([name, sha]) => `${sha}  ${name}`),
    `${await hash(path.join(out, "manifest.json"))}  manifest.json`,
  ].join("\n") + "\n",
);
console.log(
  `Public release ready: ${out}. No instance credentials or database backups are included.`,
);
