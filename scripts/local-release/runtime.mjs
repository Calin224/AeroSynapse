import { randomBytes } from "node:crypto";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import {
  hash,
  json,
  parseSecrets,
  password,
  run,
  saveJson,
  secretKeys,
  services,
  validateManifest,
  verifyFiles,
} from "./lib.mjs";

const directory = path.dirname(fileURLToPath(import.meta.url));
function inside(parent, candidate) {
  const relation = path.relative(path.resolve(parent), path.resolve(candidate));
  return (
    relation === "" ||
    (!relation.startsWith(".." + path.sep) &&
      relation !== ".." &&
      !path.isAbsolute(relation))
  );
}
const backupFiles = [".env", "database.dump", "api-data.tar.gz"];
const stamp = () =>
  new Date().toISOString().replace(/[:.]/g, "-") +
  "-" +
  randomBytes(3).toString("hex");

export async function verifyBundle(bundle = directory) {
  const manifest = validateManifest(
    await json(path.join(bundle, "manifest.json")),
  );
  const expectedInventory =
    [
      ...Object.entries(manifest.files).map(([name, sha]) => `${sha}  ${name}`),
      `${await hash(path.join(bundle, "manifest.json"))}  manifest.json`,
    ].join("\n") + "\n";
  if (
    (await readFile(path.join(bundle, "SHA256SUMS"), "utf8")) !==
    expectedInventory
  )
    throw new Error("Checksum mismatch: manifest or SHA256SUMS inventory.");
  await verifyFiles(bundle, manifest.files);
  return manifest;
}

export async function instance(bundle, stateDirectory, options = {}) {
  const manifest = await verifyBundle(bundle);
  const state = path.resolve(stateDirectory);
  if (inside(bundle, state))
    throw new Error(
      "Keep private instance state outside the public release directory.",
    );
  let value;
  try {
    value = await json(path.join(state, "instance.json"));
  } catch (error) {
    if (error.code !== "ENOENT" || !options.create) throw error;
    const port = Number(options.port ?? 8080);
    if (!Number.isInteger(port) || port < 0 || port > 65535)
      throw new Error("Port must be an integer from 0 to 65535.");
    await mkdir(path.dirname(state), { recursive: true });
    await mkdir(state, { mode: 0o700 }); // Restore/up never overwrite an unrelated directory.
    value = {
      schemaVersion: 1,
      releaseId: manifest.releaseId,
      project: "aerosynapse-local-" + randomBytes(6).toString("hex"),
      port,
    };
    await saveJson(path.join(state, "instance.json"), value);
    await writeFile(
      path.join(state, ".env"),
      options.restoreEnv ??
        secretKeys.map((key) => `${key}=${password()}`).join("\n") + "\n",
      { mode: 0o600, flag: "wx" },
    );
  }
  if (
    value.schemaVersion !== 1 ||
    !/^aerosynapse-local-[a-f0-9]{12}$/.test(value.project ?? "") ||
    !Number.isInteger(value.port) ||
    value.port < 0 ||
    value.port > 65535
  )
    throw new Error("Invalid local instance marker.");
  if (value.releaseId !== manifest.releaseId)
    throw new Error(
      "Use the release matching this instance. Restore a matching backup into a fresh instance for rollback.",
    );
  if (options.port !== undefined && Number(options.port) !== value.port)
    throw new Error("An existing instance retains its original port.");
  const credentials = parseSecrets(
    await readFile(path.join(state, ".env"), "utf8"),
  );
  const environment = {
    ...process.env,
    ...credentials,
    DEMO_PORT: String(value.port),
  };
  for (const service of services)
    environment[`RELEASE_${service.toUpperCase()}_IMAGE`] =
      manifest.images[service].id;
  const composeArgs = [
    "compose",
    "--project-name",
    value.project,
    "--env-file",
    path.join(state, ".env"),
    "-f",
    path.join(bundle, "compose.yaml"),
  ];
  const docker = (args, opts = {}) =>
    run("docker", args, {
      env: environment,
      secrets: Object.values(credentials),
      ...opts,
    });
  const compose = (args, opts = {}) => docker([...composeArgs, ...args], opts);
  return { bundle, state, manifest, value, compose, docker };
}

export async function loadImages(target) {
  const platform = (
    await target.docker(
      ["version", "--format", "{{.Server.Os}}/{{.Server.Arch}}"],
      { quiet: true },
    )
  ).text.trim();
  if (
    Object.values(target.manifest.images).some(
      (image) => image.platform !== platform,
    )
  )
    throw new Error(
      `Bundle platform differs from Docker server ${platform}. Prepare the bundle for this laptop architecture.`,
    );
  const ids = Object.values(target.manifest.images).map((image) => image.id);
  const present = await target.docker(["image", "inspect", ...ids], {
    quiet: true,
    allowFailure: true,
  });
  if (present.code !== 0)
    await target.docker(
      ["image", "load", "--input", path.join(target.bundle, "images.tar")],
      { timeoutMs: 600_000 },
    );
  await target.docker(["image", "inspect", ...ids], { quiet: true });
}

