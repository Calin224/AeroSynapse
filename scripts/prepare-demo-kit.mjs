import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { hash, publicFiles } from "./local-release/lib.mjs";
import { verifyBundle } from "./local-release/runtime.mjs";

const args = process.argv.slice(2);
if (
  args.length !== 6 ||
  args[0] !== "--bundle" ||
  args[2] !== "--report" ||
  args[4] !== "--out"
)
  throw new Error(
    "Usage: node scripts/prepare-demo-kit.mjs --bundle <verified-release> --report <readiness-summary.json> --out <new-kit-directory>",
  );
const bundle = path.resolve(args[1]),
  reportPath = path.resolve(args[3]),
  out = path.resolve(args[5]);
const relation = path.relative(bundle, out);
if (
  !relation ||
  (!relation.startsWith(".." + path.sep) &&
    relation !== ".." &&
    !path.isAbsolute(relation))
)
  throw new Error(
    "Keep the candidate kit outside the immutable public release.",
  );
const report = JSON.parse(await readFile(reportPath, "utf8"));
const manifest = await verifyBundle(bundle);
if (
  !report.automatedPassed ||
  report.sourceDirty ||
  manifest.sourceDirty ||
  report.sourceCommit !== manifest.commit
)
  throw new Error(
    "The kit requires passing readiness evidence and a clean release from the exact same commit.",
  );
const proof = path.join(path.dirname(reportPath), "browser");
await mkdir(path.dirname(out), { recursive: true });
await mkdir(out); // Never overwrite a kit or any private state.
await mkdir(path.join(out, "release"));
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const files = [];
async function copy(source, name) {
  await copyFile(source, path.join(out, name));
  files.push(name);
}
// A public release is a strict inventory. Do not recursively copy accidental private extras.
for (const name of [...publicFiles, "manifest.json", "SHA256SUMS"])
  await copy(path.join(bundle, name), "release/" + name);
await copy(reportPath, "readiness-summary.json");
await copy(
  path.join(path.dirname(reportPath), "report.md"),
  "readiness-report.md",
);
const runbook = (
  await readFile(path.join(root, "scripts/readiness/README.md"), "utf8")
)
  .replaceAll("scripts/readiness/signoff.template.json", "signoff.template.json");
await writeFile(path.join(out, "runbook.md"), runbook);
files.push("runbook.md");
await mkdir(path.join(out, "qa"));
for (const name of [
  "accessibility-preset",
  "accessibility-report",
  "accessibility-2d-replay",
  "presentation-reflow",
  "replay-reflow",
])
  await copy(path.join(proof, name + ".json"), "qa/" + name + ".json");
await copy(
  path.join(root, "scripts/readiness/signoff.template.json"),
  "signoff.template.json",
);
const readme = `# Operation Bottleneck · presentation candidate\n\nRelease ${manifest.releaseId}, commit ${manifest.commit}. Automated evidence passed; physical presentation-device and team sign-off remain required. All demonstration inputs and captures are synthetic.\n\n1. Verify: node release/release.mjs verify\n2. Start offline from this directory: node release/release.mjs up --state ../demo-private-state --port 8080\n3. Open http://127.0.0.1:8080/operator-workflow (Flux de decizie) and load the demonstration scenario. Use the private runtime account, never publish its password.\n4. If a service fails, follow runbook.md to restart it, then sign in and open /replay for a persisted run.\n5. Follow runbook.md; assign presenter/operator/recovery owner and complete signoff.template.json only after the real disconnected rehearsal.\n\nKeep private state/checkpoints outside this public kit. A second copy of this kit is the artifact backup; it does not replace a private database/Identity checkpoint. SHA256SUMS covers the copied files. Calculated results remain in the application and its private database checkpoint.\n`;
await writeFile(path.join(out, "README.md"), readme);
files.push("README.md");
const sums = [];
for (const name of files)
  sums.push(`${await hash(path.join(out, name))}  ${name}`);
await writeFile(path.join(out, "SHA256SUMS"), sums.join("\n") + "\n");
console.log(
  "Synthetic presentation candidate prepared: " +
    out +
    ". Presentation approval remains pending.",
);
