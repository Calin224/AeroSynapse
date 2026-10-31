# Portable local release

Build from a committed checkout with Node.js 24 and Docker using Linux containers:

```sh
node scripts/test-critical.mjs
node scripts/prepare-release.mjs --out artifacts/release
```

The release contains the saved service images, manifest, checksums, Compose configuration, runtime CLI and synthetic scenario fixture. Copy the complete directory to a machine with the same architecture recorded in its manifest.

From inside the copied release directory:

```sh
node release.mjs verify
node release.mjs up --state ../demo-private-state --port 8080
node release.mjs status --state ../demo-private-state
```

Open `http://127.0.0.1:8080/operator-workflow` (Flux de decizie) and press **Încărcare scenariu demonstrativ**. Read the generated account credentials locally from the private state's `.env`. Keep the state directory outside the public release. Images are loaded from the archive and startup requires no source build or registry pull.

## Logs and recovery

```sh
node release.mjs logs --state ../demo-private-state --out ../demo-logs
node release.mjs down --state ../demo-private-state
node release.mjs up --state ../demo-private-state
```

`down` preserves named volumes. Use the same state directory and matching release to resume an instance. Restore an unavailable service before signing in and reopening a persisted run in Time Travel. Resetting synthetic data is a separate, explicit maintenance command: `node release.mjs reset-seed --state ../demo-private-state`.

## Private checkpoints

```sh
node release.mjs backup --state ../demo-private-state --out ../demo-checkpoint
node release.mjs restore --state ../restored-private-state --from ../demo-checkpoint --port 8081
```

Backups include credentials, the database, Identity state and keys. Keep them private and with the matching public release. Restore uses a fresh destination; it does not overwrite a running instance or downgrade a database. Verify the restored instance before using it for rollback.
