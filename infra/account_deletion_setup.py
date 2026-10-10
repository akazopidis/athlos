"""Account deletion setup for Athlos (design §5.2: "delete the Entra user through Microsoft Graph").
Safe to re-run.

For each stage (dev, staging, prod) this:
  1. creates, in the External ID tenant, the app "Athlos account deletion (<stage>)" with Microsoft
     Graph's application permission User.ReadWrite.All, and grants admin consent for it;
  2. adds a client secret (valid 2 years) and stores it as entra-deletion-client-secret in
     kv-athlos-<stage> (it grants you "Key Vault Secrets Officer" there to write it);
  3. sets ACCOUNT_DELETION_CLIENT_ID on the GitHub environment, so the next deploy wires it in.

Each run adds a new secret and removes the app's older ones, so only the stored secret works.

Run it yourself, signed in to both tenants with az, and with gh:
    az login --tenant 8daab877-07e4-4546-a727-554cc0d5be80 --allow-no-subscriptions   (External ID)
    az login                                                                           (Azure for Students)
    python infra/account_deletion_setup.py
Nothing is written to disk; the secret goes straight into Key Vault.
"""

import json
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone

TENANT_ID = "8daab877-07e4-4546-a727-554cc0d5be80"  # Entra External ID tenant "athlos"
SUBSCRIPTION = "3f059998-c5af-4b64-83b6-f6e3c581b4fd"  # Azure for Students: the Key Vaults
GITHUB_REPO = "akazopidis/athlos"
STAGES = ["dev", "staging", "prod"]

GRAPH = "https://graph.microsoft.com/v1.0"
MS_GRAPH_APP_ID = "00000003-0000-0000-c000-000000000000"
USER_READWRITE_ALL_ROLE = "741f803b-c850-494e-b5df-cde7c675a1ca"  # Graph application permission
SECRETS_OFFICER_ROLE = "Key Vault Secrets Officer"


def run(*args, capture=True):
    result = subprocess.run(list(args), check=True, capture_output=capture, text=True, shell=sys.platform == "win32")
    return result.stdout.strip() if capture else ""


TOKEN = run("az", "account", "get-access-token", "--tenant", TENANT_ID, "--resource-type", "ms-graph", "--query", "accessToken", "-o", "tsv")


def graph(method, path, body=None):
    request = urllib.request.Request(
        GRAPH + urllib.parse.quote(path, safe="/?$=&'(),:-"),
        method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Authorization": f"Bearer {TOKEN}", "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request) as response:
            text = response.read().decode()
            return json.loads(text) if text else None
    except urllib.error.HTTPError as error:
        raise SystemExit(f"{method} {path} failed: {error.code} {error.read().decode()}") from None


def service_principal(app_id):
    found = graph("GET", f"/servicePrincipals?$filter=appId eq '{app_id}'")["value"]
    return found[0] if found else graph("POST", "/servicePrincipals", {"appId": app_id})


def ensure_app(stage):
    name = f"Athlos account deletion ({stage})"
    access = [{"resourceAppId": MS_GRAPH_APP_ID, "resourceAccess": [{"id": USER_READWRITE_ALL_ROLE, "type": "Role"}]}]
    found = graph("GET", f"/applications?$filter=displayName eq '{name}'")["value"]
    if found:
        app = found[0]
        graph("PATCH", f"/applications/{app['id']}", {"requiredResourceAccess": access})
    else:
        app = graph("POST", "/applications", {"displayName": name, "signInAudience": "AzureADMyOrg", "requiredResourceAccess": access})
    return app


def ensure_admin_consent(app_sp, graph_sp):
    assigned = graph("GET", f"/servicePrincipals/{app_sp['id']}/appRoleAssignments")["value"]
    if not any(a["appRoleId"] == USER_READWRITE_ALL_ROLE and a["resourceId"] == graph_sp["id"] for a in assigned):
        graph("POST", f"/servicePrincipals/{app_sp['id']}/appRoleAssignments",
              {"principalId": app_sp["id"], "resourceId": graph_sp["id"], "appRoleId": USER_READWRITE_ALL_ROLE})


def new_secret(app):
    expires = (datetime.now(timezone.utc) + timedelta(days=730)).strftime("%Y-%m-%dT%H:%M:%SZ")
    created = graph("POST", f"/applications/{app['id']}/addPassword",
                    {"passwordCredential": {"displayName": "athlos tenancy-api", "endDateTime": expires}})
    for old in app.get("passwordCredentials", []):
        if old["keyId"] != created["keyId"]:
            graph("POST", f"/applications/{app['id']}/removePassword", {"keyId": old["keyId"]})
    return created["secretText"]


def store(stage, name, value):
    vault = f"kv-athlos-{stage}"
    me = run("az", "account", "show", "--subscription", SUBSCRIPTION, "--query", "user.name", "-o", "tsv")
    scope = run("az", "keyvault", "show", "--subscription", SUBSCRIPTION, "-n", vault, "--query", "id", "-o", "tsv")
    run("az", "role", "assignment", "create", "--subscription", SUBSCRIPTION, "--assignee", me,
        "--role", SECRETS_OFFICER_ROLE, "--scope", scope, "-o", "none")
    run("az", "keyvault", "secret", "set", "--subscription", SUBSCRIPTION, "--vault-name", vault,
        "-n", name, "--value", value, "-o", "none")


def main():
    graph_sp = service_principal(MS_GRAPH_APP_ID)
    for stage in STAGES:
        app = ensure_app(stage)
        ensure_admin_consent(service_principal(app["appId"]), graph_sp)
        store(stage, "entra-deletion-client-secret", new_secret(graph("GET", f"/applications/{app['id']}")))
        run("gh", "variable", "set", "ACCOUNT_DELETION_CLIENT_ID", "-R", GITHUB_REPO, "--env", stage, "--body", app["appId"], capture=False)
        print(f"== {stage}: app {app['appId']}, consent granted, secret in kv-athlos-{stage}, ACCOUNT_DELETION_CLIENT_ID set")

    print("Done. Redeploy each stage (the next push to dev, merge to main and release) to wire it in.")


if __name__ == "__main__":
    main()
