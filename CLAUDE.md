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
- Stripe webhooks locally: `stripe listen --forward-to https://localhost:<gateway-port>/webhooks/stripe`

## Rules that must not be broken

- One PostgreSQL server (B1ms) serves all environments. Keep `Maximum Pool Size=3` per service and never add a second server.

- **Tenant isolation is the point of the thesis.**
  - Every tenant-owned entity implements `ITenantOwned` and gets the EF global query filter.
  - Every tenant-owned table has an RLS policy with `ENABLE` and `FORCE ROW LEVEL SECURITY`.
  - Tenant id comes only from `ITenantContext`, never from a request body or query string.
  - `IgnoreQueryFilters()` only in slices explicitly listed as cross-tenant in the design (§3.2).
  - Another tenant's resource returns `404`, never `403`.
  - Every new endpoint gets a case in `tests/Athlos.IsolationTests`.
- Services connect to PostgreSQL as their own non-owner role. Never grant `BYPASSRLS` or superuser to an app role.
- `set_config('app.tenant_id', ..., true)` is transaction-local; do not change it to session-level.
- Database per service. A service never reads another service's database; use an HTTP call or an event.
- Money is integer minor units plus a currency code.
- Never store or handle passwords; Entra External ID owns sign-up, verification and password reset. App emails go only through the `notifications` queue and the notifications-worker; never log email addresses.
- No secrets in the repo. Local: user secrets or Aspire parameters. Azure: Key Vault + managed identity.
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

Keep this line updated as you go: **Phase 0, setup** (see §15 of the design).