export async function status(target) {
  const text = (
    await target.compose(["ps", "--all", "--format", "json"], { quiet: true })
  ).text.trim();
  const rows = !text
    ? []
    : text.startsWith("[")
      ? JSON.parse(text)
      : text.split("\n").map((line) => JSON.parse(line));
  return {
    releaseId: target.manifest.releaseId,
    commit: target.manifest.commit,
    project: target.value.project,
    services: rows.map((row) => ({
      service: row.Service,
      state: row.State,
      health: row.Health,
    })),
    url: await url(target),
  };
}

async function url(target) {
  const port = await target.compose(["port", "frontend", "80"], {
    quiet: true,
    allowFailure: true,
  });
  const match = port.text.trim().match(/^127\.0\.0\.1:(\d+)$/);
  return match ? `http://127.0.0.1:${match[1]}` : null;
}

export async function up(target) {
  await loadImages(target);
  await target.compose(
    [
      "up",
      "-d",
      "--no-build",
      "--pull",
      "never",
      "--wait",
      "--wait-timeout",
      "180",
      "frontend",
    ],
    { timeoutMs: 210_000 },
  );
  const address = await url(target);
  if (!address) throw new Error("Frontend has no published loopback port.");
  console.log(`Synthetic demo ready: ${address}/control-tower`);
  console.log(`Flux de decizie presentation: ${address}/operator-workflow`);
  console.log(
    `Accounts: viewer.demo, operator.demo, admin.demo. Read passwords locally from ${path.join(target.state, ".env")}.`,
  );
}

export async function logs(target, destination) {
  const out = path.resolve(
    destination ?? path.join(target.state, "logs", stamp()),
  );
  await mkdir(path.dirname(out), { recursive: true });
  await mkdir(out, { mode: 0o700 });
  const output = await target.compose(
    ["logs", "--no-color", "--tail", "1000"],
    { quiet: true, captureStderr: true },
  );
  await writeFile(path.join(out, "services.log"), output.text, { mode: 0o600 });
  await saveJson(path.join(out, "status.json"), await status(target));
  console.log(`Redacted service diagnostics: ${out}`);
}

async function quiesce(target, action) {
  const current = await status(target);
  if (
    !services.every((service) =>
      current.services.some(
        (row) =>
          row.service === service &&
          row.state === "running" &&
          row.health === "healthy",
      ),
    )
  )
    throw new Error("Start a healthy instance before backup or seed reset.");
  await target.compose([
    "stop",
    "--timeout",
    "15",
    "frontend",
    "api",
    "intelligence",
  ]);
  try {
    return await action();
  } finally {
    await target.compose(
      [
        "up",
        "-d",
        "--no-build",
        "--pull",
        "never",
        "--wait",
        "--wait-timeout",
        "180",
        "frontend",
      ],
      { timeoutMs: 210_000 },
    );
  }
}

export async function backup(target, destination) {
  const out = path.resolve(
    destination ?? path.join(target.state, "backups", stamp()),
  );
  if (inside(target.bundle, out))
    throw new Error("Private backups cannot be saved in the public release.");
  await mkdir(path.dirname(out), { recursive: true });
  await mkdir(out, { mode: 0o700 });
  await quiesce(target, async () => {
    await copyFile(path.join(target.state, ".env"), path.join(out, ".env"));
    await target.compose(
      [
        "exec",
        "-T",
        "postgres",
        "pg_dump",
        "-U",
        "aerosynapse",
        "-d",
        "aerosynapse",
        "-Fc",
        "--no-owner",
        "--no-acl",
      ],
      { stdoutFile: path.join(out, "database.dump") },
    );
    await target.docker([
      "run",
      "--rm",
      "--network",
      "none",
      "--user",
      "0",
      "--mount",
      `type=volume,source=${target.value.project}_api-data,target=/data,readonly`,
      "--mount",
      `type=bind,source=${out},target=/backup`,
      "--entrypoint",
      "tar",
      target.manifest.images.postgres.id,
      "-czf",
      "/backup/api-data.tar.gz",
      "-C",
      "/data",
      ".",
    ]);
    const files = {};
    for (const file of backupFiles)
      files[file] = await hash(path.join(out, file));
    await saveJson(path.join(out, "backup.json"), {
      schemaVersion: 1,
      releaseId: target.manifest.releaseId,
      manifestHash: await hash(path.join(target.bundle, "manifest.json")),
      createdAtUtc: new Date().toISOString(),
      files,
    });
  });
  console.log(
    `Private checkpoint ready: ${out}. Keep it with the matching public release; do not upload it as a CI artifact.`,
  );
}

