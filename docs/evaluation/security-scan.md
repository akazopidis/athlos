# Security scan (backlog item 38)

An OWASP ZAP baseline scan of staging, run after every staging deploy (`zap` job in [`deploy-staging.yml`](../../.github/workflows/deploy-staging.yml), report as the `zap-report` artifact). ZAP spiders the shop for a minute and checks every response passively: headers, cookies, caching, information leaks. It sends no attacks, so it is safe on a shared stage. It reports and does not block a release. Rules that do not apply are listed with their reason in [`.zap/rules.tsv`](../../.zap/rules.tsv).

## First run: 28 October 2026, staging at v1.18.0

Run by hand with the same image and options (`ghcr.io/zaproxy/zaproxy:stable`, `zap-baseline.py -a`). Raw report: [`tests/evaluation/results/zap-baseline-2026-10-28-staging.json`](../../tests/evaluation/results/zap-baseline-2026-10-28-staging.json).

**0 failures, 8 warnings, 63 rules passed.** Signed-out pages only: the spider has no account.

| Rule | Risk | Finding | What was done |
|---|---|---|---|
| 10055 CSP: Wildcard Directive | Medium | `img-src ... https:` allowed images from any host; `*.ciamlogin.com` any External ID tenant | The policy now names the stage's own hosts: the blob endpoint that serves covers and logos (`Spa:ImageOrigin`), and the sign-in authority's host (`Spa:EntraAuthority`). The wildcards remain only as the fallback when they are not configured (locally). |
| 10055 CSP: style-src unsafe-inline | Medium | Inline styles are allowed | Kept: Radix (shadcn/ui) positions popovers and menus with `style` attributes. Scripts stay `'self'` only, which is what an injection would need. |
| 10063 Permissions Policy Header Not Set | Low | | Added: camera, microphone, location, payment and USB are off (Stripe Checkout is its own page). |
| 90004 Cross-Origin-Resource-Policy missing | Low | | Added `same-origin`: other sites may link to Athlos but not load its files into their pages. |
| 10094 Base64 Disclosure | Informational | Matches in asset names | Not data: Vite's content hashes in file names. Ignored in the rules file. |
| 90005 Sec-Fetch-* missing | Informational | | Request headers browsers send; ZAP's client does not. Ignored in the rules file. |
| 10109 Modern Web Application | Informational | | ZAP noting a single-page app; no action. |
| 10015, 10049 Cache directives | Informational | `no-cache` shell, year-long `immutable` assets | Intended (design §8.1, v1.14.0). |

Already present before the scan and passed: HSTS, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `frame-ancestors 'none'`, `Referrer-Policy`, `base-uri` and `form-action` limited to the shop.

## What it does not cover

- Signed-in pages and the APIs behind them. Tenant isolation, the API's real attack surface, is tested by the isolation suite (`tests/Athlos.IsolationTests`, 270 cases), not by a scanner.
- Active attacks (ZAP's full scan): it would hammer a stage that dev, staging and prod share a database server with (design §7).
