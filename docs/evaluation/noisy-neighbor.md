# Noisy-neighbor experiment: first run

Run on 5 October 2026 against **staging** (`sha-e2f52d2`), as design §4 describes, with the demo coaches from §15.2. Raw results: [`tests/load/results/`](../../tests/load/results/); script: [`tests/load/noisy-neighbor.js`](../../tests/load/noisy-neighbor.js).

## Setup

- **Tenant B**: `demo-forge` (Pro plan, 100 req/s). A steady 10 req/s to its storefront's program list (`GET /api/v1/marketplace/programs?coach=demo-forge`): gateway, then catalog-api, then the shared PostgreSQL B1ms server.
- **Tenant A**: `demo-stride` (Free plan, 20 req/s). The same request for its own storefront at 300 req/s, in the two flood runs.
- **Runs**: 2 minutes each, from one machine in Greece to `italynorth`, after a warm-up that waits until both coaches answer in under a second, so cold starts are not measured.
  - *Limits off*: `RateLimits__Enabled=false` set on the staging gateway for that run only, then removed.
- **Fixed**: catalog-api up to 3 replicas with `Maximum Pool Size=3` each; gateway up to 2 replicas.

## Results

| Run | B p50 | B p95 | B p99 | B errors | A requests | A refused (429) | A admitted |
|---|---|---|---|---|---|---|---|
| Baseline (B only) | 59 ms | 133 ms | 146 ms | 0% | n/a | n/a | n/a |
| A floods, limits **on** | 58 ms | 193 ms (+45%) | 342 ms | 0% | 36,001 (300/s) | 90.1% | 29.8 req/s |
| A floods, limits **off** | 1,242 ms | 11,209 ms (84×) | 12,654 ms | 27.5% | 24,692 (206/s) | 0% | 205.8 req/s |

## What it shows

1. **Without limits, one tenant's spike takes down another.**
   - B's median went from 59 ms to 1.2 s, its p95 to 11 s, and 27.5% of its requests failed.
   - catalog-api's logs show why: 124 `connection pool has been exhausted (Max Pool Size 3)` timeouts. A's requests held every connection the service had, and B's requests waited behind them.
   - A did not even get its 300 req/s: k6 reached only 206 req/s as answers slowed. The flood hurt its own sender too.
2. **With limits, B is protected.**
   - No errors for B, and the median is unchanged (58 ms).
   - The gateway refused 90% of A's requests with 429 before they reached catalog-api or the database.
3. **The p95 target (within 20% of baseline) was missed: +45%.** Two causes, both expected from the design:
   - *The gateway's own work.* It still receives all 300 req/s, looks up the plan (cached) and writes 270 rejections per second, on the same small replicas that forward B's requests.
   - *More of A gets through than its plan allows.* It got 29.8 req/s instead of 20, because the gateway scaled to 2 replicas and each keeps its own bucket (the in-memory limiter limitation in §4). Those extra requests reach catalog-api and the database.
4. **The cost of refusing is small.** B's p95 stayed under 200 ms against 11 s without limits, a 58× difference for the same flood.

## Caveats and next steps

- One run per configuration, from one client machine. Repeat each run at least three times and report the spread before the evaluation chapter (phase 9).
- Application Insights samples requests (about 2,100 of more than 36,000 gateway requests were kept), so latency percentiles come from k6, and App Insights is used for errors and the `athlos.ratelimit.rejected` metric. The tenant-aware workbook and graphs are phase 8.
- **Found during the run:**
  - When the gateway restarted with a new revision, its plan lookups to tenancy-api timed out while tenancy started from zero replicas, and 13 requests failed with 500.
  - The limiter now fails open: a failed plan lookup falls back to the per-client limit and the request goes on.
- **Ways to close the gap to +20%, to try in phase 9:**
  - a single gateway replica during the experiment (exact per-tenant limits);
  - a short cache of "over limit" per tenant, so the gateway skips the plan lookup for rejected floods;
  - more gateway CPU.

# Round 2: repeated runs and gateway CPU

Run on 8 October 2026 against staging (`sha-afd7b72`, v0.7.0's images) with [`tests/evaluation/noisy_neighbor_round.sh`](../../tests/evaluation/noisy_neighbor_round.sh).

- **Setup:** the same as the first run, with every condition repeated 3 times.
- **Second gateway size:** a second configuration ran the gateway at 0.5 vCPU / 1 GiB instead of 0.25 vCPU / 0.5 GiB. This tests the first run's suspicion that the gateway's own CPU, refusing 270 req/s, is what slowed tenant B.
- **Restoring staging:** the script restores the staging gateway afterwards (0.25 vCPU, limits on); this was checked.
- **Raw results:** [`tests/load/results/round2/`](../../tests/load/results/round2/).

| Condition (3 runs each) | B p50 (median) | B p95 (median, min–max) | B p99 (median) | B errors (mean) | A refused (mean) |
|---|---|---|---|---|---|
| Baseline, gateway 0.25 vCPU | 56 ms | 83 ms (64–109) | 115 ms | 0% | n/a |
| A floods, limits **on**, 0.25 vCPU | 55 ms | 142 ms (62–148) | 320 ms | 0% | 87.6% |
| A floods, limits **off**, 0.25 vCPU | 1,864 ms | 10,691 ms (8,830–11,580) | 11,210 ms | 26.0% | 0.6% |
| Baseline, gateway 0.5 vCPU | 57 ms | 64 ms (59–65) | 69 ms | 0% | n/a |
| A floods, limits **on**, 0.5 vCPU | 54 ms | 60 ms (60–62) | 82 ms | 0% | 86.5% |

## What round 2 shows

1. **The first run's result repeats.**
   - Without limits, B's p95 reached 9–12 s and 18–41% of its requests failed; catalog-api's connection pool runs out (first run).
   - With limits, B had no errors in any run.
   - A could only send 214 req/s on average without limits, because its own answers slowed down.
2. **The gap to the 20% target was the gateway's CPU.**
   - *At 0.25 vCPU:* with limits on, B's p95 rose to 142 ms (+71% over its baseline) and varied widely between runs (62–148 ms). Even the baseline was noisy (64–109 ms), because one quarter of a vCPU leaves little headroom.
   - *At 0.5 vCPU:* B's p95 under a 300 req/s flood was 60 ms, the same as its own baseline (64 ms, −6%), in all three runs, well within the 20% target. Refusing a flood costs the gateway CPU; with enough of it, the refusals no longer reach anyone else.
   - *Change made:* the gateway now runs at 0.5 vCPU in every environment (`infra/env.bicep`). It still scales to zero, and the Consumption plan bills vCPU-seconds only while a replica runs, so the cost stays within the free monthly grant at this traffic (cost report in [targets.md](targets.md)).
3. **More of A gets through than its plan allows.** About 13% of A's 300 req/s, roughly 40 req/s, got through, against Free's 20 req/s. With 2 gateway replicas, each keeps its own token bucket (§4 known limitation). Pinning the gateway to 1 replica would make the limit exact but halve the capacity that absorbs the flood; at 0.5 vCPU the extra 20 req/s did not hurt B, so the limitation stays documented, not changed.

**Notes on the runs:**
- In the first limits-off run, 1.9% of A's requests still got 429s: the gateway was switching revisions as the run started.
- k6 reported "Insufficient VUs" in the limits-off runs: with answers taking seconds, 1,000 virtual users were not enough to keep 300 req/s going. That is why A's achieved rate is reported.