export async function restore(bundle, destination, source, port) {
  const manifest = await verifyBundle(bundle);
  const saved = await json(path.join(source, "backup.json"));
  if (
    saved.schemaVersion !== 1 ||
    saved.releaseId !== manifest.releaseId ||
    saved.manifestHash !== (await hash(path.join(bundle, "manifest.json"))) ||
    Object.keys(saved.files ?? {})
      .sort()
      .join() !== [...backupFiles].sort().join() ||
    Object.values(saved.files).some((v) => !/^[a-f0-9]{64}$/.test(v))
  )
    throw new Error("Backup does not match this release.");
  await verifyFiles(source, saved.files);
  const restoreEnv = await readFile(path.join(source, ".env"), "utf8");
  parseSecrets(restoreEnv);
  // Destination must be new; never perform a database downgrade or overwrite live data.
  try {
    await readFile(path.join(destination, "instance.json"));
    throw new Error("Restore requires a fresh state directory.");
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
  }
  const target = await instance(bundle, destination, {
    create: true,
    port,
    restoreEnv,
  });
  await loadImages(target);
  await target.compose([
    "up",
    "-d",
    "--no-build",
    "--pull",
    "never",
    "--wait",
    "--wait-timeout",
    "90",
    "postgres",
  ]);
  await target.compose(
    [
      "exec",
      "-T",
      "postgres",
      "pg_restore",
      "-U",
      "aerosynapse",
      "-d",
      "aerosynapse",
      "--clean",
      "--if-exists",
      "--no-owner",
      "--no-acl",
      "--exit-on-error",
    ],
    { stdinFile: path.join(source, "database.dump") },
  );
  await target.compose(["create", "--no-build", "--pull", "never", "api"]);
  const mount = `type=bind,source=${path.resolve(source)},target=/backup,readonly`;
  const entries = (
    await target.docker(
      [
        "run",
        "--rm",
        "--network",
        "none",
        "--mount",
        mount,
        "--entrypoint",
        "tar",
        manifest.images.postgres.id,
        "-tzf",
        "/backup/api-data.tar.gz",
      ],
      { quiet: true },
    )
  ).text
    .trim()
    .split("\n");
  if (
    entries.some(
      (name) => name.startsWith("/") || name.split("/").includes(".."),
    )
  )
    throw new Error("Unsafe backup archive path.");
  await target.docker([
    "run",
    "--rm",
    "--network",
    "none",
    "--user",
    "0",
    "--mount",
    `type=volume,source=${target.value.project}_api-data,target=/data`,
    "--mount",
    mount,
    "--entrypoint",
    "tar",
    manifest.images.postgres.id,
    "-xzf",
    "/backup/api-data.tar.gz",
    "-C",
    "/data",
  ]);
  await up(target);
  return target;
}

export async function main(args = process.argv.slice(2)) {
  const command = args.shift();
  const options = {};
  while (args.length) {
    const key = args.shift();
    if (
      !["--state", "--out", "--from", "--port"].includes(key) ||
      !args.length ||
      options[key]
    )
      throw new Error("Invalid release arguments.");
    options[key] = args.shift();
  }
  if (command === "verify") {
    const result = await verifyBundle();
    console.log(`Verified ${result.releaseId}`);
    return;
  }
  if (
    ![
      "up",
      "down",
      "status",
      "logs",
      "backup",
      "restore",
      "reset-seed",
    ].includes(command) ||
    !options["--state"]
  )
    throw new Error(
      "Usage: node release.mjs <up|down|status|logs|backup|restore|reset-seed> --state <private-directory> [--port 8080] [--out path] [--from backup]",
    );
  const state = path.resolve(options["--state"]);
  if (command === "restore") {
    if (!options["--from"])
      throw new Error("Restore requires --from <private-backup-directory>.");
    await restore(
      directory,
      state,
      path.resolve(options["--from"]),
      options["--port"],
    );
    return;
  }
  const target = await instance(directory, state, {
    create: command === "up",
    port: options["--port"],
  });
  if (command === "up") await up(target);
  if (command === "down") await target.compose(["down", "--timeout", "15"]); // Keep named volumes and evidence.
  if (command === "status")
    console.log(JSON.stringify(await status(target), null, 2));
  if (command === "logs") await logs(target, options["--out"]);
  if (command === "backup") await backup(target, options["--out"]);
  if (command === "reset-seed")
    await quiesce(target, () =>
      target.compose(["run", "--rm", "--no-deps", "api", "--reset-synthetic"]),
    );
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
