"""Platform admin setup for Athlos (design §1, §5: "One Entra app role, Platform.Admin, covers the
admin"). Safe to re-run.

For each stage (dev, staging, prod), or the ones named with --stage, this:
  1. adds the app role Platform.Admin (for users) to "Athlos API (<stage>)", the app registration
     created by entra_setup.py, keeping any roles it already has;
  2. assigns it to the user given with --user (the email they sign in with, or their object id in
     the External ID tenant), so their access tokens carry "roles": ["Platform.Admin"].

The user needs to sign out and in again (or wait for their token to expire) to get the role.
--remove takes the assignment away instead.

Run it yourself, signed in to the External ID tenant with az:
    az login --tenant 8daab877-07e4-4546-a727-554cc0d5be80 --allow-no-subscriptions
    python infra/platform_admin_setup.py --user <email or object id> [--stage dev] [--remove]
Prints the user's object id, never their email.
"""

import argparse
import json
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

TENANT_ID = "8daab877-07e4-4546-a727-554cc0d5be80"  # Entra External ID tenant "athlos"
TENANT_DOMAIN = "athlos.onmicrosoft.com"
STAGES = ["dev", "staging", "prod"]
ROLE = "Platform.Admin"

GRAPH = "https://graph.microsoft.com/v1.0"


def run(*args):
    return subprocess.run(list(args), check=True, capture_output=True, text=True, shell=sys.platform == "win32").stdout.strip()


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


def find_user(user):
    try:
        return graph("GET", f"/users/{uuid.UUID(user)}?$select=id")["id"]
    except ValueError:
        pass
    # External ID users sign in with their email as an identity of the tenant (design §5.2).
    quoted = user.replace("'", "''")
    found = graph("GET", f"/users?$select=id&$filter=identities/any(i:i/issuerAssignedId eq '{quoted}' and i/issuer eq '{TENANT_DOMAIN}')")["value"]
    if not found:
        raise SystemExit("No user signs in with that address in the External ID tenant.")
    return found[0]["id"]


def ensure_role(stage):
    name = f"Athlos API ({stage})"
    found = graph("GET", f"/applications?$filter=displayName eq '{name}'")["value"]
    if not found:
        raise SystemExit(f"{name} does not exist; run infra/entra_setup.py first.")
    app = graph("GET", f"/applications/{found[0]['id']}?$select=id,appId,appRoles")
    role = next((r for r in app["appRoles"] if r["value"] == ROLE), None)
    if role is None:
        role = {
            "id": str(uuid.uuid4()),
            "value": ROLE,
            "displayName": "Platform admin",
            "description": "Manages every workspace on Athlos: suspends and reactivates them and assigns plans.",
            "allowedMemberTypes": ["User"],
            "isEnabled": True,
        }
        graph("PATCH", f"/applications/{app['id']}", {"appRoles": [*app["appRoles"], role]})
    sp = graph("GET", f"/servicePrincipals?$filter=appId eq '{app['appId']}'&$select=id")["value"][0]
    return sp["id"], role["id"]


def set_assignment(sp_id, role_id, user_id, remove):
    assignments = graph("GET", f"/servicePrincipals/{sp_id}/appRoleAssignedTo")["value"]
    existing = next((a for a in assignments if a["principalId"] == user_id and a["appRoleId"] == role_id), None)
    if remove and existing:
        graph("DELETE", f"/servicePrincipals/{sp_id}/appRoleAssignedTo/{existing['id']}")
    elif not remove and not existing:
        graph("POST", f"/servicePrincipals/{sp_id}/appRoleAssignedTo", {"principalId": user_id, "resourceId": sp_id, "appRoleId": role_id})


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--user", required=True, help="the email the user signs in with, or their object id")
    parser.add_argument("--stage", choices=STAGES, action="append", help="only these stages (default: all)")
    parser.add_argument("--remove", action="store_true", help="take the role away instead")
    args = parser.parse_args()

    user_id = find_user(args.user)
    for stage in args.stage or STAGES:
        sp_id, role_id = ensure_role(stage)
        set_assignment(sp_id, role_id, user_id, args.remove)
        print(f"{stage}: {ROLE} {'removed from' if args.remove else 'assigned to'} user {user_id}")


if __name__ == "__main__":
    main()
