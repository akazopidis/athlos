# Limitations and future work

What Athlos (v1.6.0, October 2026) does not do, or does only partly, collected in one place as input for the thesis's limitations and future-work chapter. Each entry says what is missing, why, what it would mean for a real launch, and how it would be solved.

The list comes from the design (`docs/design.md`; section numbers below refer to it), the evaluation (`targets.md`, `noisy-neighbor.md`) and a review of edge cases on 16 October 2026. Edge cases that review found and fixed in v1.5.1 and v1.6.0 are not repeated here; they are in the design's §2.2.

The limitations fall into three kinds:
- **Scope.** Needed for a commercial launch, but outside a thesis about multi-tenant architecture. Most are product and legal work.
- **Budget.** The fix exists and is known, but costs more than the €100-for-12-months budget allows.
- **Trade-off.** A decision taken on purpose, with its cost written down.

## Summary

| # | Limitation | Kind | Area |
|---|---|---|---|
| 1 | No VAT or invoices | Scope | Legal, payments |
| 2 | Legal pages are drafts; coaches accept no terms | Scope | Legal |
| 3 | The data export is one synchronous JSON file | Trade-off | Legal, GDPR |
| 4 | Customers lose bought programs when a coach deletes their workspace | Trade-off | Tenant lifecycle |
| 5 | No program versioning | Scope | Product |
| 6 | Stripe in test mode only | Scope | Payments |
| 7 | Some refund cases are settled by hand | Trade-off | Payments |
| 8 | Disputes cost the platform, not the coach | Trade-off | Payments |
| 9 | Coach plans are not billed | Scope | Business model |
| 10 | Backups kept 7 days in one region, restore never tested | Budget | Operations |
| 11 | One PostgreSQL server for all three stages | Budget | Operations, isolation |
| 12 | One Container Apps environment for all three stages | Budget | Operations, isolation |
| 13 | Cold starts of about 90 seconds | Budget | Performance |
| 14 | The rate limit grows with each gateway replica | Budget | Noisy neighbor |
| 15 | One coach's flood can block that coach's own visitors | Trade-off | Noisy neighbor |
| 16 | One signing key shared by the gateway and every service | Trade-off | Security |
| 17 | No CDN or web application firewall | Budget | Security, performance |
| 18 | Workspace deletion is not one transaction | Trade-off | Tenant lifecycle |
| 19 | No admin deletion of a workspace; deleted sign-ins kept 30 days | Scope | Tenant lifecycle |
| 20 | Email from a Microsoft-managed domain | Scope | Email |
| 21 | Shop features of a mature marketplace are missing | Scope | Product |
| 22 | Gaps in automated testing | Scope | Quality |
| 23 | Slow first paint on a slow phone | Trade-off | Frontend |

## Legal and compliance

### 1. No VAT or invoices
- **Missing:** Athlos charges the program's price as is. It calculates no VAT, issues no invoices, and keeps no VAT records.
- **Why:** tax compliance is outside the thesis.
- **Impact:** with separate charges and transfers (§2.2), the platform is the merchant of record. Under EU rules for marketplaces selling digital services, it would owe VAT at the customer's country rate and report it through the OSS (One-Stop Shop) scheme. Selling without this is not legal in the EU.
- **Solution:** Stripe Tax on Checkout (automatic tax calculation, plus customer address collection) and Stripe invoices or receipts. Stripe Tax costs about 0.5% per transaction. Prices would need to be stored as VAT-inclusive or exclusive, and each order's tax amount recorded in ordering-api.

