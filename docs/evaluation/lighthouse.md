# Frontend performance (Lighthouse)

Measured 24 October 2026 on dev: before the change at v1.13.0, and after it at `sha-36e5b47` (PR #172, released in v1.14.0). The script is [`tests/evaluation/lighthouse.sh`](../../tests/evaluation/lighthouse.sh) (Lighthouse 12.8.2, headless Chrome 155). Raw data: [`lighthouse-dev-before.csv`](../../tests/evaluation/results/lighthouse-dev-before.csv) and [`lighthouse-dev-after.csv`](../../tests/evaluation/results/lighthouse-dev-after.csv). Returning visitors were measured with [`tests/evaluation/repeat_visit.mjs`](../../tests/evaluation/repeat_visit.mjs) ([before](../../tests/evaluation/results/repeat-visit-v1.13.0-staging.csv), [after](../../tests/evaluation/results/repeat-visit-after-dev.csv)).

## What was done

Five public pages a visitor lands on: the home page, the coaches page, the "Sell on Athlos" page, a storefront and a program page (`demo-forge`). Each page was audited three times as a phone and three times as a desktop, and every number below is the median of the three runs. Lighthouse's phone profile simulates a slow 4G connection (150 ms round trip, 1.6 Mbit/s) and a CPU four times slower than the test machine. Its desktop profile simulates a fast cable connection. Each run starts with an empty cache. The apps were woken first, so a cold start (`targets.md`, median 87 s) is not part of the numbers.

## Results

Accessibility, best practices and SEO scored **100 on every page in both runs**. Performance:

| Page | Desktop before | Desktop after | Phone before | Phone after |
|---|---|---|---|---|
| Home | 96 | 97 | 61 | 62 |
| Coaches | 79 | 97 | 62 | 63 |
| Sell on Athlos | 79 | 96 | 66 | 67 |
| Storefront | 89 | 96 | 65 | 67 |
| Program page | 72 | 97 | 66 | 67 |

| Metric (median of the five pages) | Desktop before | Desktop after | Phone before | Phone after |
|---|---|---|---|---|
| First contentful paint | 1.06 s | 0.98 s | 5.38 s | 4.64 s |
| Largest contentful paint | 1.10 s | 1.10 s | 5.54 s | 5.53 s |
| Cumulative layout shift (worst page) | 0.754 | 0 | 0.434 (single run on staging) | 0 |
| Total blocking time (worst page) | 0 ms | 0 ms | 42 ms | 56 ms |

Returning visitor, home page, five samples each, unthrottled from the test machine:

| | Scripts, styles and fonts | From the browser's cache | Revalidated with the server (304) | Second visit loaded |
|---|---|---|---|---|
| Before (v1.13.0, staging) | 51 | 2 | 49 | 150–164 ms |
| After (dev) | 51 | 51 | 0 | 110–116 ms |

## What it shows

- **The page moved as it loaded, and now does not.** On desktop, four of the five pages had a layout shift from 0.16 to 0.75, where "good" is below 0.1. Two things caused it:
  - *The fonts.* The CSS requests Archivo and Geist only when text first renders, and the text then jumps when the fonts swap in. `index.html` now preloads both Latin font files, so they download alongside the JavaScript.
  - *The footer.* It sat in the first screen while data loaded, and was pushed down when the data arrived. The main area is now at least one screen tall.

  Both fixes together bring the shift to 0 on every page. This is most of the desktop score gain (72–89 → 96–97).
- **Returning visitors no longer ask about 49 files.** The gateway served Vite's hashed files `no-cache` (`MapStaticAssets` only recognises its own fingerprints), so each visit sent a revalidation for every script, style and font. They are now `immutable` for a year. The saving here was 40 ms on a short round trip; on a phone it is about one round trip per batch of requests.
- **On a slow phone the shop is still slow to show its first content: 4.6 s** (Lighthouse calls under 1.8 s "good"). The page renders only after about 220 kB of compressed JavaScript, `/config.json` and MSAL's start-up. Preloading `/config.json` and the fonts took 0.7 s off the first paint. Largest paint did not change: it is the first heading on most pages, and on the home and coaches pages Lighthouse's model also puts the cover and logo images before it.
- **Images are the biggest download.** The home page on dev transfers 2.1 MB, of which about 1.3 MB is three PNG covers (830 kB together) and a 440 kB PNG logo, all shown at card size. The studio now shrinks covers (1,600 px) and logos (512 px) and uploads WebP, so new uploads are a fraction of that. Images uploaded before stay as they are until the coach replaces them.

## Caveats

- **The phone numbers are simulated.** Lighthouse computes them from a model of the page's requests, not from a phone. Unthrottled headless Chrome paints the home page's first content at about 0.6 s.
- **Headless Chrome's mobile emulation sometimes delays the first frame by one to two seconds** (the first `requestAnimationFrame` fired at 1.3–1.5 s, although React had rendered at 0.48 s). The same layout in a narrow desktop window always painted at 0.6 s. This makes single runs vary, which is why the script takes the median of three. The single staging run before the change showed a phone layout shift of 0.43 on three pages that the dev medians did not.
- **Data differs by environment.** Dev has more programs and images than staging, so the home page's bytes and image numbers are dev's.

## Round 2: MSAL loaded on demand (v1.15.0)

The first item below, measured the same way on dev at `sha-fa004cb` (PR #176), the same day. MSAL now loads only for Entra's redirect back, a tab that signed in, or a click on Sign in, so a signed-out visitor's first load is 161 kB compressed instead of 223 kB. Raw data: [`lighthouse-dev-lazy-msal.csv`](../../tests/evaluation/results/lighthouse-dev-lazy-msal.csv).

| Page | Desktop (v1.14.0 → round 2) | Phone (v1.14.0 → round 2) | Phone first paint | Phone largest paint |
|---|---|---|---|---|
| Home | 97 → 98 | 62 → 65 | 4.64 → 3.60 s | 9.22 → 7.33 s |
| Coaches | 97 → 98 | 63 → 67 | 4.64 → 3.59 s | 8.17 → 6.76 s |
| Sell on Athlos | 96 → 98 | 67 → 77 | 4.71 → 3.51 s | 5.39 → 4.04 s |
| Storefront | 96 → 99 | 67 → 75 | 4.79 → 3.59 s | 5.50 → 4.31 s |
| Program page | 97 → 99 | 67 → 75 | 4.63 → 3.60 s | 5.53 → 4.35 s |

The phone's first paint is now about 3.6 s on every page (1 s sooner), and the largest paint 4.0 to 4.4 s where it is the first heading. The home and coaches pages stay slower because Lighthouse's model counts their images before the heading (largest paint 6.8 to 7.3 s). Each page transfers about 245 kB less, MSAL's chunk included.

## What would improve the phone numbers

- ~~**Load MSAL only when it is needed.**~~ Done in v1.15.0 (round 2 above).
- **Server-side rendering or a static shell** for the public pages, so the heading paints before the JavaScript runs. This is a larger change to how the gateway serves the SPA (§8.1).
- **Resize existing covers once**, or serve sized variants, so cards do not load a 1,600 px image (`srcset`).

## In CI: a budget after every staging deploy (backlog item 40)

Since v17.8 the `lighthouse` job in `deploy-staging.yml` runs `tests/evaluation/lighthouse.sh` (the five pages, phone and desktop, median of three runs) after each staging deploy and checks the medians with `tests/evaluation/lighthouse_budget.py`. A result over budget fails the job; the reports are the `lighthouse-report` artifact and the table is in the run's summary.

| Budget | Desktop | Phone |
|---|---|---|
| Performance | at least 85 | at least 50 |
| Accessibility, best practices, SEO | at least 95, 90, 90 | at least 95, 90, 90 |
| Largest contentful paint | at most 2.5 s | at most 12 s |
| Total blocking time | at most 300 ms | at most 600 ms |
| Layout shift | at most 0.1 | at most 0.1 |

The limits sit below the measured numbers, with room for a GitHub runner's slower and noisier CPU: they catch a regression (a heavier bundle, a layout shift, a blocking script), not a slow run. A check of the script on staging at v1.18.0 (28 October 2026, one run per page from this machine, [`tests/evaluation/results/lighthouse-staging-v1.18.0.csv`](../../tests/evaluation/results/lighthouse-staging-v1.18.0.csv)): desktop 98–99, phone 64–77, accessibility, best practices and SEO 100 everywhere, layout shift 0, all within budget. The phone home page is the closest, at 64, with a largest paint of 8.1 s: its eight covers.
