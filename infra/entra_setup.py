"""Entra External ID setup for Athlos (design §5). Safe to re-run.

Creates, in the external tenant, per stage (dev, staging, prod):
  - "Athlos API (<stage>)": the audience services validate; exposes the scope access_as_user and
    puts the user's email in access tokens;
  - "Athlos Web (<stage>)": the SPA (auth code + PKCE), redirecting to the stage's gateway,
    pre-consented to the API scope;
and one sign-up/sign-in user flow (email + password, collects the display name) for all web apps.

Run after `az login --tenant <external tenant> --allow-no-subscriptions`:
    python infra/entra_setup.py
Prints the values for the GitHub environment variables (AUTH_AUTHORITY, AUTH_AUDIENCE,
SPA_CLIENT_ID, API_SCOPE) and for local development.
"""

import json
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

TENANT_ID = "8daab877-07e4-4546-a727-554cc0d5be80"
TENANT_SUBDOMAIN = "athlos"
STUDENT_SUBSCRIPTION = "3f059998-c5af-4b64-83b6-f6e3c581b4fd"
STAGES = ["dev", "staging", "prod"]
LOCAL_SPA = "http://localhost:5173"  # the Vite dev server under Aspire (dev app only)

GRAPH = "https://graph.microsoft.com/v1.0"
MS_GRAPH_APP_ID = "00000003-0000-0000-c000-000000000000"
OPENID_SCOPE_ID = "37f7f235-527c-4136-accd-4a02d197296e"
OFFLINE_ACCESS_SCOPE_ID = "7427e0e9-2fba-42fe-b0c0-848c9e6a8182"


def az(*args):
    return subprocess.run(["az", *args], check=True, capture_output=True, text=True, shell=sys.platform == "win32").stdout.strip()


TOKEN = az("account", "get-access-token", "--subscription", TENANT_ID, "--resource-type", "ms-graph", "--query", "accessToken", "-o", "tsv")


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


def find_app(display_name):
    found = graph("GET", f"/applications?$filter=displayName eq '{display_name}'")["value"]
    return found[0] if found else None


def ensure_service_principal(app_id):
    found = graph("GET", f"/servicePrincipals?$filter=appId eq '{app_id}'")["value"]
    return found[0] if found else graph("POST", "/servicePrincipals", {"appId": app_id})


def ensure_grant(client_sp_id, resource_sp_id, scope):
    grants = graph("GET", f"/oauth2PermissionGrants?$filter=clientId eq '{client_sp_id}' and resourceId eq '{resource_sp_id}'")["value"]
    if grants:
        graph("PATCH", f"/oauth2PermissionGrants/{grants[0]['id']}", {"scope": scope})
    else:
        graph("POST", "/oauth2PermissionGrants",
              {"clientId": client_sp_id, "consentType": "AllPrincipals", "resourceId": resource_sp_id, "scope": scope})


def ensure_api(stage):
    name = f"Athlos API ({stage})"
    app = find_app(name)
    if app is None:
        app = graph("POST", "/applications", {
            "displayName": name,
            "signInAudience": "AzureADMyOrg",
            "api": {
                "requestedAccessTokenVersion": 2,
                "oauth2PermissionScopes": [{
                    "id": str(uuid.uuid4()),
                    "value": "access_as_user",
                    "type": "User",
                    "isEnabled": True,
                    "adminConsentDisplayName": "Use Athlos",
                    "adminConsentDescription": "Lets the Athlos web app call the Athlos API as the signed-in user.",
                    "userConsentDisplayName": "Use Athlos",
                    "userConsentDescription": "Lets the Athlos web app call the Athlos API for you.",
                }],
            },
        })
    # The email in the access token lets tenancy-api keep user_profiles current (design §5.2).
    graph("PATCH", f"/applications/{app['id']}", {
        "identifierUris": [f"api://{app['appId']}"],
        "optionalClaims": {"accessToken": [{"name": "email", "essential": False}]},
    })
    scope_id = graph("GET", f"/applications/{app['id']}")["api"]["oauth2PermissionScopes"][0]["id"]
    return app, scope_id, ensure_service_principal(app["appId"])


