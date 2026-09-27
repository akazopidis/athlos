#!/usr/bin/env bash
# One-time setup of everything that Bicep and the workflows cannot create themselves
# (design §11, §12.3, §15 phase 0). Safe to re-run: every step checks before it creates.
#
#   GITHUB_REPO=<owner>/athlos BUDGET_EMAIL=<you@example.com> ./infra/bootstrap.sh
#
# Needs: az (signed in to the Azure for Students subscription), gh (signed in, repo admin).
set -euo pipefail
# Git Bash on Windows rewrites arguments like /subscriptions/... into C:/Program Files/Git/...
export MSYS_NO_PATHCONV=1

: "${GITHUB_REPO:?Set GITHUB_REPO=<owner>/<repo>}"
: "${BUDGET_EMAIL:?Set BUDGET_EMAIL to the address that receives budget alerts}"
LOCATION="${LOCATION:-italynorth}"
ENVIRONMENTS=(dev staging prod)

SUBSCRIPTION_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)
echo "Azure subscription: $(az account show --query name -o tsv) ($SUBSCRIPTION_ID)"
echo "GitHub repository:  $GITHUB_REPO"
echo "Region:             $LOCATION"
read -r -p "Continue? [y/N] " answer
[[ "$answer" == "y" ]] || exit 1

echo "== Resource providers"
for ns in Microsoft.App Microsoft.ContainerRegistry Microsoft.DBforPostgreSQL Microsoft.ServiceBus \
          Microsoft.Storage Microsoft.KeyVault Microsoft.OperationalInsights Microsoft.Insights \
          Microsoft.ManagedIdentity Microsoft.Communication Microsoft.Network Microsoft.Consumption; do
  az provider register --namespace "$ns" --output none
done

echo "== Resource groups"
for rg in shared "${ENVIRONMENTS[@]}"; do
  az group create --name "rg-athlos-$rg" --location "$LOCATION" --tags app=athlos env="$rg" --output none
done

# GitHub's OIDC subject names the owner and repo with their immutable ids:
# repo:<owner>@<owner id>/<repo>@<repo id>:environment:dev
oidc_repo=$(gh api "repos/$GITHUB_REPO" --jq '"\(.owner.login)@\(.owner.id)/\(.name)@\(.id)"')

echo "== GitHub OIDC: one app registration per environment"
# Each environment's workflow signs in as its own identity, which has rights on its own
# resource group and on the shared one (registry, PostgreSQL server) only. A dev token
# can therefore never deploy to prod.
declare -A CLIENT_IDS
for env in "${ENVIRONMENTS[@]}"; do
  name="athlos-github-$env"
  app_id=$(az ad app list --display-name "$name" --query '[0].appId' -o tsv)
  if [[ -z "$app_id" ]]; then
    app_id=$(az ad app create --display-name "$name" --query appId -o tsv)
  fi
  az ad sp show --id "$app_id" --output none 2>/dev/null || az ad sp create --id "$app_id" --output none
  sp_id=$(az ad sp show --id "$app_id" --query id -o tsv)
  CLIENT_IDS[$env]=$app_id

  subjects=("environment:$env")
  # PR checks (Bicep what-if) sign in as the dev identity.
  [[ "$env" == "dev" ]] && subjects+=("pull_request")
  for subject in "${subjects[@]}"; do
    fic_name="github-ids-${subject//:/-}"
    if [[ -z $(az ad app federated-credential list --id "$app_id" --query "[?name=='$fic_name'].name" -o tsv) ]]; then
      az ad app federated-credential create --id "$app_id" --output none --parameters "{
        \"name\": \"$fic_name\",
        \"issuer\": \"https://token.actions.githubusercontent.com\",
        \"subject\": \"repo:$oidc_repo:$subject\",
        \"audiences\": [\"api://AzureADTokenExchange\"]
      }"
    fi
  done

  # Contributor deploys resources; Role Based Access Control Administrator lets Bicep grant the
  # service identities their roles (AcrPull, Key Vault Secrets User, ...). Both are scoped to
  # the environment's own resource group plus the shared one.
  for rg in "rg-athlos-$env" rg-athlos-shared; do
    scope="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$rg"
    for role in "Contributor" "Role Based Access Control Administrator"; do
      az role assignment create --assignee-object-id "$sp_id" --assignee-principal-type ServicePrincipal \
        --role "$role" --scope "$scope" --output none
    done
  done