### 2. Legal pages are drafts; coaches accept no terms
- **Built (v1.12.0, design §5.4):** Terms, Privacy and Refund policy pages linked from the footer; customers accept the Terms at checkout, and each checkout keeps the accepted version.
- **Missing:** a lawyer's review of the texts, an imprint naming the operator, and terms for coaches: a coach opens a workspace without accepting platform terms.
- **Why:** these are legal texts, not engineering; the pages describe what the platform does, as a starting point.
- **Impact:** required before any public launch with real payments (GDPR transparency, EU consumer law, the platform's terms with coaches).
- **Solution:** a reviewed text, and the same version check for coaches when they open a workspace, with the accepted version recorded in tenancy-api.

### 3. The data export is one synchronous JSON file
- **Built (v1.12.0, design §5.4):** "Download my data" (`GET /api/v1/me/export` on tenancy-api) gathers the user's own rows from every service through signed internal routes and hands back one JSON file: the rights of access and portability (GDPR articles 15 and 20) next to erasure (§5.2).
- **Missing:** an asynchronous export for large accounts, and a coach's export of their workspace (programs, sales).
- **Why:** a customer's data is small; one request is enough.
- **Impact:** the export waits for the slowest service, and a cold start can make the first try time out (it answers 502 and the user tries again).
- **Solution:** a queued export job writing the file to Blob Storage and emailing a short-lived link.

### 4. Customers lose bought programs when a coach deletes their workspace
- **Missing:** a way for customers to keep paid content after the coach erases their workspace (§3.4). Since v1.7.0 a workspace cannot be deleted while any sale can still be refunded, and its open payment pages are closed first, so the loss only reaches purchases older than the refund window.
- **Why:** a trade-off between two rights. The coach's right to erasure removes their content, the bought programs included. Keeping copies would mean keeping the coach's data after they asked for it to be erased.
- **Impact:** a customer who bought more than 14 days earlier loses access to what they paid for, with no refund. A coach who wants their data erased must wait until the window of their last sale ends, or refund those sales.
- **Solution, in order of effort:**
  - refund refundable sales automatically as part of deletion, instead of waiting;
  - give customers a notice period (for example 30 days) before the content goes.

## Payments

### 5. No program versioning
- **Missing:** a bought program is a live link to the coach's current content. When the coach edits workouts, every customer sees the edited version.
- **Why:** versioning adds a content model the thesis does not need.
- **Impact:** a coach can change or empty a program after selling it. Customers have no record of what they paid for.
- **Solution:** publish creates an immutable version (programs, workouts) and each enrollment points to the version bought, possibly with "upgrade to the latest version" offered to the customer. This touches catalog-api (versions under the same tenant, same RLS) and enrollment-api.

### 6. Stripe in test mode only
- **Missing:** live payments.
- **Why:** the thesis has no registered business. Live mode needs Stripe's business verification for the platform, and real identity verification for every coach's Connect account.
- **Impact:** every payment in every stage is a test payment. The flows are real (Checkout, Connect, transfers, refunds, webhooks), but the money is not.
- **Solution:** a registered company, live keys per stage in Key Vault (only prod should use live keys), and a review of Stripe's Connect obligations for platforms (KYC, negative balances, payout schedules).

### 7. Some refund cases are settled by hand
- **Missing:** automatic handling in three rare cases. Each is logged, and the dead-letter alert covers failed messages:
  - a refund made in the Stripe dashboard without an `athlos_order_id` in its metadata, which Athlos cannot map to a program;
  - a refund at Stripe or a lost dispute whose transfer reversal Stripe refuses: the customer is refunded and loses access, but the coach keeps their share;
  - refunds for a suspended coach: the studio is closed, and the platform admin has no refund tool, only the Stripe dashboard.
- **Why:** each needs a decision by a person, or a back-office tool beyond the thesis.
- **Solution:** a refund page in the platform admin (`/admin`), with a list of reversals Stripe refused, retried like failed payouts (`payout_retries`, §2.2).

### 8. Disputes cost the platform, not the coach
- **Missing:** passing the dispute fee on to the coach, and acting while a dispute is open. When a customer disputes a payment, Stripe takes the amount and a dispute fee from the platform at once. Athlos tells the coach (`sale-disputed`) and waits for the outcome. A lost dispute refunds the order and reverses the coach's share; the fee stays with the platform.
- **Why:** charging coaches for disputes needs platform terms (limitation 2) and a way to debit their account.
- **Solution:** take the dispute fee back from the coach's Connect account (a transfer reversal for the fee, or Stripe's `debit` on Express accounts where available), and let coaches submit evidence through the studio.

