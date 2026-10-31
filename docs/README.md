# Documentation

Start with the [repository README](../README.md) for the local demo. This folder contains maintained technical references; generated reports and screenshots belong in `artifacts/` or the test runner's output directory.

| Topic | Guides |
| --- | --- |
| Setup and operation | [Docker](docker.md), [security and access](security.md), [portable release](../scripts/local-release/README.md) |
| API and persistence | [Data model and contracts](product/data-and-contracts.md), [schemas and examples](../contracts/), [API implementation](../backend/src/AeroSynapse.API/) |
| Simulation | [Operator workflow](simulation-journey.md), [algorithms and assumptions](product/algorithms-and-assumptions.md), [passenger journeys and delays](passenger-journey-delays.md) |
| Data evidence | [Data provenance, verifiable calculations and real-airport precedents](product/data-evidence-and-real-airports.md) |
| Decisions and AI | [Explanations and transcript retention](explainable-copilot.md) |
| 3D assets | [Source model and materials](../assets/blender/), [model manifest](../AeroSynapse.Client/public/models/aerosynapse/manifest.json), [material credits](../assets/blender/materials/asset-credits.json) |

## Validation

Run the isolated integration suite from the repository root:

```sh
node scripts/test-critical.mjs
```

Use `node scripts/validate-demo.mjs` for the presentation rehearsal checks. Its operational checklist and template live in [scripts/readiness](../scripts/readiness/README.md). The [frontend](../AeroSynapse.Client/README.md) and [Python service](../intelligence/README.md) have component-specific commands.

Contract examples and schemas remain under `contracts/`. The management scenario fixture used by integration tests is [backend/examples/scenario-request.json](../backend/examples/scenario-request.json).

Update the relevant guide when behavior changes. Keep secrets, private backups, screenshots, benchmark captures and historical review reports outside this folder.
