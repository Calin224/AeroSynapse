import path from "node:path";
import { fileURLToPath } from "node:url";
import { readFile } from "node:fs/promises";
import { run, secretKeys } from "./local-release/lib.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
if (process.argv.length > 2)
  throw new Error("Usage: node scripts/start-bottleneck.mjs");
await run(process.execPath, [path.join(root, "scripts/init-demo.mjs")], {
  cwd: root,
});
const config = await readFile(path.join(root, ".env"), "utf8");
const secrets = secretKeys
  .flatMap((key) => [
    process.env[key],
    config
      .match(new RegExp(`^${key}=(.+)$`, "m"))?.[1]
      ?.trim()
      .replace(/^(['"])(.*)\1$/, "$2"),
  ])
  .filter(Boolean);
await run(
  "docker",
  ["compose", "up", "--build", "--detach", "--wait", "--wait-timeout", "180"],
  { cwd: root, timeoutMs: 1_200_000, secrets },
);
const published = await run("docker", ["compose", "port", "frontend", "80"], {
  cwd: root,
  quiet: true,
  secrets,
});
const port =
  published.text.trim().match(/:(\d+)$/)?.[1] ??
  process.env.FRONTEND_PORT ??
  config.match(/^FRONTEND_PORT=(\d+)$/m)?.[1] ??
  "8080";
console.log(
  `Open http://127.0.0.1:${port}/operator-workflow and load the demonstration scenario.`,
);
console.log(
  "Use operator.demo; read its password locally from .env. No password was printed.",
);
