# Presentation checks and recovery

From the repository root, run `node scripts/validate-demo.mjs`. The runner creates an isolated synthetic stack and writes measurements and test reports under `artifacts/readiness/`. It does not target the running demo instance.

On the actual presentation machine, use `node scripts/validate-demo.mjs --presentation-device`. Automated checks do not substitute for checking the intended browser, projector, screen reader, timing budget and rendering performance on that device.

## Rehearsal

1. Verify and start the portable release using its `README.md`.
2. Sign in, open `/operator-workflow` (Flux de decizie) and load the demonstration scenario. Add an arrival surge, simulate it and run the staffing optimization. Wait for calculated results, inspect before/after values in the comparison and at the same simulated minute in Time Travel, then add a stronger surge on another flight and repeat.
3. Check keyboard navigation, focus, text size and the 2D fallback. Use the measured 3D performance or choose 2D explicitly.
4. Rehearse with external connectivity unavailable while keeping the local stack running. Time the complete presentation and assign the presenter, operator and recovery owner.
5. Copy `scripts/readiness/signoff.template.json` to a private checklist. Fill only checks actually completed and enter the exact SHA-256 of the readiness `summary.json`.

```sh
node scripts/accept-demo-readiness.mjs --report artifacts/readiness/<run>/summary.json --signoff <completed-checklist.json>
```

The acceptance command rejects incomplete checks and mismatched reports. It records the team's rehearsal acceptance, not operational or security certification.

## Recovery

Use 2D if WebGL or the rendering budget fails. For API or engine failures, inspect the instance's health and logs, restart the affected service and verify an existing command before sending another. Reopen persisted runs after the API and session are available. Reconnect SignalR without resending mutations.

After a browser crash, reopen the frontend and sign in. After a host crash, start Docker and the same release/state. Preserve volumes; use a matching private checkpoint and fresh restore destination if recovery requires it. Record run ID, command ID, stage, UTC time and error code without recording credentials.

## Candidate kit

With passing readiness evidence and a clean release from the same commit:

```sh
node scripts/prepare-demo-kit.mjs --bundle artifacts/releases/<release> --report artifacts/readiness/<run>/summary.json --out artifacts/presentation-kit/<new-name>
```

The kit contains the verified public release, synthetic calculated results, QA evidence, this runbook and an unfilled sign-off template. Private state and backups stay separate.
