import test from "node:test";
import assert from "node:assert/strict";
import { acceptanceErrors } from "./acceptance.mjs";

function fixture() {
  return {
    report: {
      schemaVersion: 1,
      automatedPassed: true,
      machineRole: "presentation-device-declared-by-operator",
      steps: [
        "browser-prepare",
        "browser-engine-down",
        "browser-backend-down",
        "browser-recovered",
        "cleanup",
      ].map((label) => ({ label, exitCode: 0 })),
    },
    signoff: {
      schemaVersion: 1,
      reportSha256: "a".repeat(64),
      presentationDeviceConfirmed: true,
      externalConnectionDisabled: true,
      budgetsAccepted: true,
      screenReaderChecked: true,
      projectorChecked: true,
      syntheticLimitationsExplained: true,
      teamAcknowledged: true,
      owners: {
        presenter: "Demo presenter",
        operator: "Demo operator",
        recovery: "Demo recovery owner",
      },
      browser: "Local browser",
      gpu: "Recorded GPU",
      rehearsalAtUtc: "2026-10-25T10:00:00Z",
      timedDemoSeconds: 240,
      pitchBudgetSeconds: 300,
      render: { mode: "3d", fps: 45, sampleSeconds: 5 },
    },
  };
}
test("development measurements cannot approve a presentation even with a filled checklist", () => {
  const { report, signoff } = fixture();
  report.machineRole = "development";
  assert(
    acceptanceErrors(report, signoff, signoff.reportSha256).some((error) =>
      error.includes("presentation laptop"),
    ),
  );
});
test("failed recovery and incomplete human ownership cannot be hidden by an overall green flag", () => {
  const { report, signoff } = fixture();
  report.steps.pop();
  signoff.owners.recovery = "";
  signoff.teamAcknowledged = false;
  assert(acceptanceErrors(report, signoff, signoff.reportSha256).length >= 3);
});
test("sign-off is tied to the exact report and a timed disconnected rehearsal", () => {
  const { report, signoff } = fixture();
  signoff.timedDemoSeconds = 301;
  signoff.externalConnectionDisabled = false;
  assert(acceptanceErrors(report, signoff, "b".repeat(64)).length >= 3);
});
test("slow native 3D needs a deliberate 2D decision rather than an invented FPS pass", () => {
  const { report, signoff } = fixture();
  signoff.render.fps = 20;
  assert(acceptanceErrors(report, signoff, signoff.reportSha256).length > 0);
  signoff.render = {
    mode: "2d",
    reason: "Measured GPU is below the rendering budget.",
  };
  assert.deepEqual(acceptanceErrors(report, signoff, signoff.reportSha256), []);
});
test("complete same-device evidence and acknowledged ownership can produce acceptance", () => {
  const { report, signoff } = fixture();
  assert.deepEqual(acceptanceErrors(report, signoff, signoff.reportSha256), []);
});
