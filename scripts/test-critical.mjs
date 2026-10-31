import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { createWriteStream } from "node:fs";
import {
  mkdir,
  mkdtemp,
  readFile,
  rmdir,
  unlink,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

// Docker is the only runtime prerequisite besides Node. Nothing connects to an
// existing demo: no published ports, shared volumes, fixed project or credentials.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const suffix = `${new Date().toISOString().replace(/[:.]/g, "-")}-${randomBytes(3).toString("hex")}`;
const project = `aerosynapse-critical-${randomBytes(6).toString("hex")}`;
const artifacts = path.join(root, "artifacts", "critical", suffix);
for (const directory of ["dotnet", "python", "angular", "browser"])
  await mkdir(path.join(artifacts, directory), { recursive: true });
const secretDir = await mkdtemp(path.join(tmpdir(), "aerosynapse-critical-"));
const secretFile = path.join(secretDir, "runtime.env");
const compose = [
  "compose",
  "--project-name",
  project,
  "--env-file",
  secretFile,
  "-f",
  path.join(root, "compose.critical-tests.yaml"),
];
let active;
let cancelled = false;
let exitCode = 1;
let failure;
const steps = [];
const secrets = ["DB", "OPERATOR", "VIEWER", "ADMIN"].map((role) => [
  `CRITICAL_${role}_PASSWORD`,
  randomBytes(32).toString("hex") + "aA1!",
]);
const environment = {
  ...process.env,
  ...Object.fromEntries(secrets),
  CRITICAL_ARTIFACTS: artifacts,
  COMPOSE_PARALLEL_LIMIT: "2",
};

for (const signal of ["SIGINT", "SIGTERM"])
  process.on(signal, () => {
    cancelled = true;
    active?.kill("SIGTERM");
  });
async function docker(args, label, seconds = 300, required = true) {
  if (cancelled && required) throw new Error("Suite cancelled.");
  console.log(`[critical] ${label}`);
  const log = createWriteStream(path.join(artifacts, `${label}.log`));
  const started = Date.now();
  const child = spawn("docker", args, {
    cwd: root,
    env: environment,
    shell: false,
    stdio: ["ignore", "pipe", "pipe"],
  });
  active = child;
  // Redact generated secrets even if a future test or Docker error echoes one.
  const redact = (text) =>
    secrets.reduce(
      (value, [, secret]) => value.replaceAll(secret, "[REDACTED]"),
      text,
    );
  // Buffer partial lines so a secret split between chunks cannot bypass redaction.
  const forward = (output) => {
    let pending = "";
    output.on("data", (chunk) => {
      pending += chunk.toString();
      const lines = pending.split("\n");
      pending = lines.pop();
      for (const line of lines) {
        const safe = redact(line) + "\n";
        log.write(safe);
        process.stdout.write(safe);
      }
    });
    output.on("end", () => {
      if (pending) {
        const safe = redact(pending) + "\n";
        log.write(safe);
        process.stdout.write(safe);
      }
    });
  };
  forward(child.stdout);
  forward(child.stderr);
  let timedOut = false;
  const deadline = setTimeout(() => {
    timedOut = true;
    child.kill("SIGTERM");
  }, seconds * 1000);
  const code = await new Promise((resolve) => {
    child.once("error", () => resolve(127));
    child.once("close", (code) => resolve(code ?? 1));
  });
  clearTimeout(deadline);
  active = undefined;
  await new Promise((resolve) => log.end(resolve));
  steps.push({
    label,
    exitCode: code,
    milliseconds: Date.now() - started,
    timedOut,
  });
  if (required && (code !== 0 || timedOut || cancelled))
    throw new Error(
      `${label} ${timedOut ? "timed out" : "failed"} (exit ${code}).`,
    );
  return timedOut ? 1 : code;
}

async function verifyReport(relativePath, counterTag, minimum) {
  const xml = await readFile(path.join(artifacts, relativePath), "utf8");
  const counters = [
    ...xml.matchAll(new RegExp(`<${counterTag}\\b[^>]*>`, "g")),
  ];
  const attribute = (tag, name) =>
    Number(tag.match(new RegExp(`\\b${name}="(\\d+)"`))?.[1] ?? NaN);
  const totalName = counterTag === "testsuite" ? "tests" : "total";
  const failureNames =
    counterTag === "testsuite"
      ? ["failures", "errors", "skipped"]
      : ["failed", counterTag === "Counters" ? "notExecuted" : "skipped"];
  const total = counters.reduce(
    (sum, [tag]) => sum + attribute(tag, totalName),
    0,
  );
  if (
    !Number.isFinite(total) ||
    total < minimum ||
    counters.some(([tag]) =>
      failureNames.some((name) => attribute(tag, name) !== 0),
    )
  )
    throw new Error(
      `${relativePath} must prove execution of at least ${minimum} passing tests, with no skips.`,
    );
}
try {
  await writeFile(
    secretFile,
    secrets.map(([key, value]) => `${key}=${value}`).join("\n") + "\n",
    { mode: 0o600 },
  );
  await docker(["compose", "version"], "prerequisites", 30);
  await docker(
    [
      ...compose,
      "build",
      "api",
      "intelligence",
      "frontend",
      "dotnet-tests",
      "python-tests",
      "browser-tests",
    ],
    "build",
    1200,
  );
  await docker(
    [...compose, "up", "-d", "--wait", "--wait-timeout", "180", "frontend"],
    "startup",
    210,
  );
  for (const service of [
    "dotnet-tests",
    "python-tests",
    "angular-tests",
    "browser-tests",
  ]) {
    await docker(
      [...compose, "run", "--no-deps", "--rm", service],
      service,
      service === "dotnet-tests" ? 600 : 300,
    );
    if (service === "dotnet-tests") {
      await verifyReport("dotnet/domain-api.trx", "Counters", 1);
      await verifyReport("dotnet/contracts.xml", "assembly", 9);
      await verifyReport("dotnet/domain-management.xml", "assembly", 1);
    }
    if (service === "angular-tests")
      await verifyReport("angular/results.xml", "testsuite", 1);
  }
  const browser = JSON.parse(
    await readFile(path.join(artifacts, "browser", "results.json"), "utf8"),
  );
  if (
    browser.stats.skipped ||
    browser.stats.unexpected ||
    browser.stats.flaky ||
    browser.stats.expected < 5
  )
    throw new Error(
      "Browser smoke must execute at least five passing tests without skips or retries.",
    );
  exitCode = 0;
} catch (error) {
  failure = error.message;
  console.error(`[critical] ${error.message}`);
} finally {
  await docker(
    [...compose, "logs", "--no-color", "--tail", "1000"],
    "services",
    30,
    false,
  );
  const cleanup = await docker(
    [...compose, "down", "--volumes", "--remove-orphans", "--timeout", "10"],
    "cleanup",
    60,
    false,
  );
  if (cleanup !== 0 || cancelled) exitCode = 1;
  await unlink(secretFile).catch((error) => {
    if (error.code !== "ENOENT") throw error;
  });
  await rmdir(secretDir); // Exact owned file and now-empty directory; no recursive deletion.
  await writeFile(
    path.join(artifacts, "summary.json"),
    JSON.stringify(
      { project, passed: exitCode === 0, cancelled, failure, steps },
      null,
      2,
    ),
  );
}
console.log(
  `[critical] ${exitCode === 0 ? "PASS" : "FAIL"} — reports: ${artifacts}`,
);
process.exitCode = exitCode;
