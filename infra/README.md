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
- **Known trade-off (email):** the notifications-worker sends email with its managed identity through the built-in `Communication and Email Service Owner` role, scoped to its own stage's Communication Services resource. That role can also list the resource's keys and delete it. A custom role with only `Microsoft.Communication/CommunicationServices/Read` and `/Write` and `Microsoft.Communication/EmailServices/Write` is the hardening step; creating role definitions needs `Microsoft.Authorization/roleDefinitions/write`, which the deploy identities deliberately do not have.
- **Budget start date** defaults to the first day of the current month and cannot change once the budget exists. When re-running the script in a later month, skip the budget step or pass the original `startDate`.

## Rotating secrets

Every secret lives in its stage's Key Vault (`kv-athlos-{env}`), never in the repo or a workflow. Rotate one stage at a time, dev first. Writing a secret needs `Key Vault Secrets Officer` on that vault (the setup scripts grant it to you; `az role assignment create --role "Key Vault Secrets Officer" --assignee <you> --scope <vault id>`).

**Reminder.** [`secret-expiry.yml`](../.github/workflows/secret-expiry.yml) runs on the first of every month and fails, which GitHub emails you about, when a secret in `kv-athlos-dev` expires within 60 days. Scheduled runs can only use the dev environment, and the setup scripts store each secret in all three stages at once with the same expiry, so dev stands for all. The deletion secrets stored before v1.18.0 (10 October 2026) carry no expiry yet; give them theirs once, for each stage: `az keyvault secret set-attributes --vault-name kv-athlos-<env> -n entra-deletion-client-secret --expires 2028-10-09T10:00:00Z`. Only secrets stored with an expiry date are checked: the Entra deletion secret has one (2 years from `account_deletion_setup.py`); the others have none.

| Secret | Who uses it | How to rotate | Downtime |
|---|---|---|---|
| `gateway-signing-key` | Every app: the gateway and service-to-service calls sign with it, every service verifies (design §3.2) | The three deploys below | None |
| `stripe-secret-key` | ordering-api (payments, refunds), tenancy-api (Connect) | Roll the key in the Stripe dashboard, then `python infra/stripe_setup.py` for that stage and redeploy | Seconds: the old key stops when you roll it unless Stripe is told to keep it for a while |
| `stripe-webhook-secret`, `stripe-connect-webhook-secret` | ordering-api, tenancy-api (webhook signatures) | `python infra/stripe_setup.py` recreates both endpoints with new secrets; redeploy | Events Stripe sends between the two steps fail and are retried by Stripe |
| `entra-deletion-client-secret` | tenancy-api (account deletion through Graph) | `python infra/account_deletion_setup.py`: a new secret, the old ones removed, the expiry stored; redeploy | Account deletion answers 503 until the redeploy |

### The gateway signing key, without downtime

Services accept a request signed with their key, or with `gateway-signing-key-also` while that secret exists (`GatewaySignature:AlsoAcceptKey`). The deploy workflow wires the second key into every app when it finds it. Apps roll out one at a time, so each step must work with half the apps on the old setting:

1. **Accept the new key.** `az keyvault secret set --vault-name kv-athlos-<env> -n gateway-signing-key-also --value "$(openssl rand -base64 48)"`, then redeploy the stage. Everyone still signs with the old key; everyone now also accepts the new one.
2. **Sign with the new key.** Swap them: set `gateway-signing-key` to the new value and `gateway-signing-key-also` to the old one, then redeploy. Apps that already moved sign with the new key, which the others accept since step 1.
3. **Drop the old key.** `az keyvault secret delete --vault-name kv-athlos-<env> -n gateway-signing-key-also` (purge it too, since soft delete keeps it 7 days), then redeploy.

A compromised key goes the same way, as fast as three deploys allow. Per-service keys, so a compromised service could not sign as another, are future work (`docs/evaluation/limitations.md` #16).