### 9. Coach plans are not billed
- **Missing:** coaches pay nothing for the Pro plan. The platform admin assigns plans (§3.4), and the plan sets rate limits and quotas (§4).
- **Why:** the thesis studies what plans do to tenant isolation, not subscription billing.
- **Impact:** the only revenue is the 10% platform fee per sale (`Payments:PlatformFeePercent`).
- **Solution:** Stripe Billing subscriptions per workspace. A `customer.subscription.updated` webhook on tenancy-api would set the plan, and the gateway already picks plan changes up within 60 seconds.

## Operations

### 10. Backups kept 7 days in one region, restore never tested
- **Missing:** a restore drill, and backups kept outside `italynorth`. PostgreSQL Flexible Server keeps automatic backups for 7 days (`backupRetentionDays: 7`, `geoRedundantBackup: Disabled`). Blob Storage (cover images) has no soft delete or versioning.
- **Why:** budget and the single allowed region (§10). Geo-redundant backups need a paired region the subscription's policy blocks.
- **Impact:** a lost region loses everything. A bad migration found more than 7 days later cannot be undone. Nobody knows how long a restore takes, so there is no measured recovery time (RTO).
- **Solution:** a scripted point-in-time restore to a temporary server, timed and recorded like the other evaluation numbers. Then longer retention, Blob soft delete, and with another region, geo-redundant backups.

### 11. One PostgreSQL server for all three stages
- **Missing:** separate database servers per stage. Dev, staging and prod have separate databases and roles on one Burstable B1ms server (free for 12 months), with at most 3 connections per service (CLAUDE.md).
- **Why:** budget. A second server is about €13 a month or more.
- **Impact:**
  - a load test on staging competes with prod for the same CPU and memory;
  - a server outage or upgrade takes down all three stages at once;
  - stage isolation rests on database roles, not on separate servers;
  - B1ms is a burstable tier: sustained load uses up its CPU credits.
- **Solution:** one server per stage, or at least for prod. Nothing in the code changes, only connection strings.

### 12. One Container Apps environment for all three stages
- **Missing:** separate compute per stage. The subscription allows one Container Apps environment per region, so `cae-athlos-shared` runs dev, staging and prod side by side (§11).
- **Why:** the subscription quota.
- **Impact:** stages are separated by identities, Key Vaults and signing keys, not by network or platform. A platform problem hits all three, and a load test on staging shares the environment with prod. The noisy-neighbor experiment ran on staging for this reason.
- **Solution:** with more quota, the environment moves back into `env.bicep` per stage, unchanged.

### 13. Cold starts of about 90 seconds
- **Missing:** fast first requests after idle. Every app scales to zero (`minReplicas: 0`, §10). The first request after about 5 minutes without traffic waits for up to three apps to start in turn: the gateway, tenancy-api for the plan lookup, then catalog-api and its first database connection. Median 87 s (70–97 s, 4 samples); warm requests take 0.2–0.3 s (`targets.md`, "Cold start").
- **Why:** budget. Scale to zero keeps the cost at about €3 a month.
- **Impact:**
  - the first visitor after a quiet period sees a long wait, and the gateway also serves the SPA, so even the page load waits;
  - Stripe's webhook calls can time out during a cold start and are retried later. v1.5.1 made a refund that arrives before its payment's event wait for that event, rather than be lost;
  - the end-to-end tests run a warm-up step first.
- **Solution, in order of cost:**
  - `minReplicas: 1` for the gateway alone, which hides most of the delay, at about €10–15 a month;
  - keep the apps warm with a scheduled ping (cheap, but it defeats scale to zero during the day);
  - smaller images, and native AOT for the gateway, to shorten each start.

## Noisy neighbor and security

