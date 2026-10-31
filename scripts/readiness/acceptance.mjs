export function acceptanceErrors(report, signoff, reportHash) {
  const errors = [];
  if (
    report.schemaVersion !== 1 ||
    !report.automatedPassed ||
    !report.steps?.every((step) => step.exitCode === 0) ||
    ![
      "browser-prepare",
      "browser-engine-down",
      "browser-backend-down",
      "browser-recovered",
      "cleanup",
    ].every((name) =>
      report.steps?.some((step) => step.label === name && step.exitCode === 0),
    )
  )
    errors.push(
      "Complete automated evidence must pass, including recovery and cleanup.",
    );
  if (report.machineRole !== "presentation-device-declared-by-operator")
    errors.push(
      "The evidence must be rerun on the declared presentation laptop.",
    );
  if (signoff.schemaVersion !== 1 || signoff.reportSha256 !== reportHash)
    errors.push(
      "Sign-off must reference the exact SHA-256 of the acceptance report.",
    );
  for (const field of [
    "presentationDeviceConfirmed",
    "externalConnectionDisabled",
    "budgetsAccepted",
    "screenReaderChecked",
    "projectorChecked",
    "syntheticLimitationsExplained",
    "teamAcknowledged",
  ])
    if (signoff[field] !== true) errors.push(`Human check pending: ${field}.`);
  for (const role of ["presenter", "operator", "recovery"])
    if (
      typeof signoff.owners?.[role] !== "string" ||
      !signoff.owners[role].trim() ||
      /^(tbd|todo|pending|de atribuit|n\/a|unknown)$/i.test(
        signoff.owners[role].trim(),
      )
    )
      errors.push(`Assign a named ${role}.`);
  if (!signoff.browser?.trim() || !signoff.gpu?.trim())
    errors.push("Record the actual presentation browser and GPU.");
  if (
    !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z$/.test(
      signoff.rehearsalAtUtc ?? "",
    ) ||
    !Number.isFinite(Date.parse(signoff.rehearsalAtUtc))
  )
    errors.push("Record the rehearsal UTC timestamp.");
  if (
    !Number.isFinite(signoff.timedDemoSeconds) ||
    signoff.timedDemoSeconds <= 0 ||
    !Number.isFinite(signoff.pitchBudgetSeconds) ||
    signoff.timedDemoSeconds > signoff.pitchBudgetSeconds
  )
    errors.push(
      "The disconnected rehearsal must fit the accepted pitch budget.",
    );
  if (signoff.render?.mode === "3d") {
    if (
      !Number.isFinite(signoff.render.fps) ||
      signoff.render.fps < 30 ||
      !Number.isFinite(signoff.render.sampleSeconds) ||
      signoff.render.sampleSeconds < 2.5
    )
      errors.push(
        "Native 3D must provide a measured >=30 FPS sample over >=2.5 seconds.",
      );
  } else if (
    signoff.render?.mode !== "2d" ||
    typeof signoff.render.reason !== "string" ||
    signoff.render.reason.trim().length < 5
  )
    errors.push(
      "Record native 3D evidence or an explicit 2D presentation decision.",
    );
  return errors;
}
