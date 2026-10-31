# Workspace security and access

The API uses ASP.NET Core Identity with a separate SQLite store and protected session cookies. PostgreSQL contains simulation data. The local Compose stack keeps Identity data and Data Protection keys in its API data volume.

## Accounts and roles

`node scripts/init-demo.mjs` initializes missing demo credentials in the ignored root `.env`; it preserves existing values. Demo provisioning is opt-in and has no built-in password. Read the configured password locally and sign in at `/login`.

Viewer can read scenarios, results and replay. Operator and Admin can also edit scenarios, run calculations and approve or reject recommendations. Account-management, governance and general audit screens and their dedicated APIs have been removed. Existing roles still govern operational access.

The API checks authorization independently of the interface. Missing or expired sessions receive 401; authenticated callers without the required role receive 403. Writes require CSRF validation. Passwords and server credentials are excluded from browser runtime configuration.

## Sessions and organization isolation

Identity owns each user's organization membership. The protected session and current user record select the server-configured database, map store and simulation service. Request headers, query parameters and model output do not select a different organization. Expiry, logout, security-stamp changes and membership changes invalidate access; live subscriptions also enforce current membership.

Organization configuration belongs to deployment setup. Keep separate PostgreSQL databases, map files and simulation execution stores for independently configured organizations. Back up Identity, Data Protection keys and operational data together.

## Operational history and AI

Operational changes and recommendation decisions retain internal journal entries with actor, correlation and revision information. Database triggers protect the append-only journal; configured retention expires old evidence. This is application history, not protection against a trusted database owner changing the database.

Copilot execution uses persisted tool policies, bounded model requests and server-rendered citations. Model output cannot approve recommendations or grant itself access. See [tool enforcement](../backend/src/AeroSynapse.Infrastructure/Governance/) and [transcript retention](explainable-copilot.md).

Use HTTPS and appropriate secret storage for deployments beyond the local demo. The browser calls the same-origin .NET API; keep FastAPI and database services on the internal network. See [Docker configuration](docker.md) and the [data model](product/data-and-contracts.md).