### 14. The rate limit grows with each gateway replica
- **Missing:** an exact per-coach limit. The rate limiter keeps its token buckets in each gateway replica's memory (§4). With 2 replicas, a coach gets up to twice their plan's limit. Measured: about 30–40 req/s got through against the Free plan's 20 (`noisy-neighbor.md`, rounds 1 and 2).
- **Why:** budget. A shared limiter needs Redis, about €13 a month or more.
- **Impact:** limits are approximate. The experiment showed the extra traffic did not hurt the other coach at 0.5 vCPU, so the gateway keeps `maxReplicas: 2`.
- **Solution:** a distributed limiter on Redis, or dividing each limit by the replica count (exact only while the replica count stays fixed).

### 15. One coach's flood can block that coach's own visitors
- **Missing:** protection for a flooded coach's real visitors. A flood of one coach's public storefront spends that coach's budget, so their own visitors also get 429 responses, while every other coach is unaffected (§4).
- **Why:** this is the isolation the experiment set out to show: the damage stays inside the tenant. It also means one abusive client can do it on purpose.
- **Solution:** a per-client limit on storefront traffic stops a single client but not a distributed one. A CDN with a web application firewall (limitation 17) handles the rest.

### 16. One signing key shared by the gateway and every service
- **Missing:** per-service credentials between services. The gateway signs every request it forwards (HMAC-SHA256), and services accept only signed requests (§3.2). There is one key per stage, shared by the gateway and every service, and a signed request can be replayed for up to 60 seconds.
- **Why:** a simple, testable mechanism that keeps every service gateway-only (layer 1 of the isolation design).
- **Impact:** a compromised service could sign requests as the gateway, including requests for another tenant. Replay needs access inside the platform, since all traffic is TLS.
- **Solution:** per-service keys, or workload identity tokens: the gateway's managed identity requests an Entra token for each service, and each service validates the token's audience. Short-lived tokens or a nonce cache would close the replay window.

### 17. No CDN or web application firewall
- **Missing:** a CDN and a web application firewall (WAF) in front of the gateway. The gateway serves both the API and the SPA (§8.1), straight from `italynorth`, with no global edge and no WAF. Private endpoints are not used either; VNets are (§11).
- **Why:** budget, and Static Web Apps is not offered in the allowed regions. Front Door costs about €30 a month or more.
- **Impact:** every visitor fetches the SPA from Italy. There is no edge protection against floods or common web attacks beyond the gateway's own rate limits and validation.
- **Solution:** Azure Front Door (Standard or Premium, with WAF) in front of the gateway, caching the SPA's static files.

## Tenant lifecycle and accounts

### 18. Workspace deletion is not one transaction
- **Missing:** an all-or-nothing deletion. Deleting a workspace calls catalog-api, then enrollment-api, then marks the tenant deleted in tenancy-api (§3.4). There is no distributed transaction.
- **Why:** database per service. Every step is idempotent instead.
- **Impact:** a crash between steps leaves a partly erased but still active workspace, until the owner retries.
- **Solution:** a deletion saga driven by a Service Bus queue: retried until every service confirms, with the workspace marked "Deleting" in the meantime.

### 19. No admin deletion of a workspace; deleted sign-ins kept 30 days
- **Missing:** two small pieces of the tenant and account lifecycle.
  - Only the owner can delete a workspace. The platform admin can suspend one, but not delete it.
  - When a customer deletes their account, Entra keeps the deleted user for 30 days before removing it for good (§5.2).
- **Why:** both were left as future work.
- **Solution:**
  - an admin route that runs the same deletion as the owner's;
  - one more Microsoft Graph call, `DELETE /directory/deletedItems/{id}`, to remove the sign-in at once.

### 20. Email from a Microsoft-managed domain
- **Missing:** email from Athlos's own domain. Athlos's emails come from an Azure-managed `azurecomm.net` address. Entra's sign-up and password-reset emails come from a Microsoft sender with Athlos branding (§5.2, §5.3).
- **Why:** no owned domain. A custom domain for Entra's own emails needs a custom email provider extension.
- **Impact:** a weaker brand and lower deliverability: shared domains are more likely to land in spam. There is also no handling of bounces or complaints.
- **Solution:** an owned domain with SPF, DKIM and DMARC in Azure Communication Services, and an event subscription for bounces.

