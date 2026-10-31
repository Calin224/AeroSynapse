import { readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import path from "node:path";
import { acceptanceErrors } from "./readiness/acceptance.mjs";

const args = process.argv.slice(2);
if (args.length !== 4 || args[0] !== "--report" || args[2] !== "--signoff")
  throw new Error(
    "Usage: node scripts/accept-demo-readiness.mjs --report <summary.json> --signoff <filled-checklist.json>",
  );
const reportPath = path.resolve(args[1]);
const bytes = await readFile(reportPath);
const report = JSON.parse(bytes),
  signoff = JSON.parse(await readFile(path.resolve(args[3]), "utf8"));
const sha256 = createHash("sha256").update(bytes).digest("hex");
const errors = acceptanceErrors(report, signoff, sha256);
if (errors.length) {
  console.error(errors.join("\n"));
  process.exitCode = 1;
} else {
  const output = path.join(
    path.dirname(reportPath),
    "presentation-acceptance.json",
  );
  await writeFile(
    output,
    JSON.stringify(
      {
        schemaVersion: 1,
        presentationApproved: true,
        acceptedAtUtc: new Date().toISOString(),
        reportSha256: sha256,
        signoff,
        scope:
          "Team-attested prototype rehearsal; not airport operational or security certification.",
      },
      null,
      2,
    ),
    { flag: "wx" },
  );
  console.log("Presentation acceptance recorded: " + output);
}
