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
| 2 | No legal pages (terms, privacy, cookies) | Scope | Legal |
| 3 | No personal data export | Scope | Legal, GDPR |
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

## Legal and compliance

### 1. No VAT or invoices
- **Missing:** Athlos charges the program's price as is. It calculates no VAT, issues no invoices, and keeps no VAT records.
- **Why:** tax compliance is outside the thesis.
- **Impact:** with separate charges and transfers (§2.2), the platform is the merchant of record. Under EU rules for marketplaces selling digital services, it would owe VAT at the customer's country rate and report it through the OSS (One-Stop Shop) scheme. Selling without this is not legal in the EU.
- **Solution:** Stripe Tax on Checkout (automatic tax calculation, plus customer address collection) and Stripe invoices or receipts. Stripe Tax costs about 0.5% per transaction. Prices would need to be stored as VAT-inclusive or exclusive, and each order's tax amount recorded in ordering-api.

### 2. No legal pages
- **Missing:** terms of service for customers and coaches, a privacy policy, a cookie notice and an imprint.
- **Why:** these are legal texts, not engineering.
- **Impact:** required before any public launch (GDPR transparency, EU consumer law, the platform's terms with coaches). Athlos sets no tracking cookies. The sign-in library keeps tokens in browser storage and the cart lives in local storage, both strictly necessary, but the privacy policy must say so.
- **Solution:** static pages served by the SPA and linked from the footer and from sign-up. Coaches would accept the platform terms when they open a workspace, with the accepted version recorded in tenancy-api.

### 3. No personal data export
- **Missing:** a "download my data" option. Account deletion exists (§5.2): the right to erasure is implemented, but the rights of access and portability (GDPR articles 15 and 20) are not.
- **Why:** deletion was the harder, more interesting problem across tenants; export follows the same path.
- **Impact:** requests would have to be answered by hand from four databases.
- **Solution:** `GET /api/v1/me/export` on tenancy-api, fanning out to each service's internal route for the user's own rows (the same cross-tenant slices account deletion already uses), assembled into one JSON file. Without an async job, the export is bounded by the slowest service.

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
- **Reviews and ratings:** none.
- **Search engine visibility:** the SPA has no per-page titles for crawlers, no social preview tags, no sitemap and no server-side rendering.
- **Search quality:** search is a case-insensitive text match on title and description, with no full-text index, ranking or typo tolerance (§8). A PostgreSQL full-text or trigram index would be the next step.
- **Languages and currencies:** English only, euros only. Money is already stored as minor units plus a currency code, so the data model is ready.
- **Cart:** it lives in the browser, not in the account, so it does not follow the customer to another device and is shared by two accounts on one browser. Its total shows the prices seen when items were added; Stripe charges the current price.
- **Categories:** programs created before v1.5.0 became "General fitness" until their coaches pick a category.
- **Customer support:** no help center or ticketing. Customers reach a coach through the coach's contact email, if set (v1.6.0); there is no way to reach the platform.

### 22. Gaps in automated testing
- **Missing:** some kinds of automated test. The backend has 320 tests on every PR, 163 of them in the isolation suite. Playwright covers the shop, the cart, layouts on three screen sizes, and a signed-in checkout up to Stripe's page, after every staging deploy.
  - *Not covered end to end:* the studio, refunds, the platform admin, or paying on Stripe's page itself (only a person may enter card details).
  - *No automated accessibility checks:* for example, axe in the Playwright suite.
  - *No load test of checkout or webhooks:* only of reads (k6).
- **Why:** time. The isolation suite was the priority, because isolation is the subject of the thesis.
- **Solution:**
  - Playwright tests for a coach's studio, with a seeded test coach;
  - `@axe-core/playwright` on each page;
  - a k6 scenario against Stripe's test mode for checkout, within Stripe's rate limits.
