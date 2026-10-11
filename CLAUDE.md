# Athlos

Master thesis project: a multi-tenant B2B2C fitness marketplace. Coaches are tenants who sell programs; customers buy from any coach. The full design is in `docs/design.md` (diagrams in `docs/diagrams/`). Read the relevant section before building a feature, and follow it. If something in the design seems wrong, say so instead of silently diverging.

## Stack

- .NET 10, ASP.NET Core Minimal APIs, EF Core 10 + Npgsql, C# latest
- React 19 + TypeScript (strict) + Vite, TanStack Query, React Router, MSAL React, Tailwind + shadcn/ui
- Aspire for local orchestration (`aspire run`)
- PostgreSQL 17, Azure Service Bus (Basic, queues only), Azure Blob Storage, Azure Communication Services Email
- Azure Container Apps (Consumption), Bicep, GitHub Actions with OIDC

## Commands

- Run everything locally: `aspire run`
- Backend tests: `dotnet test`
- Frontend: `cd src/web && npm run lint && npm run typecheck && npm test && npm run build`
- Bicep: `az bicep lint --file infra/env.bicep`
- Container images: `dotnet publish <project> -c Release --os linux --arch x64 -t:PublishContainer` (use `-t:`, not `/t:`, in Git Bash)
- Stripe webhooks locally: `stripe listen --forward-to https://localhost:<gateway-port>/webhooks/stripe`

## Rules that must not be broken

- One PostgreSQL server (B1ms) serves all environments. Keep `Maximum Pool Size=3` per service and never add a second server.

- **Tenant isolation is the point of the thesis.**
  - Every tenant-owned entity implements `ITenantOwned` and gets the EF global query filter.
  - Every tenant-owned table has an RLS policy with `ENABLE` and `FORCE ROW LEVEL SECURITY`.
  - Tenant id comes only from `ITenantContext`, never from a request body or query string.
  - Every service calls `AddGatewaySignature` + `UseGatewayOnly` (`Athlos.Tenancy`): it accepts only gateway-signed requests. Service-to-service calls sign with the same `GatewaySignature`. Never exempt a route beyond `/health/*` and `/version`.
  - `IgnoreQueryFilters()` only in slices explicitly listed as cross-tenant in the design (§3.2).
  - Another tenant's resource returns `404`, never `403`.
  - Every new endpoint gets a case in `tests/Athlos.IsolationTests`.
- Services connect to PostgreSQL as their own non-owner role. Never grant `BYPASSRLS` or superuser to an app role.
- Tenant-scoped route groups use `.RequireTenant()` and `.WithTenantTransaction<TDbContext>()`; RLS settings only exist inside a transaction.
- Schema changes go through EF migrations; RLS policies are written in the same migration with the `RowLevelSecurity` helpers. Never widen reads in a combined policy: `AddTenantIsolationPolicy(table, alsoReadable)` splits SELECT from writes (design §3.2).
- `set_config('app.tenant_id', ..., true)` is transaction-local; do not change it to session-level.
- Database per service. A service never reads another service's database; use an HTTP call or an event.
- Money is integer minor units plus a currency code.
- Never store or handle passwords; Entra External ID owns sign-up, verification and password reset. App emails go only through the `notifications` queue and the notifications-worker; never log email addresses.
- No secrets in the repo. Local: user secrets or Aspire parameters. Azure: Key Vault + managed identity.
- One Container Apps environment (`cae-athlos-shared`) hosts dev, staging and prod: the subscription quota is one per region. Stage separation comes from per-stage identities, Key Vaults and signing keys; never share those across stages.
- Every Azure resource goes in `italynorth`; the subscription's policy blocks other regions (design §10). Static Web Apps is not available there, so the gateway serves the SPA and per-environment SPA settings come from `GET /config.json`, never from build-time variables (§8.1).
- Budget is €100 for 12 months. Do not add Azure resources that are not in the design (no Redis, API Management, Front Door, a second PostgreSQL server, Service Bus Standard) without asking.

## Code style

- Vertical slices: one file or folder per feature under `Features/`. No MediatR, no generic repositories over EF Core, no AutoMapper.
- Nullable enabled, warnings as errors, central package management (`Directory.Packages.props`).
- EF Core snake_case naming convention; UUIDv7 ids (`Guid.CreateVersion7()`).
- Errors are `ProblemDetails`; routes follow the API conventions in §7.1 of the design.
- Tests: xUnit v3, Testcontainers for PostgreSQL. Prefer integration tests against a real database over mocks.
- Frontend API client is generated from the OpenAPI documents with `openapi-typescript`; do not hand-write request types.

## Git

- Branch from `dev` as `feature/<short-name>`; PRs go into `dev`. `main` only receives PRs from `dev` (or `hotfix/*`).
- Environments are dev (every push to `dev`), staging (every merge into `main`) and prod (a `v*` release tag, with approval). There is no staging branch.
- Build once, promote: images are built on `dev` and tagged `sha-<short>`; staging and prod reuse those images, never rebuild them.
- Conventional Commits (`feat:`, `fix:`, `chore:`, `docs:`, `test:`, `refactor:`).
- Releases are GitHub Releases tagged `vMAJOR.MINOR.PATCH` on `main`.

## Current phase

Keep this line updated as you go: **Build plan complete; v1.3.0 released** (design §15; v1.3.0 made the marketplace an e-shop with a cart across coaches, paid once with Stripe separate charges and transfers, §2.2, §8; v1.1.0 added customer account deletion, §5.2, with Graph apps set up per stage by `infra/account_deletion_setup.py`; v1.2.0 added the platform admin, §1, §3.4: `/admin` for the Entra app role `Platform.Admin`, assigned per stage by `infra/platform_admin_setup.py`, to suspend workspaces and assign plans). Still not built: the optional Playwright tests. What remains is the thesis write-up, from the evidence in `docs/evaluation/` (`targets.md` against §15.3, `noisy-neighbor.md`, raw data and the scripts in `tests/evaluation/` that reproduce each number). Open point recorded there: cold starts (median 87 s from zero). `deploy-dev` skips the shared-infra step unless `infra/` changed (since 11 October 2026), and CI checks that the OpenAPI documents and the SPA client match the code. The gateway runs at 0.5 vCPU (noisy-neighbor round 2). Telemetry is tagged per coach (`tenant.id`, `tenant.slug`, `tenant.plan`, `athlos.release`); the "Athlos per coach" workbook is in each environment; the prod gateway 5xx alert emails the prod `ALERT_EMAIL` variable. Released: `v0.1.0` (walking skeleton), `v0.2.0` (phases 2 and 3: Entra sign-in, tenancy, catalog with RLS, cover images), `v0.3.0` (phases 4 and 5: Stripe Connect and Checkout, ordering, enrollment, notifications-worker, staff invites), `v0.4.0` (phase 6: Tailwind + shadcn/ui frontend, marketplace search and paging, branded storefronts, studio editor, sales and settings, lazy-loaded customer pages), `v0.5.0` (frontend feature folders and the ink-and-volt redesign, responsive from phone to desktop), `v0.6.0` (phase 7: demo data, rate limits and quotas), `v0.7.0` (workspace deletion, phase 8: per-coach telemetry, workbook, 5xx alert), `v1.0.0` (phase 9: evaluation, gateway at 0.5 vCPU), `v1.1.0` (customer account deletion), `v1.2.0` (platform admin), `v1.3.0` (shop and cart). Prod has no required reviewers (GitHub Pro, private repo): publishing the Release is the prod gate. Entra External ID tenant `athlos` lives on a separate pay-as-you-go subscription (design §5).
