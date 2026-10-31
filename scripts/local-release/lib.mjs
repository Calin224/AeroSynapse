import { spawn } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { createReadStream } from "node:fs";
import { open, readFile, writeFile } from "node:fs/promises";

export const services = ["postgres", "api", "intelligence", "frontend"];
export const publicFiles = [
  "release.mjs",
  "lib.mjs",
  "compose.yaml",
  "README.md",
  ".env.example",
  "demo-scenario.json",
  "images.tar",
];
export const secretKeys = [
  "POSTGRES_PASSWORD",
  "IDENTITY_DEMO_VIEWER_PASSWORD",
  "IDENTITY_DEMO_OPERATOR_PASSWORD",
  "IDENTITY_DEMO_ADMIN_PASSWORD",
];
export const password = () => randomBytes(32).toString("hex") + "aA1!";
export const json = async (file) => JSON.parse(await readFile(file, "utf8"));
export const saveJson = (file, value) =>
  writeFile(file, JSON.stringify(value, null, 2) + "\n", {
    mode: 0o600,
    flag: "wx",
  });
export async function hash(file) {
  const result = createHash("sha256");
  for await (const chunk of createReadStream(file)) result.update(chunk);
  return result.digest("hex");
}

export function parseSecrets(text) {
  const result = {};
  for (const line of text.trim().split(/\r?\n/)) {
    const match = line.match(/^([A-Z_]+)=([a-f0-9]{64}aA1!)$/);
    if (!match || !secretKeys.includes(match[1]) || result[match[1]])
      throw new Error("Invalid private release configuration.");
    result[match[1]] = match[2];
  }
  if (Object.keys(result).length !== secretKeys.length)
    throw new Error("Incomplete private release configuration.");
  return result;
}

export function redact(text, secrets = []) {
  return secrets
    .reduce((value, secret) => value.replaceAll(secret, "[REDACTED]"), text)
    .replace(
      /(authorization\s*[:=]\s*(?:bearer|basic)\s+)\S+/gi,
      "$1[REDACTED]",
    );
}

// No shell interpolation; binary dumps/archives use file descriptors, including on PowerShell 5.
export async function run(command, args, options = {}) {
  const input = options.stdinFile ? await open(options.stdinFile, "r") : null;
  const output = options.stdoutFile
    ? await open(options.stdoutFile, "wx", 0o600)
    : null;
  const chunks = [];
  let timedOut = false;
  let timer;
  let hardStop;
  try {
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: options.env ?? process.env,
      shell: false,
      stdio: [input?.fd ?? "ignore", output?.fd ?? "pipe", "pipe"],
    });
    const forward = (stream, collect) => {
      let pending = "";
      const emit = (text) => {
        const safe = redact(text, options.secrets);
        if (collect) chunks.push(safe);
        if (!options.quiet) process.stdout.write(safe);
      };
      stream?.on("data", (chunk) => {
        pending += chunk.toString();
        const lines = pending.split("\n");
        pending = lines.pop();
        for (const line of lines) emit(line + "\n");
      });
      stream?.on("end", () => {
        if (pending) emit(pending);
      });
    };
    forward(child.stdout, true);
    forward(child.stderr, Boolean(options.captureStderr));
    timer = setTimeout(() => {
      timedOut = true;
      child.kill("SIGTERM");
      hardStop = setTimeout(() => child.kill("SIGKILL"), 5000);
    }, options.timeoutMs ?? 180_000);
    const code = await new Promise((resolve, reject) => {
      child.once("error", reject);
      child.once("close", (code) => resolve(code ?? 1));
    });
    if ((code !== 0 || timedOut) && !options.allowFailure)
      throw new Error(
        `${command} ${args[0] ?? ""} ${timedOut ? "timed out" : `failed (exit ${code})`}.`,
      );
    return { code, text: chunks.join("") };
  } finally {
    clearTimeout(timer);
    clearTimeout(hardStop);
    await input?.close();
    await output?.close();
  }
}

export function validateManifest(value) {
  if (
    value.schemaVersion !== 1 ||
    !/^[a-z0-9][a-z0-9.-]{0,80}$/.test(value.releaseId ?? "") ||
    !/^[a-f0-9]{40}$/.test(value.commit ?? "") ||
    typeof value.sourceDirty !== "boolean"
  )
    throw new Error("Unsupported or invalid release manifest.");
  if (
    Object.keys(value.files ?? {})
      .sort()
      .join() !== [...publicFiles].sort().join() ||
    Object.values(value.files).some((v) => !/^[a-f0-9]{64}$/.test(v))
  )
    throw new Error(
      "Release file inventory must match the supported public files.",
    );
  if (
    Object.keys(value.images ?? {})
      .sort()
      .join() !== [...services].sort().join() ||
    Object.values(value.images).some(
      (v) =>
        !/^sha256:[a-f0-9]{64}$/.test(v.id ?? "") ||
        !/^linux\/(amd64|arm64)$/.test(v.platform ?? ""),
    )
  )
    throw new Error(
      "Release must contain four content-addressed Linux images.",
    );
  return value;
}

export async function verifyFiles(root, inventory) {
  for (const [name, digest] of Object.entries(inventory)) {
    if ((await hash(root + "/" + name)) !== digest)
      throw new Error(`Checksum mismatch: ${name}.`);
  }
}
