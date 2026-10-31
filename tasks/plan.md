# Unify simulation in Control Tower

## Objective and scope
Control Tower owns the four existing simulation pages: configure, saved scenarios/run, staffing comparison, repeated tests. The simulation page keeps its launcher, summary and Digital Twin and shows Agent AI, incidents, captured resources and terminal trends beside that same simulation. Remove duplicate module navigation and the old separate dashboard view.

## Decisions
- A routed Control Tower shell reuses ScenarioStudio, Scenario, CompareWorkbench and Batches; preserve Studio canDeactivate and write guards.
- Scenario uses ControlTowerStore as the single observation source for the selected run. Keep the module 2 SimulationPanel command/idempotency/recovery implementation, emitting validated run receipts immediately to select the observed run.
- Keep Viewer access to observing Control Tower; editing sections/actions require Operator/Admin as before.
- Old page URLs redirect to corresponding Control Tower child pages, preserving query parameters/fragments. Keep alert zone/minute links and remembered scenario/run context.
- Keep captured inputs separate from editable current scenario; agent changes refresh current configuration and launcher revisions.
- No backend, dependencies, or API contract changes.

## Work and acceptance
1. Route existing pages within the Control Tower shell and consolidate sidebar/journey links. Existing deep links and unsaved-form guard remain usable.
2. Move diagram and retained panels onto the Scenario workspace. Diagram, incidents/resources/agent/trends refer to the same selected live or historical run; no duplicated launch button.
3. Review navigation, role boundaries, recovery and source diff. Compile/build Docker and inspect UI manually. Automated tests will not be run, as explicitly requested by the user.

## Verification and commands
Baseline: npm.cmd run build (AeroSynapse.Client).
Final: docker compose --progress plain up --build --detach --wait --wait-timeout 180.
Review source syntax/types through production compilation and inspect app at http://127.0.0.1:8080/.
No npm test, Playwright test or backend test commands.

## Risks
Context drift: derive every observation panel from one ControlTowerStore. Lost command receipt: preserve persisted SimulationPanel command recovery. Dirty wizard: keep child canDeactivate. Legacy links: explicit redirects preserve parameters. Viewer: keep launcher and mutable sections inaccessible.

## Completed verification
- Baseline and changed frontend production builds succeeded.
- Docker services rebuilt and healthy; frontend returns HTTP 200.
- Manually opened all four Control Tower sections with the local Operator session; validated saved run map and retained panels beside it.
- Legacy /scenario URL preserved explicit scenarioId, runId and fragment on redirect.
- Independent source review identified and resolved run selection restoration and duplicate launch issues.
- Automated tests were not executed at the user's request.

## Follow-up: move Scenarios and Runs into Control Tower
The Operations hero, recent runs, scenario gallery, prepare/results phases and configuration panels move into Scenario. Keep TowerStore as the only observation source and SimulationPanel as the only command owner. Keep module 2 Studio creation in the sidebar. Show the existing map/Agent/incidents/resources/trends in the results phase and retain the forecast and run settings. Save callbacks update the latest scenario without rewriting historical captured inputs. Retire the separate /operations destination; preserve old URLs by redirecting with their query/fragment. Build and inspect manually; do not run automated tests.

Operations workbench follow-up completed: production build succeeds; manually inspected the scenario hero, six preparation tabs, gallery and historical run selection in the deployed Control Tower. Existing maps, Agent, incidents, resources and trends remain in the results pane. Source review confirmed immutable historical inputs, stable recent-run ordering, failed-run diagnostics and save/launch locks. Automated tests were not run.
