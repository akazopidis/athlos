# Stress test (v1.12.0 testing round)

Measured 20 October 2026 on staging (v1.11.0, the read path is unchanged in v1.12.0) with [`tests/load/stress.js`](../../tests/load/stress.js). Raw data: [`tests/load/results/stress-v1.11.0.json`](../../tests/load/results/stress-v1.11.0.json).

## What was done

One coach's storefront traffic (the Pro demo coach, `demo-forge`): 40% its program list, 40% its program pages, 20% its storefront header, all public reads. After a warm-up that waits until every app on the path answers in under a second, the rate was raised in steps of 60 seconds (25, 50, 100, 150 and 200 requests per second, 10-second ramps between them) from one client machine. The coach's plan allows 100 req/s at the gateway (design §4).

## Results

| Step | Requests in the step | p50 | p95 | p99 | Timed out (30 s) | 429 | 5xx |
|---|---|---|---|---|---|---|---|
| 25 req/s | 1,501 | 66 ms | 200 ms | 239 ms | 0% | 0 | 0 |
| 50 req/s | 3,000 | 63 ms | 82 ms | 200 ms | 0% | 0 | 0 |
| 100 req/s | 5,133 (86/s reached) | 1,368 ms | 11,138 ms | 16,460 ms | 0% | 0 | 0 |
| 150 req/s | 8,403 (140/s reached) | 65 ms | 14,815 ms | 15,777 ms | 9.2% | 0 | 0 |
| 200 req/s | 12,101 | 63 ms | 1,071 ms | 1,325 ms | 0% | 0 | 0 |

Replicas and CPU during the run (Azure Monitor, maximum per minute):

| App | Replicas | CPU |
|---|---|---|
| gateway (0.5 vCPU) | 1, then 2 | at most 47% |
| catalog-api (0.25 vCPU) | 1, then 3 (its maximum) | 91% to 120% while on one replica |
| tenancy-api | 1 | at most 50% |

## What it shows

- **Below about 85 req/s on warm minimum replicas, the shop answers well within the 300 ms target** (p95 82 to 200 ms).
- **The first bottleneck is catalog-api, not the gateway.** At about 85 to 100 req/s its single 0.25 vCPU replica runs out of CPU on ordinary per-request work (the queries, EF Core and JSON for the program list and program pages), requests queue, and p95 rises to 11 to 15 seconds. Signing the cover links is not the cost: the delegation key is cached for a day and each link is one HMAC.
- **The platform recovers by scaling out, without errors.** Container Apps' HTTP rule adds catalog replicas within about two minutes; meanwhile 9% of the requests in the 150 req/s step waited longer than the client's 30-second timeout. After scale-out, 200 req/s are served with p50 63 ms and p95 1.1 s. No request was answered with a 5xx.
- **The rate limit did not trip.** The coach's 100 req/s bucket lives in each gateway replica's memory, and the gateway had scaled to two replicas, so the coach's effective limit was 200 req/s (`limitations.md` 14). The limit therefore protects other coaches only up to the replica count; the services themselves scaled to absorb it.
- **The client was a limit too:** at 100 and 150 req/s, k6 reached its 600 virtual users while responses were slow, so fewer requests were sent than asked (86 and 140 per second).

## What would raise the knee

- A minimum of two catalog-api replicas in prod (cost: one more 0.25 vCPU replica kept warm, roughly €10 a month at Consumption prices after the free grant, which the €100 budget for the year cannot carry), or a scaling rule on CPU as well as concurrent requests.
- Caching the storefront's program list and program pages in catalog-api for a few seconds (they change rarely), which would remove most of the per-request database and serialization work at this load. (Cover links are now the same within a 10-minute window, v1.13.0, which lets browsers cache the images, but this saves bandwidth, not catalog CPU.)
- A shared rate-limit store (Redis), so the per-coach limit does not grow with gateway replicas (`limitations.md` 14).
