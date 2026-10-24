#!/usr/bin/env bash
# Lighthouse on five public shop pages, mobile (simulated slow 4G, 4x CPU slowdown) and desktop.
#   tests/evaluation/lighthouse.sh <gateway base URL> <output dir> [runs]
# Needs Node and Chrome. Writes one JSON and one HTML report per page, form factor and run, then prints
# a CSV summary with the median of the runs (tests/evaluation/lighthouse_summary.py): single runs vary,
# layout shift most of all.
set -euo pipefail
BASE=${1:?gateway base URL}
OUT=${2:?output directory}
RUNS=${3:-3}
mkdir -p "$OUT"

# Wake the gateway and catalog-api first: a cold start (design §15.3) is measured on its own.
curl -s -m 200 -o /dev/null "$BASE/api/v1/marketplace/programs?coach=demo-forge"

PAGES="home:/ storefront:/c/demo-forge program:/c/demo-forge/programs/minimalist coaches:/coaches sell:/sell"
for page in $PAGES; do
  name=${page%%:*}
  path=${page#*:}
  for form in mobile desktop; do
    preset=()
    [ "$form" = desktop ] && preset=(--preset=desktop)
    for run in $(seq 1 "$RUNS"); do
      report="$OUT/$name-$form-$run"
      # Reports already there are kept, so an interrupted round resumes; Chrome occasionally fails
      # to start, so a run is tried up to three times.
      for attempt in 1 2 3; do
        [ -f "$report.report.json" ] && break
        npx -y lighthouse@12.8.2 "$BASE$path" "${preset[@]}" --chrome-flags="--headless=new" \
          --output=json --output=html --output-path="$report" --quiet || echo "run $report failed (attempt $attempt)" >&2
      done
    done
  done
done

python -I "$(dirname "$0")/lighthouse_summary.py" "$OUT"