## Product and quality

### 21. Shop features of a mature marketplace are missing
Each of these is ordinary product work with no new multi-tenancy question:
- **Reviews (since v1.11.0, design §2.4):** only buyers review, and a refund takes the review away. Still missing:
  - *Moderation (since v1.12.0) is by hand.* Reports reach the platform admin, who hides or keeps each review; there is no automatic filter for abusive words, and a hidden review can be shown again only while it is reported again.
  - *Two copies of the rating.* enrollment-api owns the reviews and catalog keeps the count and average for cards and sorting. The copy is written before the review commits, so the two can only differ if that commit fails after catalog took the new values; the next review of the program sets it right.
- **Discount codes (since v1.11.0):** percentages only, one code per cart, no "once per customer" rule, and two customers paying at the same moment for a code's last use can both get it (the use count is read, not locked).
- **Saved programs (since v1.11.0):** no email when a saved program's price drops.
- **Search engine visibility:** storefronts and program pages have their own titles and link previews (v1.10.0), and the gateway serves `robots.txt` and a sitemap (v1.13.0), but there is no server-side rendering.
- **Search quality:** search is a case-insensitive text match on title, description and the coach's address (not the coach's display name, which catalog does not hold), with no full-text index, ranking or typo tolerance (§8). A PostgreSQL full-text or trigram index would be the next step.
- **Languages and currencies:** English only, euros only. Money is already stored as minor units plus a currency code, so the data model is ready.
- **Cart:** it lives in the browser, not in the account, so it does not follow the customer to another device and is shared by two accounts on one browser. (Since v1.8.1 it shows current prices and names any that changed.)
- **Categories:** programs created before v1.5.0 became "General fitness" until their coaches pick a category.
- **Customer support:** no help center or ticketing. Customers reach a coach through the coach's contact email, if set (v1.6.0); there is no way to reach the platform.

### 22. Gaps in automated testing
- **Missing:** some kinds of automated test. The backend has 462 tests on every PR, 195 of them in the isolation suite. Playwright covers, after every staging deploy, the shop, the cart, layouts on three screen sizes, the account pages and saved programs, a studio's lifecycle (with a discount code) on a throwaway workspace, axe accessibility checks on the public pages, and a signed-in checkout up to Stripe's page.
  - *Not covered end to end:* refunds and the platform admin. A whole purchase with Stripe's test card, then a review, runs once `E2E_PURCHASE_PROGRAM_PATH` names a second program of an onboarded coach on staging (v1.12.0); until then it is skipped.
  - *No load test of checkout or webhooks:* reads are load- and stress-tested on staging (k6, `stress.md`); writes are covered by concurrency tests (concurrent reviews leave an exact rating, concurrent saves keep one row) and contract tests between the services (v1.12.0), not by load.
- **Why:** time. The isolation suite was the priority, because isolation is the subject of the thesis.
- **Solution:**
  - the second staging program for the purchase test;
  - many test accounts, to load-test writes past the per-customer write limit;
  - a k6 scenario against Stripe's test mode for checkout, within Stripe's rate limits.

### 23. Slow first paint on a slow phone
- **Missing:** a fast first paint on a slow connection. On Lighthouse's simulated slow 4G phone the shop paints its first content after 3.6 s (4.6 s before MSAL loaded on demand, v1.15.0), against 1.8 s for "good" (`lighthouse.md`). On desktop it is about 1 s, and accessibility, best practices and SEO are 100.
- **Why:** the SPA renders in the browser only. Nothing shows until about 160 kB of compressed JavaScript has run and `/config.json` has arrived. v1.14.0 and v1.15.0 removed the avoidable delays: fonts and `/config.json` are preloaded, built files are cached for a year, uploaded images are shrunk, and MSAL (62 kB) loads only for visitors who sign in.
- **Solution:**
  - server-side rendering, or a static shell for the public pages, served by the gateway;
  - resizing the covers uploaded before v1.16.0, which have no card-size version (since v1.16.0 the studio uploads one, and the shop's cards use it).
