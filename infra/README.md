# Infrastructure

Bicep for Athlos (design §11), all in `italynorth`.

| File | Deploys | Deployed by |
|---|---|---|
| `shared.bicep` | `rg-athlos-shared`: registry `acrathlosshared` (Standard), Log Analytics `log-athlos-shared` (0.15 GB/day cap), VNet `vnet-athlos-shared` and the one Container Apps environment `cae-athlos-shared` (the subscription allows one per region). PostgreSQL comes with the first database | `deploy-dev.yml`, before images are pushed |
| `env.bicep` + `{dev,staging,prod}.bicepparam` | `rg-athlos-{env}`: App Insights, Key Vault with the stage's gateway signing key, one identity per service with `AcrPull` and `Key Vault Secrets User`, and the stage's container apps (placed in `cae-athlos-shared`) | `_deploy.yml`, called by the dev, staging and prod workflows |
| `modules/budget.bicep` | Subscription budget | `bootstrap.sh`, once |

The parameter files read `IMAGE_TAG`, `GATEWAY_SIGNING_KEY` (and optionally `APP_VERSION`) from the environment. `_deploy.yml` reads the stored key from `kv-athlos-{env}` and only generates one on the first deploy:

```bash
export GATEWAY_SIGNING_KEY=$(az keyvault secret show --vault-name kv-athlos-dev --name gateway-signing-key --query value -o tsv)
IMAGE_TAG=sha-3f2a1c9 az deployment group create -g rg-athlos-dev --parameters infra/dev.bicepparam
```

**A Container Apps environment cannot be moved into a VNet.** Changing `vnetConfiguration` means deleting the environment (and its apps) and deploying again.

**GitHub plan:** branch protection on a private repo needs GitHub Pro (active since 2026-09-27). Required reviewers on the `prod` environment need GitHub Enterprise for private repos, so `bootstrap.sh` warns and skips them; publishing the GitHub Release is the manual gate instead. Secret scanning is also unavailable for private repos on Pro; the GitGuardian check covers PRs.

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
| GitHub OIDC identities | App registrations `athlos-github-{dev,staging,prod}`, each with a federated credential for its GitHub Environment (subject uses the immutable owner and repo ids) (dev also for `pull_request`, used by Bicep what-if). No client secrets |
| Azure roles | Each identity gets `Contributor` and `Role Based Access Control Administrator` on its own resource group and on `rg-athlos-shared` |
| Budget | `budget-athlos` on the subscription: 5 per month (billing currency), alert at 100% actual and 80% forecast |
| GitHub variables | `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_CLIENT_ID` (repo level = dev identity; overridden per environment) |
| GitHub Environments | `dev`; `staging` (deploys only from `main`); `prod` (deploys only from `v*` tags, requires your approval) |
| Branch protection | `dev` and `main`: PR required, no force pushes, applies to admins too. Required checks: `backend`, `frontend`, `infra` on both, plus `source-branch` on `main` |
| Secret scanning | Secret scanning and push protection, where the plan allows it |

### Notes for the thesis

- **Why one identity per environment:** the federated credential ties a token to a GitHub Environment, and each identity has rights only on its own resource group plus the shared one. A workflow running in `dev` cannot touch `rg-athlos-prod`.
- **Known trade-off:** every identity has `Contributor` on `rg-athlos-shared`, because each environment pushes or imports images in the shared registry and grants its own identities `AcrPull` there. `Role Based Access Control Administrator` is unconstrained. A production hardening step is a role-assignment condition that only allows the specific roles Bicep grants (`AcrPull`, `Key Vault Secrets User`, and so on).
- **Budget start date** defaults to the first day of the current month and cannot change once the budget exists. When re-running the script in a later month, skip the budget step or pass the original `startDate`.
