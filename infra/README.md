# Infrastructure

Bicep for Athlos (design §11). `shared.bicep` deploys `rg-athlos-shared` (registry, PostgreSQL server, Log Analytics); `env.bicep` with a `.bicepparam` file deploys one environment. Both are added in phase 1.

## One-time setup

Everything Bicep and the workflows cannot create for themselves is done once by [`bootstrap.sh`](bootstrap.sh). It is safe to re-run.

### 1. Check the subscription (portal, once)

- **Free services:** Azure for Students > *Free services* must list *Azure Database for PostgreSQL Flexible Server* (750 h B1ms) and *Container Registry* (Standard). If they are missing, stop: the budget in design §10 no longer holds.
- **Allowed regions:** Student subscriptions often restrict regions by policy. Check *Policy > Assignments* and pick one allowed EU region and use it for everything (`LOCATION`). The University of Thessaly subscription allows only `italynorth`, `polandcentral`, `denmarkeast`, `belgiumcentral` and `switzerlandnorth`; Athlos uses `italynorth` (closest to Greece; B1ms and PostgreSQL 17 available).
- **App registrations:** the script creates three Entra app registrations. If the subscription belongs to a university directory that blocks app registrations for students, create the directory's apps by hand or ask IT. The rest of the script still applies.

### 2. Create the GitHub repository

The repository must exist with both branches pushed before the script can protect them:

```bash
gh repo create <owner>/athlos --private --source . --push   # or --public (design §16.4)
git push -u origin dev
```

### 3. Run the bootstrap

```bash
az login
az account set --subscription "<Azure for Students subscription>"
gh auth login

GITHUB_REPO=<owner>/athlos BUDGET_EMAIL=<address> ./infra/bootstrap.sh
```

What it creates:

| What | Details |
|---|---|
| Resource providers | Registered once per subscription |
| Resource groups | `rg-athlos-shared`, `rg-athlos-dev`, `rg-athlos-staging`, `rg-athlos-prod` |
| GitHub OIDC identities | App registrations `athlos-github-{dev,staging,prod}`, each with a federated credential for its GitHub Environment (dev also for `pull_request`, used by Bicep what-if). No client secrets |
| Azure roles | Each identity gets `Contributor` and `Role Based Access Control Administrator` on its own resource group and on `rg-athlos-shared` |
| Budget | `budget-athlos` on the subscription: 5 per month (billing currency), alert at 100% actual and 80% forecast |
| GitHub variables | `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_CLIENT_ID` (repo level = dev identity; overridden per environment) |
| GitHub Environments | `dev`; `staging` (deploys only from `main`); `prod` (deploys only from `v*` tags, requires your approval) |
| Branch protection | `dev` and `main`: PR required, no force pushes, applies to admins too. Required checks: `backend` on both, plus `source-branch` on `main` |
| Secret scanning | Secret scanning and push protection, where the plan allows it |

### Notes for the thesis

- **Why one identity per environment:** the federated credential ties a token to a GitHub Environment, and each identity has rights only on its own resource group plus the shared one. A workflow running in `dev` cannot touch `rg-athlos-prod`.
- **Known trade-off:** every identity has `Contributor` on `rg-athlos-shared`, because each environment pushes or imports images in the shared registry and grants its own identities `AcrPull` there. `Role Based Access Control Administrator` is unconstrained. A production hardening step is a role-assignment condition that only allows the specific roles Bicep grants (`AcrPull`, `Key Vault Secrets User`, and so on).
- **Budget start date** defaults to the first day of the current month and cannot change once the budget exists. When re-running the script in a later month, skip the budget step or pass the original `startDate`.
