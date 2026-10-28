# Athlos: what's left to build

As of 26 October 2026 (v1.17.0).

## How to read this

58 items are left. 51 rows are ranked from the most important down, and 13 rows wait until the thesis is published:
- 7 to fix now
- 4 before real customers
- 12 to run it in production
- 11 missing tests
- 10 product gaps
- 7 for later
- 13 that add cost

Six items are split. Their free part is ranked, and the part that costs money waits with the others. Item numbers are those of the list from 25 October.

The scan covered all 100 routes (the four OpenAPI documents plus the internal ones), the design section by section against the code and Bicep, the CI workflows, every test suite and each user flow end to end. Items already in [`limitations.md`](limitations.md) keep their number there (#n); the rest are new.

- **Cost:** decided on 26 October 2026: nothing that adds cost is built before the thesis is published. Priorities 1–6 run inside what Athlos already pays for and the free allowances. A few add fractions of a cent, such as one more email (about €0.00025 each) or a short-lived export file in Blob Storage; those count as no cost here.
- **Effort:** S = up to half a day, M = one to three days, L = more than three days.
- **Needs:** Code = I can do it alone · You = your decision first · Budget = it costs money · Lawyer = legal advice · Portal = a setting in Entra, Stripe or Azure.
- **Status:** Not started, In progress, Done or Won't do; this file is updated as each item moves.

## Priority 1: fix now

These seven are cheap and urgent: a broken project rule, an unguarded isolation claim, a risk that can take every stage down, a misleading promise to buyers, and what a demo needs.

| # | Item | Problem | Fix | Effort | Needs | Status |
|---|---|---|---|---|---|---|
| 1 | Isolation cases for two endpoints | Breaks the rule that every endpoint has an isolation case: `GET /api/v1/me/export` and `POST /internal/tenants/{id}/prepare-deletion`. | Add unsigned-request and cross-tenant cases to the isolation suite. | S | Code | Done |
| 2 | Schema-wide RLS check | RLS is tested table by table; nothing asserts that every `tenant_id` table has RLS enabled, forced and a policy, so a forgetful migration passes CI. | One test per service database after migrations, with an allow-list for customer-owned and work-queue tables. | S | Code | Done |
| 3 | Database connection budget | 4 services × 3 replicas × pool 3 × 3 stages = 108 connections, against 35 for the app roles on the B1ms (50, 15 reserved); a staging load test during prod traffic can take every stage down. | Pool 2 per replica; at most 2 replicas per database service in staging, 1 in dev and prod (32 in all); the migrator caps each database at twice its share. The alert on active connections waits (item 3 below). | S | Code | Done |
| 4 | "Yours to keep" wording | The home page ("Buy once, keep it") and program pages ("yours to keep") promise permanent access; buyers lose it when a coach deletes the workspace (#4). | Change the wording now; the real fix is item 16. | S | Code | Done |
| 5 | Payment path re-tested on every deploy | The test customer owns the purchase program, so the e2e run no longer pays: payment, webhook and enrollment go untested after the first run. | Refund through Stripe's test API at the end of the spec (staging key from Key Vault), so every run pays again. | M | Code | Done (v1.18.0: the staging run of 27 October paid, reviewed and refunded) |
| 6 | Audit trail for admin actions | Suspensions, plan changes, review decisions and payout retries record neither the admin nor a reason. | An admin-actions log with a required reason, shown in `/admin` and sent in the suspension email. | M | Code | Done |
| 7 | Content in prod | The evaluation is meant to run on prod with demo data, but prod is never seeded and has no programs. | Create showcase workspaces in prod (with Stripe test onboarding), or decide that demos run on staging. | S | You | Done |

## Priority 2: before real customers

None of these hurt while Stripe runs in test mode, and all four can be built now at no cost. Items 9 and 10 are platform features. Item 14 asks one question at checkout. Item 16 needs a decision first.

| # | Item | Problem | Fix | Effort | Needs | Status |
|---|---|---|---|---|---|---|
| 9 | Refunds Athlos can stand behind | The refund page promises a refund within 14 days, but only the coach can refund, their contact address is optional, customers cannot ask in the app, and no admin can refund. | "Request a refund" on the order within the window, an email to the coach, and an admin refund when the coach has not acted after a few days. | L | Code, You (the escalation rule) | Done |
| 10 | Reporting a program or a coach | Only reviews can be reported; the admin can only suspend a whole workspace, and gives no reason. The EU Digital Services Act asks for a report mechanism and a statement of reasons. | A report button on program pages and storefronts into the admin queue, unpublishing a single program, and the reason sent to the coach. | M | Code | Done |
| 14 | Right of withdrawal at checkout (#2) | EU law gives 14 days to withdraw from a digital purchase unless the buyer expressly accepts immediate access and losing that right; checkout does not ask. | That consent at checkout, kept with the Terms version. The lawyer review and the imprint wait (item 14 below). | S | Code | Done |
| 16 | Buyers' access after a coach leaves (#4) | Deleting a workspace erases programs that customers paid for. | Keep a read-only copy of each bought program for its buyers, or refuse deletion while customers own programs. | L | You, Code | Done |

## Priority 3: to run it in production

These turn a working thesis deployment into one you could leave running: secrets, recovery, monitoring and the remaining abuse limits, each in its free form.

| # | Item | Problem | Fix | Effort | Needs | Status |
|---|---|---|---|---|---|---|
| 19 | Signing key: rotation and one key per service (#16) | One key is shared by the gateway and every service, and services accept only that key: rotating it means downtime, and a compromised service could sign anything. | Accept the current and the previous key during a rotation; later, per-service keys or Entra workload tokens. | M | Code | In progress |
| 20 | Secret rotation runbook | The signing keys, the Stripe secrets and the Graph client secret (expires in 2028) have no written rotation steps. | A runbook in `infra/README.md` and an expiry reminder. | S | Code | Done |
| 21 | Rollbacks and migrations | A rollback redeploys an older image, but migrations only go forward, so old code can meet a newer schema. | An expand-then-contract rule (add in one release, remove in a later one), written down and checked in review. | S | Code | Done |
| 22 | Restore drill (#10) | Backups have never been restored. | A timed restore drill, recorded in the evaluation. `pg_dump` cannot do it: FORCE RLS binds the tables' owner and Azure has no superuser, so a dump reads no tenant rows. Instead `tests/evaluation/restore_drill.sh` restores the server to a point in time on a temporary server, counts each database's tables, tables holding data and migrations, and deletes it (a few cents, `docs/evaluation/restore-drill.md`). The off-server copies wait (item 22 below). | S | Code | Done (28 October 2026: restored in 430–492 s, queryable after about 10 minutes, all 12 databases with their schema and data) |
| 23 | Uptime check | Nothing checks that prod is up, and the 5xx alert only fires with traffic. | A scheduled health check from GitHub Actions a few times a day, inside the included Actions minutes (it also measures cold starts). The alerts wait (item 23 below). | S | Code | Done |
| 24 | Frontend error reporting | Page crashes are only logged to the visitor's browser console. | Send the error boundary's errors to a small gateway endpoint that writes them to Application Insights, without cookies. | S | Code | Done |
| 25 | Base-image patches and Dependabot | Images are rebuilt only when code is pushed, so OS patches wait; `dependabot.yml` has no `src/web` entry, so the shop gets security fixes only. | A monthly scheduled rebuild and release, and `src/web` added to Dependabot. | S | Code | Done |
| 26 | MFA for admins | Nothing in the repo sets up multi-factor sign-in; unless it was switched on in the portal, the admin signs in with a password alone. | Switch on email-code MFA in the Entra user flow, at least for `Platform.Admin` accounts. | S | Portal | Not started |
| 27 | Email bounces | Failed deliveries from Communication Services are not processed, so Athlos keeps writing to dead addresses. | An Event Grid subscription on email events that marks bad addresses. | M | Code | Done (v17.8: ACS system topic → `email-events` queue → worker; tenancy keeps hashes in `email_suppressions`, skipped for 180 days) |
| 28 | Rate limit across gateway replicas (#14) | Each gateway replica keeps its own buckets, so a coach's limit doubles at two replicas. | Divide the plan's limit by `maxReplicas`, or keep the buckets in PostgreSQL. Redis waits (item 28 below). | M | You | Done |
| 29 | Storefront floods (#15) | One client flooding a storefront spends that coach's budget, so their own visitors get 429s. | A per-client limit on storefront traffic. A CDN and firewall wait (item 29 below). | M | Code | Done |
| 30 | Infra what-if on pull requests | Design §11 promises an `az deployment what-if` on infra PRs; CI only lints Bicep. | Sign in with OIDC on infra PRs and post the what-if to the job summary. | S | Code | Done |

## Priority 4: tests still missing

The backend suites are strong (about 320 test methods, a third of them for isolation) and 60 end-to-end runs follow each staging deploy; the gaps are flows tested at one layer only, and kinds of test that do not exist yet.

| # | Item | Problem | Fix | Effort | Needs | Status |
|---|---|---|---|---|---|---|
| 31 | End-to-end: a coach refund | Coach refunds and the access they remove are tested in integration tests only, never through Stripe. | A spec signed in as a test coach that refunds a sale from the studio and checks the access is gone. | M | Code, You (a test coach account) | Not started |
| 32 | End-to-end: the admin | Suspending, plans and review moderation have no end-to-end coverage. | Specs signed in as a test admin on staging. | M | Code, You (a test admin account) | Not started |
| 33 | End-to-end: invites, discounts, exports, coach terms | Staff invites, a discount code at checkout, the workspace export and the coach-terms notice are covered by unit and integration tests only. | Specs on a throwaway `e2e-…` workspace, like the studio lifecycle spec. | M | Code | Not started |
| 34 | Messaging against the real broker | The outbox, the queue consumers and the emails are tested with fakes, never against Service Bus. | Integration tests on the Service Bus emulator container in CI. | M | Code | Not started |
| 35 | DbMigrator role setup | Its one test covers demo data; creating roles, refusing a role that can bypass RLS, and the grants are untested. | Tests that run the migrator on Testcontainers and check roles and grants. | S | Code | Not started |
| 36 | Accessibility of studio and account pages | Axe checks cover the public pages only. | Add the studio and account pages to the accessibility spec. | S | Code | Not started |
| 37 | Frontend unit tests | About 20 pages have none: the storefront, orders, checkout confirmation, the studio overview, team and programs, the account menu and more. | Vitest tests, pages with logic first. | M | Code | Not started |
| 38 | Security scan | No automated security testing runs against a deployed stage. | An OWASP ZAP baseline scan against staging after each deploy (free). | S | Code | Not started |
| 39 | Write load test (#22) | Reads are load- and stress-tested; checkout and webhooks are not. | A k6 scenario with many test accounts against Stripe's test mode, within its rate limits. | M | Code, You (test accounts) | Not started |
| 40 | Lighthouse in CI | Performance is measured by hand (`tests/evaluation/lighthouse.sh`). | Run it after staging deploys with a budget that fails on regressions. | S | Code | Not started |
| 41 | Old code on a new schema | No test shows that the previous release still runs after the next release's migrations (pairs with item 21). | Run the previous release's tests against the new schema in CI. | M | Code | Not started |

## Priority 5: product gaps to close

Smaller gaps in flows that already work; each takes a few hours to a few days, and none blocks the thesis.

| # | Item | Problem | Fix | Effort | Needs | Status |
|---|---|---|---|---|---|---|
| 42 | Payouts in the studio | The studio shows onboarding and a status only: no link to the coach's Stripe dashboard, no payouts or balance. | A Stripe Express login link and a payouts list read from Stripe. | M | Code | Not started |
| 43 | Email preferences | Customers cannot opt out of the "new workout" or "coach replied" emails. | Preferences in `/me/account` and an unsubscribe link with a signed token. | M | Code | Not started |
| 44 | Workout text formatting | The design calls workout content Markdown, but customers see the raw text. | Render safe Markdown (no HTML), or call it plain text in the studio. | S | Code | Not started |
| 45 | Link rules | Video links accept any address, not only YouTube or Vimeo, and logos and profile links may use plain `http://`. | HTTPS only, and an allow-list of video hosts. | S | Code | Not started |
| 46 | Onboarding reminder | Coaches who start Stripe onboarding and stop get no reminder (optional in design §5.3). | One reminder email after 3 days. | S | Code | Not started |
| 47 | Deadline for new coach terms (#2) | Workspaces that never accept the current coach terms keep selling. | After a deadline, tenancy reports them not ready to sell, which checkout already checks. | S | You (the deadline), Code | Not started |
| 48 | Discount code rules (#21) | No "once per customer" rule, and two buyers paying at the same moment can both get a code's last use. | A per-customer use check and a lock on the code's use count at checkout. | M | Code | Not started |
| 49 | Search quality (#21) | Search is a case-insensitive text match, with no ranking, no typo tolerance and no coach names. | A PostgreSQL full-text or trigram index, and coach names copied into catalog. | M | Code | Not started |
| 50 | Price-drop email for saved programs (#21) | Saving a program brings no alert when its price falls. | An email when a saved program's price drops, at most once a week. | M | Code | Not started |
| 51 | Account deletion leftovers (§5.2) | Deleted sign-ins stay in Entra's recycle bin for 30 days, and invites sent to the address stay until they expire. | Purge the Entra user at once (one Graph call) and remove open invites to the address. | S | Code | Not started |

## Priority 6: later and future work

Known trade-offs and bigger features: worth naming in the thesis as future work, not needed to finish it.

| # | Item | Problem | Fix | Effort | Needs | Status |
|---|---|---|---|---|---|---|
| 52 | Program versioning (#5) | Editing a program changes it for everyone who bought it; there are no versions. | Versioned program content, with buyers keeping the version they bought or choosing to update. | L | You, Code | Not started |
| 53 | Disputes and manual refund cases (#7, #8) | A lost dispute costs the platform, not the coach, and three rare refund cases are settled by hand. | Evidence submission, recovering the coach's share, and automating the manual cases. | L | You, Code | Not started |
| 54 | Workspace deletion as one workflow (#18, #19) | Deletion is several calls with no distributed transaction, and the admin cannot delete a workspace. | A durable deletion job that resumes after a crash, and an admin route that runs it. | M | Code | Not started |
| 55 | Asynchronous data export (#3) | "Download my data" and the workspace export run in one request; a cold start can make the first try time out. | A queued export that writes the file to Blob Storage and emails a short-lived link. | M | Code | Not started |
| 56 | Faster first paint on phones (#23) | First paint is 3.6 s on Lighthouse's slow phone (good is under 1.8 s), and covers from before v1.16.0 have no card size. | A static shell or server-side rendering for public pages, and a one-off job that resizes the old covers. | L | Code | Not started |
| 57 | Greek language | The shop is in English only. | Translations with a language switch, Greek first. | L | You | Not started |
| 58 | Public repository | The repo is private: no free CodeQL, and no required reviewers on prod. | Make it public when the thesis allows, then turn on CodeQL and required reviewers. | S | You | Not started |

## After the thesis is published: items that add cost

These wait because each one adds cost, either a monthly bill or a fee on every sale. The costs are approximate, so check current prices before starting one. The rows marked "rest of" are the paid parts of items whose free part is ranked above.

| # | Item | Problem | Fix | Cost | Effort | Needs | Status |
|---|---|---|---|---|---|---|---|
| 3 | Connection alert (rest of 3) | Nothing warns when connections near the B1ms limit. | An Azure Monitor alert on active connections. | ≈ €0.10 a month per metric alert | S | Code | Not started |
| 8 | Cold start before a demo (#13) | The median is 87 s from zero: the first visitor after a quiet spell waits or times out. | `gatewayMinReplicas: 1`, plus catalog and tenancy, in prod for demo weeks only. Until then, open the site a few minutes before a demo. | ≈ €5–10 a month per app kept warm | S | Budget | Not started |
| 11 | Stripe live mode (#6) | Every stage runs in Stripe's test mode. | Activate the Stripe account and its Connect profile, put live keys and webhook secrets in prod's Key Vault (`infra/stripe_setup.py`), check `livemode` in the webhooks, and test one real purchase and refund. | Stripe's fees on every sale: ≈ 1.5% + €0.25 per EU card, ≈ €2 per active coach a month and a fee per payout | M | Portal, You | Not started |
| 12 | Fee model against Stripe's fees | On a €10 sale the 10% fee (€1) is about what Stripe takes. | Decide the fee, the minimum price, or passing the fees on to coaches. | None itself; it decides who pays item 11's fees | S | You | Not started |
| 13 | VAT and invoices (#1) | No VAT on sales or on the platform fee, and receipts are not tax invoices. | Stripe Tax on Checkout, the tax amount kept per order, invoices for customers and for coaches' fees. | Stripe Tax ≈ 0.5% per sale, an accountant | L | Lawyer, Code | Not started |
| 14 | Legal review and imprint (rest of 14, #2) | The legal pages are drafts with no imprint, and Athlos's refund depends on the coach. | Lawyer-reviewed texts and an imprint naming the operator. | A lawyer's fee; the operator must be a registered business or sole trader | M | Lawyer | Not started |
| 15 | Data retention periods | Orders are kept forever and logs for 30 days; the Privacy page states no retention. | Decide the periods against the tax rules, state them, and add a cleanup job. | A lawyer's fee | M | Lawyer, Code | Not started |
| 17 | Billing for the Pro plan (#9) | Pro is assigned by the admin and costs nothing. | A Stripe Billing subscription for Pro, whose webhook sets the plan. | Stripe Billing ≈ 0.7% of what it bills | L | You (the price), Code | Not started |
| 18 | Own domain and email sender (#20) | The shop runs on an azurecontainerapps.io address and emails come from a Microsoft-managed domain, which costs trust and lands in spam filters. | Buy a domain, add it to the gateway (free managed certificate) and to Communication Services with SPF and DKIM. | ≈ €10–15 a year | M | Budget | Not started |
| 22 | Off-server backup copies (rest of 22, #10) | Backups are kept 7 days in one region; Blob Storage has no soft delete. | A nightly `pg_dump` job to Blob Storage, and soft delete on blobs. | Cents a month | M | Code | Not started |
| 23 | Alerts (rest of 23) | Dev and staging have no alerts, and telemetry over the 0.15 GB daily cap is dropped without notice. | An alert when the log cap is hit, and a dead-letter alert on staging. | ≈ €0.10–1.50 a month per alert rule | S | Code | Not started |
| 28 | Redis for rate limits (rest of 28, #14) | Item 28's free fix may not be enough with many replicas. | A shared Redis cache for the rate-limit buckets. | ≈ €13+ a month | M | Budget | Not started |
| 29 | CDN and firewall (rest of 29, #17) | Nothing filters traffic before the gateway. | Azure Front Door in front of the gateway. | ≈ €30+ a month | M | Budget | Not started |

**Decisions for then**

| Decision | Cost | My recommendation | Items |
|---|---|---|---|
| Platform fee against Stripe's fees | Stripe's fees, above | Decide before live mode | 11, 12 |
| The Pro plan's price | Stripe Billing, above | Your call | 17 |
| Lawyer review: terms, withdrawal, VAT, retention | A lawyer's fee | Before live mode | 13, 14, 15 |
| Telemetry above 5 GB a month | €2.57/GB | Raise the cap only with real traffic | 23 |
| MFA by text message | Per message; email codes are free | Email codes | 26 |

## Decisions only you can make

Spending so far is about €3–4 a month against the €100 credit (from the October cost check), and the ranked items keep it there. The Items column names what each decision unlocks.

| Decision | My recommendation | Items |
|---|---|---|
| What runs after the free year ends (about late September 2027): PostgreSQL ≈ €15/month, Container Registry Standard ≈ €17/month | Set an end date, or move images to GitHub's registry and keep one database; this one cannot wait if the thesis runs past September 2027 | all |
| How long a coach has before the admin refunds | Decided 26 October 2026: 3 working days | 9 |
| Buyers' access after a coach leaves | Decided 26 October 2026: a read-only copy for buyers | 4, 16 |
| Demo on prod or on staging | Decided 26 October 2026: staging, which has the demo data | 7 |
| Which free rate limit across replicas | Decided 26 October 2026: the plan's limit divided by `maxReplicas` | 28 |
| Test accounts (a coach, an admin, several customers) | Create them in the External ID tenant | 31, 32, 39 |
| Deadline for accepting new coach terms | 30 days after a change | 47 |
| Greek language | After deciding on a launch | 57 |
| Public repository | After the thesis is graded | 58 |

## Outside the code

The thesis write-up remains, built from the evidence in `docs/evaluation/` (targets, noisy neighbor, stress, Lighthouse, limitations). When we start on an item here, its finding also goes into `limitations.md`, so the two files stay in step.
