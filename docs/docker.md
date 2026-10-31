# Local Docker stack

Use Node.js 24, Docker with Linux containers and Compose v2. Run commands from the repository root. The first build needs access to package registries.

```sh
node scripts/init-demo.mjs
docker compose up --build --detach --wait --wait-timeout 180
docker compose ps
```

The initializer generates missing local credentials in the ignored `.env` and preserves existing values. Sign in at `http://127.0.0.1:8080/login` using a configured demo account. The decision flow (Flux de decizie) starts at `/operator-workflow`.

| Service | Default local address | Readiness |
| --- | --- | --- |
| Frontend / nginx | `http://127.0.0.1:8080` | `/health` |
| .NET API | `http://127.0.0.1:5080` | `/health/ready` |
| FastAPI | `http://127.0.0.1:8000` | `/health/ready` |
| PostgreSQL | Internal Docker network | Authenticated query |

The browser uses same-origin `/api` and `/intelligence` routes. nginx forwards both through the authenticated .NET API; the API relays only supported synthetic computation routes to Python. Server credentials stay outside public `runtime-config.js`.

## Configuration and data

`.env.example` lists supported ports, credentials, logging and feature settings. Published development ports bind to loopback by default. PostgreSQL uses the internal `backend` network; HTTP services also use the `edge` network.

The `postgres-data` volume retains operational and engine data. The `airport-maps-data` volume retains map data, Identity and keys. Database credentials do not change automatically when environment values are edited after a volume has been initialized.

API migrations and synthetic seeding are controlled by the Compose environment. See [migration sources](../backend/src/AeroSynapse.Infrastructure/Storage/) and [security](security.md).

## Diagnostics and restart

```sh
docker compose config --quiet
docker compose ps
docker compose logs --tail 100 postgres api intelligence frontend
docker compose restart
docker compose up --detach --no-build --wait --wait-timeout 180
```

Check `/health/ready` for API and engine readiness, including database access. A healthy process and a ready database are different checks. Logs include correlation identifiers; avoid copying credentials or request bodies into reports.

## Stop and reset

`docker compose down` stops the stack and preserves named volumes. For an explicit reset of synthetic data:

```sh
docker compose --profile tools run --rm reset-data
```

Reset and volume deletion are maintenance operations, not crash recovery. Use the [portable release guide](../scripts/local-release/README.md) for offline startup, private checkpoints and restore into a fresh instance.

## Tests

`node scripts/test-critical.mjs` creates an isolated stack for cross-service tests. `node scripts/validate-demo.mjs` runs presentation checks. Reports belong in `artifacts/`; see the [documentation index](README.md).
