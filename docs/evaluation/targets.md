# Evaluation targets (design §15.3)

Measured 8 October 2026, at v0.7.0; isolation and deploy times refreshed 11 October 2026, at v1.2.0. Each number links to its raw data or the script that reproduces it.

| Target | Value | Measured | Met? |
|---|---|---|---|
| Cross-tenant data leaks in the isolation suite | 0 | 0 in 153 isolation tests (134 at v0.7.0) | Yes |
| Gateway p95 latency, warm, normal load | < 300 ms | 59–109 ms (tenant B's baseline, 6 runs) | Yes |
| Tenant B p95 during tenant A's flood, limits on | within 20% of baseline | −6% at 0.5 vCPU (60 vs 64 ms); +71% at 0.25 vCPU (142 vs 83 ms) | Yes, after raising the gateway to 0.5 vCPU |
| Time from `git push` on `dev` to running in dev | < 15 min | median 14.3 min, p90 18.2 min (55 runs) | Mostly: 44 of 55 runs; the slowest step is now skipped when `infra/` is unchanged (below) |
| Time from release tag to running in prod | < 10 min, no rebuild | median 8.1 min, p90 11.0 min (10 runs), images reused | Mostly: 7 of 10 runs |
| Monthly Azure cost | < €5 | €0.38 in September (from the 27th), €0.82 for 1–8 October, about €3.2 projected for October | Yes |
| Cold start from zero replicas | measured and reported | median 87 s (70–97 s, 4 samples); warm 0.2–0.3 s | Reported |

## Tenant isolation

The isolation suite (`tests/Athlos.IsolationTests`, run on every PR, where a failure blocks the merge) covers design §3.3's three proofs:
- **Every endpoint, called as tenant B against tenant A's resources, answers 404.** This covers studio routes in every service. It also covers the internal and gateway-only routes: called without the gateway signature, every one of them answers 401.
- **Layer 4 alone.**
  - *Reads:* connected as each service's own database role with `app.tenant_id` set to B, raw SQL sees none of A's rows.
  - *Writes:* it cannot insert rows for A, change them, or delete them.
- **Layer 3 off, layer 4 still blocks.**
  - *Reads:* with the EF query filter turned off (`IgnoreQueryFilters`), RLS still hides A's rows (tenancy, catalog).
  - *Writes:* bulk update and delete skip both the filter and the SaveChanges guard, and a raw `INSERT` names tenant A outright. B still changes 0 of A's rows, and the insert fails with `insufficient_privilege` (`With_layer_3_off_RLS_still_blocks_Bs_writes_to_A`).
- **Workspace deletion (§3.4).** Erasing tenant A leaves 0 of A's rows and blobs and every one of B's (catalog, enrollment), counted as the superuser.
- **Suspension and plans (§3.4, v1.2.0).** B's owner cannot suspend A or change A's plan (403, A unchanged). The platform admin's suspension of A leaves B active and hides only A's programs, and the public-read RLS policy alone hides them. A's buyers keep the content.

## Noisy neighbor

See [noisy-neighbor.md](noisy-neighbor.md):
- **Without limits,** one tenant's 300 req/s flood took another tenant's p95 from about 64 ms to 9–12 s, with 18–41% errors, because catalog-api's connection pool ran out.
- **With limits,** the other tenant had no errors.
- **The p95 target** is met once the gateway has 0.5 vCPU: at 0.25 vCPU, refusing the flood used the gateway's whole CPU. The gateway now runs at 0.5 vCPU.

## Deploy times

[`tests/evaluation/deploy_times.py`](../../tests/evaluation/deploy_times.py) reads the GitHub Actions history, from trigger to the end of the deploy (the new revision running):

| Environment | Runs | Median | p90 | Min | Max |
|---|---|---|---|---|---|
| dev (push to running) | 55 | 14.3 min | 18.2 min | 4.3 min | 42.7 min |
| staging (merge to running) | 12 | 8.0 min | 10.9 min | 3.0 min | 13.6 min |
| prod (tag to running) | 10 | 8.1 min | 11.0 min | 3.8 min | 12.7 min |

- **dev:** builds all six images every time, then deploys. The 42.7 min maximum is one deploy that failed twice and was re-run, because three merges in a row overlapped on the shared PostgreSQL server's update.
- **staging and prod:** reuse dev's images ("build once, promote") and only deploy.
- **The slowest step in every deploy** is the shared-infrastructure step (Bicep on the shared resource group, which re-applies the PostgreSQL server). It is a no-op except after an infra change, but Azure still takes minutes to confirm it. Since 11 October 2026, `deploy-dev` skips it when the push changed nothing under `infra/` (a manual run always applies it); the runs above are from before that change.

## Cost

Actual spend from Cost Management, raw data in [`cost-2026-10-08.json`](cost-2026-10-08.json):

| Month | Actual | Main item |
|---|---|---|
| September 2026 (from the 27th) | €0.38 | Container Apps environment's virtual network (€0.35) |
| October 2026, 1–8 | €0.82 | the same (€0.79) |

- **The free parts:** PostgreSQL B1ms, the container registry and Log Analytics show €0, because they are within the Azure for Students free services (§10).
- **Container Apps:** cost €0.02, inside the free monthly vCPU-second and request grants, because everything scales to zero.
- **What does cost:** about 95% is the virtual network of the Container Apps environment, most likely its public IPv4 address, which bills whether or not anything runs.
- **Projection:** at the October rate, about €3.2 a month, under the €5 target and well within €100 for 12 months.

## Cold start

[`tests/evaluation/cold_start.sh`](../../tests/evaluation/cold_start.sh), on dev:
- **Method:** wait until the gateway and catalog-api have both scaled to zero (5 minutes without traffic), then time one storefront program list (`GET /api/v1/marketplace/programs?coach=demo-forge`), then a second one right after.

| Sample | First request | Next request |
|---|---|---|
| 1 | 84.0 s | 1.01 s |
| 2 | 89.7 s | 0.19 s |
| 3 | 70.1 s | 0.28 s |
| 4 | 96.7 s | 0.26 s |

- **Why so long:** the first request after idle waits for up to three apps to start in turn:
  1. the gateway;
  2. tenancy-api, for the rate limiter's plan lookup;
  3. catalog-api and its first database connection (Entra token, TLS).
  - Each takes 20–40 s on the Consumption plan, from image pull to the readiness probe.
- **Where this showed up in testing:**
  - the 50 s first page of "My programs";
  - the 26 s workspace deletion while enrollment-api started;
  - the 13 failed requests when the plan lookup timed out (fixed by failing open).
- **Not optimized, by design (§15.3).** The options, each with a cost:
  - `minReplicas: 1` on the gateway in prod, which bills it around the clock, roughly €10–15 a month at 0.5 vCPU;
  - a scheduled warm-up;
  - starting tenancy-api and catalog-api together when the gateway wakes.