done

echo "== Budget alert"
az deployment sub create --name athlos-budget --location "$LOCATION" \
  --template-file "$(dirname "$0")/modules/budget.bicep" \
  --parameters contactEmails="[\"$BUDGET_EMAIL\"]" --output none

echo "== GitHub variables (ids, not secrets)"
gh variable set AZURE_TENANT_ID --repo "$GITHUB_REPO" --body "$TENANT_ID"
gh variable set AZURE_SUBSCRIPTION_ID --repo "$GITHUB_REPO" --body "$SUBSCRIPTION_ID"
# Repo-level client id is the dev identity, used by PR checks outside an environment.
gh variable set AZURE_CLIENT_ID --repo "$GITHUB_REPO" --body "${CLIENT_IDS[dev]}"

echo "== GitHub environments"
reviewer_id=$(gh api user --jq .id)

add_deployment_policy() { # env, name, type
  local existing
  existing=$(gh api "repos/$GITHUB_REPO/environments/$1/deployment-branch-policies" \
    --jq ".branch_policies[] | select(.name == \"$2\") | .id")
  [[ -n "$existing" ]] || gh api --silent -X POST "repos/$GITHUB_REPO/environments/$1/deployment-branch-policies" \
    -f name="$2" -f type="$3"
}

gh api --silent -X PUT "repos/$GITHUB_REPO/environments/dev"

gh api --silent -X PUT "repos/$GITHUB_REPO/environments/staging" --input - <<EOF
{ "deployment_branch_policy": { "protected_branches": false, "custom_branch_policies": true } }
EOF
add_deployment_policy staging main branch

# Required reviewers on a private repo need GitHub Pro; without it, prod still only deploys from v* tags.
if ! gh api --silent -X PUT "repos/$GITHUB_REPO/environments/prod" --input - <<EOF
{
  "reviewers": [{ "type": "User", "id": $reviewer_id }],
  "deployment_branch_policy": { "protected_branches": false, "custom_branch_policies": true }
}
EOF
then
  echo "  WARNING: no approval gate on prod (needs GitHub Enterprise for private repos). Re-run once Pro is active."
  gh api --silent -X PUT "repos/$GITHUB_REPO/environments/prod" --input - <<EOF
{ "deployment_branch_policy": { "protected_branches": false, "custom_branch_policies": true } }
EOF
fi
add_deployment_policy prod 'v*' tag

for env in "${ENVIRONMENTS[@]}"; do
  gh variable set AZURE_CLIENT_ID --repo "$GITHUB_REPO" --env "$env" --body "${CLIENT_IDS[$env]}"
done

echo "== Branch protection"
protect() { # branch, required checks (JSON array)
  gh api --silent -X PUT "repos/$GITHUB_REPO/branches/$1/protection" --input - <<EOF
{
  "required_status_checks": { "strict": false, "contexts": $2 },
  "enforce_admins": true,
  "required_pull_request_reviews": { "required_approving_review_count": 0 },
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
EOF
}
if ! protect dev '["backend", "frontend", "infra"]' || ! protect main '["backend", "frontend", "infra", "source-branch"]'; then
  echo "  WARNING: branch protection needs GitHub Pro for private repos. Re-run once Pro is active."
fi

echo "== Secret scanning and push protection"
gh api --silent -X PATCH "repos/$GITHUB_REPO" --input - <<EOF || echo "  (not available for this repo; enable it in Settings > Code security if you make the repo public)"
{ "security_and_analysis": { "secret_scanning": { "status": "enabled" }, "secret_scanning_push_protection": { "status": "enabled" } } }
EOF

echo "Done."
