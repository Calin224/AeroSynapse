import { randomBytes } from "node:crypto";
import {
  mkdir,
  mkdtemp,
  readFile,
  writeFile,
  unlink,
  rmdir,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { run } from "./local-release/lib.mjs";
import { readinessMarkdown } from "./readiness/markdown.mjs";
import { localDockerEndpoint } from "./readiness/scope.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
if (process.argv.slice(2).some((arg) => arg !== "--presentation-device"))
  throw new Error(
    "Usage: node scripts/validate-demo.mjs [--presentation-device] (creates its own synthetic stack; no target URL accepted)",
  );
function budget(name, fallback, min, max) {
  const value = Number(process.env[name] ?? fallback);
  if (!Number.isInteger(value) || value < min || value > max)
    throw new Error(`Invalid ${name}.`);
  return value;
}
const thresholds = {
  runBudgetMs: budget("QA_RUN_BUDGET_MS", 120000, 1000, 180000),
  readP95Ms: budget("QA_READ_P95_MS", 2000, 100, 15000),
  minimumFps: 30,
  loadRequests: 40,
  loadConcurrency: 5,
  status: "provisional; presentation team must accept budgets",
};
const stamp = new Date().toISOString().replace(/[:.]/g, "-");
const project = "aerosynapse-readiness-" + randomBytes(6).toString("hex");
const output = path.join(root, "artifacts", "readiness", stamp);
await mkdir(path.join(output, "browser"), { recursive: true });
const temporary = await mkdtemp(
  path.join(os.tmpdir(), "aerosynapse-readiness-"),
);
const secretFile = path.join(temporary, "runtime.env");
const secrets = Object.fromEntries(
  ["DB", "OPERATOR", "VIEWER", "ADMIN"].map((role) => [
    `CRITICAL_${role}_PASSWORD`,
    randomBytes(32).toString("hex") + "aA1!",
  ]),
);
await writeFile(
  secretFile,
  Object.entries(secrets)
    .map(([key, value]) => `${key}=${value}`)
    .join("\n") + "\n",
  { mode: 0o600 },
);
const environment = {
  ...process.env,
  ...secrets,
  CRITICAL_ARTIFACTS: output,
  COMPOSE_PARALLEL_LIMIT: "2",
  QA_RUN_BUDGET_MS: String(thresholds.runBudgetMs),
  QA_READ_P95_MS: String(thresholds.readP95Ms),
};
const compose = [
  "compose",
  "--project-name",
  project,
  "--env-file",
  secretFile,
  "-f",
  path.join(root, "compose.critical-tests.yaml"),
  "-f",
  path.join(root, "compose.readiness.yaml"),
];
const report = {
  schemaVersion: 1,
  project,
  recordedAtUtc: new Date().toISOString(),
  automatedPassed: false,
  presentationApproved: false,
  machineRole: process.argv.includes("--presentation-device")
    ? "presentation-device-declared-by-operator"
    : "development; this is not approved as the presentation laptop",
  sourceCommit: "",
  sourceDirty: true,
  host: {
    platform: os.platform(),
    release: os.release(),
    arch: os.arch(),
    cpu: os.cpus()[0]?.model,
    logicalCpus: os.cpus().length,
    memoryGiB: Math.round(os.totalmem() / 2 ** 30),
  },
  thresholds,
  isolation: {
    internalDockerNetwork: false,
    externalTargetsAllowed: false,
    hostPortsPublished: false,
  },
  steps: [],
  manualPending: [
    "Run the same acceptance on the presentation laptop with the intended browser/projector",
    "Record native GPU FPS or explicitly choose 2D; container FPS is not a hardware GPU certification",
    "Assign named presenter/operator/recovery owner and acknowledge the five-minute rehearsal",
    "Accept timing budgets and perform screen-reader/projector checks",
  ],
  failure: undefined,
};
let interrupted = false;
let scopeVerified = false;
for (const signal of ["SIGINT", "SIGTERM"])
  process.on(signal, () => {
    interrupted = true;
  });
async function docker(args, label, timeoutMs = 180000, required = true) {
  if (interrupted && required) throw new Error("Validation interrupted.");
  console.log(`[readiness] ${label}`);
  const start = Date.now();
  const result = await run("docker", args, {
    cwd: root,
    env: environment,
    secrets: Object.values(secrets),
    captureStderr: true,
    quiet: false,
    allowFailure: true,
    timeoutMs,
  });
  await writeFile(path.join(output, label + ".log"), result.text);
  report.steps.push({
    label,
    exitCode: result.code,
    milliseconds: Date.now() - start,
  });
  if (required && (result.code !== 0 || interrupted))
    throw new Error(
      `${label} failed (exit ${result.code}). Inspect its redacted log.`,
    );
  return result;
}
async function ownServices() {
  for (const service of ["api", "intelligence", "frontend"]) {
    const result = await docker(
      [...compose, "ps", "--quiet", service],
      "owned-" + service,
      30000,
    );
    const id = result.text.trim();
    if (!/^[a-f0-9]{12,64}$/.test(id))
      throw new Error("Cannot identify owned test service.");
    const label = await docker(
      [
        "inspect",
        id,
        "--format",
        '{{index .Config.Labels "com.docker.compose.project"}}',
      ],
      "label-" + service,
      30000,
    );
    if (label.text.trim() !== project)
      throw new Error("Refusing fault injection on an unowned project.");
    const ports = await docker(["port", id], "ports-" + service, 30000);
    if (ports.text.trim())
      throw new Error("The readiness stack must not publish host ports.");
    const image = await docker(
      ["inspect", id, "--format", "{{.Image}}"],
      "image-" + service,
      30000,
    );
    report.images ??= {};
    report.images[service] = image.text.trim();
  }
}
async function browser(phase, minimum) {
  environment.READINESS_PHASE = phase;
  await docker(
    [...compose, "run", "--rm", "--no-deps", "browser-tests"],
    "browser-" + phase,
    phase === "prepare" ? 540000 : 180000,
  );
  const result = JSON.parse(
    await readFile(path.join(output, "browser", phase, "results.json"), "utf8"),
  );
  if (
    result.stats.expected < minimum ||
    result.stats.skipped ||
    result.stats.unexpected ||
    result.stats.flaky
  )
    throw new Error(
      `Incomplete ${phase} browser evidence; no skips or retries are allowed.`,
    );
}
try {
  report.sourceCommit = (
    await run("git", ["rev-parse", "HEAD"], { cwd: root, quiet: true })
  ).text.trim();
  report.sourceDirty = !!(
    await run("git", ["status", "--porcelain", "--untracked-files=no"], {
      cwd: root,
      quiet: true,
    })
  ).text.trim();
  if (environment.DOCKER_HOST && !localDockerEndpoint(environment.DOCKER_HOST))
    throw new Error(
      "Remote Docker endpoints are outside this local acceptance scope.",
    );
  const endpoint = await run(
    "docker",
    ["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"],
    { cwd: root, env: environment, quiet: true, timeoutMs: 30000 },
  );
  if (!localDockerEndpoint(endpoint.text.trim()))
    throw new Error(
      "Remote Docker contexts are outside this local acceptance scope.",
    );
  scopeVerified = true;
  report.isolation.localDockerEndpoint = true;
  await docker(
    [...compose, "build", "api", "intelligence", "frontend", "browser-tests"],
    "build",
    1200000,
  );
  await docker(
    [
      ...compose,
      "up",
      "--detach",
      "--wait",
      "--wait-timeout",
      "180",
      "frontend",
    ],
    "startup",
    210000,
  );
  await ownServices();
  const network = await docker(
    ["network", "inspect", project + "_default", "--format", "{{.Internal}}"],
    "network-isolation",
    30000,
  );
  if (network.text.trim() !== "true")
    throw new Error(
      "The acceptance stack must have no external network route.",
    );
  report.isolation.internalDockerNetwork = true;
  await browser("prepare", 3);
  // Only containers already proved to belong to this random project are stopped.
  await docker(
    [...compose, "stop", "--timeout", "5", "intelligence"],
    "engine-crash",
    30000,
  );
  await browser("engine-down", 1);
  await docker(
    [...compose, "stop", "--timeout", "5", "api"],
    "api-crash",
    30000,
  );
  await browser("backend-down", 1);
  await docker(
    [
      ...compose,
      "up",
      "--detach",
      "--no-build",
      "--pull",
      "never",
      "--no-recreate",
      "--wait",
      "--wait-timeout",
      "120",
      "api",
      "intelligence",
    ],
    "recovery",
    150000,
  );
  await browser("recovered", 1);
  const evidence = async (name) =>
    JSON.parse(
      await readFile(path.join(output, "browser", name + ".json"), "utf8"),
    );
  report.observations = {
    measurements: await evidence("measurements"),
    replay: await evidence("replay-accessibility"),
    signalr: await evidence("signalr-reconnection"),
    engine: await evidence("engine-fallback"),
    backend: await evidence("backend-unavailable"),
    restart: await evidence("restart-recovery"),
    security: await evidence("security"),
  };
  report.automatedPassed = true;
} catch (error) {
  report.failure = error.message;
  console.error("[readiness] " + error.message);
} finally {
  if (scopeVerified) {
    try {
      const diagnostic = await docker(
        [...compose, "logs", "--no-color", "--tail", "500"],
        "services",
        30000,
        false,
      );
      if (diagnostic.code !== 0) report.automatedPassed = false;
      if (report.observations?.restart.queryTokenProbeSent) {
        report.queryTokensAbsentFromAccessLogs = !diagnostic.text.includes('readiness-log-probe');
        if (!report.queryTokensAbsentFromAccessLogs) { report.automatedPassed = false; report.failure = 'Frontend diagnostics exposed the query-token probe.'; }
      }
    } catch {
      report.automatedPassed = false;
    }
    try {
      const cleanup = await docker(
        [
          ...compose,
          "down",
          "--volumes",
          "--remove-orphans",
          "--timeout",
          "10",
        ],
        "cleanup",
        60000,
        false,
      );
      if (cleanup.code !== 0) report.automatedPassed = false;
    } catch {
      report.automatedPassed = false;
    }
  }
  await unlink(secretFile);
  await rmdir(temporary); // Exact owned file and empty directory; never recursively delete user paths.
  if (interrupted) report.automatedPassed = false;
  await writeFile(
    path.join(output, "summary.json"),
    JSON.stringify(report, null, 2) + "\n",
  );
  await writeFile(path.join(output, "report.md"), readinessMarkdown(report));
}
console.log(
  `[readiness] ${report.automatedPassed ? "AUTOMATED PASS; presentation sign-off pending" : "FAIL"}: ${output}`,
);
process.exitCode = report.automatedPassed ? 0 : 1;
