import { randomBytes } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const file = path.join(root, ".env");
let contents;
try {
  contents = await readFile(file, "utf8");
} catch (error) {
  if (error.code !== "ENOENT") throw error;
  contents = await readFile(path.join(root, ".env.example"), "utf8");
}
contents = contents.replaceAll("\r\n", "\n");
for (const key of [
  "POSTGRES_PASSWORD",
  "IDENTITY_DEMO_VIEWER_PASSWORD",
  "IDENTITY_DEMO_OPERATOR_PASSWORD",
  "IDENTITY_DEMO_ADMIN_PASSWORD",
]) {
  const match = contents.match(new RegExp(`^${key}=(.*)$`, "m"));
  // Existing database credentials must never be rotated implicitly.
  if (match && match[1].trim() && !['""', "''"].includes(match[1].trim()))
    continue;
  const value = randomBytes(32).toString("hex") + "aA1!";
  contents = match
    ? contents.replace(new RegExp(`^${key}=.*$`, "m"), `${key}=${value}`)
    : contents + `\n${key}=${value}\n`;
}
contents = /^IDENTITY_DEMO_ENABLED=/m.test(contents)
  ? contents.replace(
      /^IDENTITY_DEMO_ENABLED=.*$/m,
      "IDENTITY_DEMO_ENABLED=true",
    )
  : contents + "\nIDENTITY_DEMO_ENABLED=true\n";
await writeFile(file, contents, { mode: 0o600 });
console.log(
  "Local .env configured; existing credentials retained. Passwords were not printed.",
);
console.log(
  "Fictitious accounts: viewer.demo, operator.demo, admin.demo. Read passwords locally from .env.",
);
