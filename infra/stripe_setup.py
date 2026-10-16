"""Stripe test-mode setup for Athlos (design §11: the only real secrets are the Stripe key and the
webhook signing secret, one pair per environment, in that environment's Key Vault). Safe to re-run.

For each stage (dev, staging, prod) this:
  1. creates the Stripe webhook endpoint https://<stage gateway>/webhooks/stripe for the checkout
     events ordering-api handles (an existing one is replaced: Stripe shows a signing secret only
     when an endpoint is created);
  2. grants you "Key Vault Secrets Officer" on kv-athlos-<stage>, so you can write the secrets;
  3. stores stripe-secret-key and stripe-webhook-secret in kv-athlos-<stage>;
  4. sets STRIPE_CONFIGURED=true on the GitHub environment, so the next deploy wires them in.

Run it yourself, signed in with az (Azure for Students) and gh:
    python infra/stripe_setup.py
It asks for the test secret key (sk_test_...) without echoing it. Nothing is written to disk.
"""

import base64
import getpass
import json
import subprocess
import sys
import urllib.parse
import urllib.request

SUBSCRIPTION = "3f059998-c5af-4b64-83b6-f6e3c581b4fd"
GITHUB_REPO = "akazopidis/athlos"
STAGES = ["dev", "staging", "prod"]
EVENTS = [
    "checkout.session.completed",
    "checkout.session.async_payment_succeeded",
    "checkout.session.async_payment_failed",
    "checkout.session.expired",
    # Refunds made at Stripe and lost disputes refund the orders in Athlos; an opened dispute emails
    # the coaches (design §2.2).
    "charge.refunded",
    "charge.dispute.created",
    "charge.dispute.closed",
]
SECRETS_OFFICER_ROLE = "Key Vault Secrets Officer"


def run(*args, capture=True):
    result = subprocess.run(list(args), check=True, capture_output=capture, text=True, shell=sys.platform == "win32")
    return result.stdout.strip() if capture else ""


def stripe(key, method, path, form=None):
    data = None if form is None else urllib.parse.urlencode(form, doseq=True).encode()
    request = urllib.request.Request(f"https://api.stripe.com/v1{path}", data=data, method=method)
    request.add_header("Authorization", "Basic " + base64.b64encode(f"{key}:".encode()).decode())
    with urllib.request.urlopen(request) as response:
        return json.loads(response.read().decode())


def gateway_url(stage):
    fqdn = run("az", "containerapp", "show", "--subscription", SUBSCRIPTION, "-g", f"rg-athlos-{stage}",
               "-n", f"ca-athlos-gateway-{stage}", "--query", "properties.configuration.ingress.fqdn", "-o", "tsv")
    return f"https://{fqdn}"


def webhook_secret(key, stage, url):
    endpoints = stripe(key, "GET", "/webhook_endpoints?limit=100")["data"]
    for endpoint in endpoints:
        if endpoint["url"] == url:
            stripe(key, "DELETE", f"/webhook_endpoints/{endpoint['id']}")
    created = stripe(key, "POST", "/webhook_endpoints", {
        "url": url,
        "enabled_events[]": EVENTS,
        "description": f"Athlos {stage}: ordering-api checkout, refund and dispute events",
    })
    return created["secret"]


def store(stage, name, value):
    vault = f"kv-athlos-{stage}"
    me = run("az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv")
    scope = run("az", "keyvault", "show", "--subscription", SUBSCRIPTION, "-n", vault, "--query", "id", "-o", "tsv")
    run("az", "role", "assignment", "create", "--assignee-object-id", me, "--assignee-principal-type", "User",
        "--role", SECRETS_OFFICER_ROLE, "--scope", scope, "-o", "none")
    run("az", "keyvault", "secret", "set", "--subscription", SUBSCRIPTION, "--vault-name", vault,
        "-n", name, "--value", value, "-o", "none")


def main():
    key = getpass.getpass("Stripe test secret key (sk_test_...): ").strip()
    if not key.startswith("sk_test_"):
        raise SystemExit("Use a test-mode key (sk_test_...). Athlos runs Stripe in test mode only (design §10).")

    account = stripe(key, "GET", "/account")
    print(f"Stripe account: {account.get('settings', {}).get('dashboard', {}).get('display_name') or account['id']}")

    for stage in STAGES:
        url = f"{gateway_url(stage)}/webhooks/stripe"
        print(f"== {stage}: {url}")
        secret = webhook_secret(key, stage, url)
        store(stage, "stripe-secret-key", key)
        store(stage, "stripe-webhook-secret", secret)
        run("gh", "variable", "set", "STRIPE_CONFIGURED", "-R", GITHUB_REPO, "--env", stage, "--body", "true", capture=False)
        print("   webhook endpoint created, secrets stored, STRIPE_CONFIGURED=true")

    print("Done. Redeploy each stage (the next push to dev, merge to main and release) to wire the secrets in.")


if __name__ == "__main__":
    main()
