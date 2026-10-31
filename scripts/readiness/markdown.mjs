export function readinessMarkdown(report) {
  const observed = report.observations;
  const lines = [
    "# Demo readiness evidence",
    "",
    `Recorded: ${report.recordedAtUtc}. Source: ${report.sourceCommit}; tracked source dirty: ${report.sourceDirty}.`,
    `Automated result: **${report.automatedPassed ? "PASS" : "FAIL"}**. Presentation approval: **PENDING**.`,
    "",
    `Machine role: ${report.machineRole}. Host: ${report.host.platform} ${report.host.release}, ${report.host.cpu}, ${report.host.memoryGiB} GiB RAM.`,
    "Container Chromium measurements do not certify the physical presentation GPU. No airport network or external target is used.",
    "",
    "| Step | Exit | Seconds |",
    "| --- | ---: | ---: |",
    ...report.steps.map(
      (step) =>
        `| ${step.label} | ${step.exitCode} | ${(step.milliseconds / 1000).toFixed(2)} |`,
    ),
    "",
  ];
  if (observed) {
    lines.push(
      "## Measured synthetic demo",
      "",
      "| Surge input | Incident saved seconds | Full decision seconds | Baseline runId | Candidate runId |",
      "| ---: | ---: | ---: | --- | --- |",
      ...observed.measurements.measurements.map(
        (run) =>
          `| ${run.intensity}% | ${(run.savedMs / 1000).toFixed(2)} | ${(run.totalMs / 1000).toFixed(2)} | ${run.baselineRunId} | ${run.candidateRunId} |`,
      ),
      "",
    );
    const { load, renderer } = observed.measurements;
    lines.push(
      `Load: ${load.requests} successful reads, concurrency ${load.concurrency}, p50 ${load.p50Ms} ms, p95 ${load.p95Ms} ms, maximum ${load.maxMs} ms. These are local bounded reads, not production capacity.`,
      "",
      `Render: ${renderer.fallback2d ? "2D fallback" : `${renderer.measurement.fps} FPS; frame p95 ${renderer.measurement.frameP95Ms} ms`}. GPU/renderer: ${renderer.gpu ?? "unavailable in this run"}.`,
      "",
      `Persisted Time Travel with WebGL unavailable: ${observed.replay.passed}; mutations: ${observed.replay.mutations.length}.`,
      `SignalR recovery: ${(observed.signalr.recoveryMilliseconds / 1000).toFixed(2)} s; REST observations ${observed.signalr.recoveryReads}; no KPI regression: ${observed.signalr.noKpiRegression}.`,
      `Retired demo route unavailable during engine/API failure: ${observed.backend.retiredRouteUnavailable}. Restart preserved hashes and the same command/run identity: ${observed.restart.persistedHashesUnchanged && observed.restart.sameCommandSameRun}.`,
      "",
    );
  }
  if (report.failure) lines.push(`Failure: ${report.failure}`, "");
  lines.push(
    "## Remaining human acceptance",
    "",
    ...report.manualPending.map((item) => "- " + item),
    "",
    "Model limits: fictional flights, population and resources; virtual time and captured service/arrival assumptions. One common seed is descriptive, not a confidence interval or a validated airport prediction. Missing/failed results are never replaced with invented numbers.",
    "",
    "The selected WCAG rules and keyboard/reflow tests cover only part of accessibility. Incomplete axe findings remain in the detailed reports for manual inspection. Security checks are local cookie/CSRF/role/contract checks, not a penetration test or operational certification.",
    "",
    "See scripts/readiness/README.md for the five-minute pitch, named ownership checklist, device acceptance and crash recovery runbook.",
    "",
  );
  return lines.join("\n");
}