def ensure_web(stage, api_app, scope_id, gateway_url):
    name = f"Athlos Web ({stage})"
    redirect_uris = [gateway_url] + ([LOCAL_SPA] if stage == "dev" else [])
    settings = {
        "spa": {"redirectUris": redirect_uris},
        "requiredResourceAccess": [
            {"resourceAppId": api_app["appId"], "resourceAccess": [{"id": scope_id, "type": "Scope"}]},
            {"resourceAppId": MS_GRAPH_APP_ID, "resourceAccess": [
                {"id": OPENID_SCOPE_ID, "type": "Scope"},
                {"id": OFFLINE_ACCESS_SCOPE_ID, "type": "Scope"},
            ]},
        ],
    }
    app = find_app(name) or graph("POST", "/applications", {"displayName": name, "signInAudience": "AzureADMyOrg"})
    graph("PATCH", f"/applications/{app['id']}", settings)
    return app, ensure_service_principal(app["appId"])


USER_FLOW_NAME = "Athlos sign-up and sign-in"


# Email + password sign-up with email verification (Entra sends the code), collecting the display
# name; password reset comes with it. Athlos never sees a password (design §5.2).
def ensure_user_flow(web_app_ids):
    flows = graph("GET", "/identity/authenticationEventsFlows")["value"]
    flow = next((f for f in flows if f["displayName"] == USER_FLOW_NAME), None)
    if flow is None:
        flow = graph("POST", "/identity/authenticationEventsFlows", {
            "@odata.type": "#microsoft.graph.externalUsersSelfServiceSignUpEventsFlow",
            "displayName": USER_FLOW_NAME,
            "description": "Coaches and customers: one account for the whole marketplace.",
            "onInteractiveAuthFlowStart": {
                "@odata.type": "#microsoft.graph.onInteractiveAuthFlowStartExternalUsersSelfServiceSignUp",
                "isSignUpAllowed": True,
            },
            "onAuthenticationMethodLoadStart": {
                "@odata.type": "#microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp",
                "identityProviders": [{"id": "EmailPassword-OAUTH"}],
            },
            "onAttributeCollection": {
                "@odata.type": "#microsoft.graph.onAttributeCollectionExternalUsersSelfServiceSignUp",
                "attributes": [
                    {"id": "email", "displayName": "Email Address", "description": "Email address of the user",
                     "userFlowAttributeType": "builtIn", "dataType": "string"},
                    {"id": "displayName", "displayName": "Display Name", "description": "Display Name of the User.",
                     "userFlowAttributeType": "builtIn", "dataType": "string"},
                ],
                "attributeCollectionPage": {"views": [{"inputs": [
                    {"attribute": "email", "label": "Email Address", "inputType": "text", "hidden": True,
                     "editable": False, "writeToDirectory": True, "required": True},
                    {"attribute": "displayName", "label": "Your name", "inputType": "text", "hidden": False,
                     "editable": True, "writeToDirectory": True, "required": True},
                ]}]},
            },
        })

    linked = {a["appId"] for a in graph("GET", f"/identity/authenticationEventsFlows/{flow['id']}/conditions/applications/includeApplications")["value"]}
    for app_id in web_app_ids:
        if app_id not in linked:
            graph("POST", f"/identity/authenticationEventsFlows/{flow['id']}/conditions/applications/includeApplications",
                  {"@odata.type": "#microsoft.graph.authenticationConditionApplication", "appId": app_id})
    return flow


def gateway_url(stage):
    fqdn = az("containerapp", "show", "--subscription", STUDENT_SUBSCRIPTION, "-g", f"rg-athlos-{stage}",
              "-n", f"ca-athlos-gateway-{stage}", "--query", "properties.configuration.ingress.fqdn", "-o", "tsv")
    return f"https://{fqdn}"


def main():
    graph_sp = ensure_service_principal(MS_GRAPH_APP_ID)
    authority = f"https://{TENANT_SUBDOMAIN}.ciamlogin.com/{TENANT_ID}/v2.0"
    web_app_ids = []
    output = {}

    for stage in STAGES:
        api_app, scope_id, api_sp = ensure_api(stage)
        web_app, web_sp = ensure_web(stage, api_app, scope_id, gateway_url(stage))
        # Admin consent, so users are never asked to approve Athlos's own API.
        ensure_grant(web_sp["id"], api_sp["id"], "access_as_user")
        ensure_grant(web_sp["id"], graph_sp["id"], "openid offline_access")
        web_app_ids.append(web_app["appId"])
        output[stage] = {
            "AUTH_AUTHORITY": authority,
            "AUTH_AUDIENCE": api_app["appId"],
            "SPA_CLIENT_ID": web_app["appId"],
            "API_SCOPE": f"api://{api_app['appId']}/access_as_user",
        }

    ensure_user_flow(web_app_ids)
    print(json.dumps(output, indent=2))


if __name__ == "__main__":
    main()
