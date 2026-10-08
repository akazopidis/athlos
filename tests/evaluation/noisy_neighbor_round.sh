#!/usr/bin/env bash
# Noisy-neighbor experiment, repeated runs (design §4, phase 9), against staging.
#   tests/evaluation/noisy_neighbor_round.sh <round-name> [repeats]
#
# Config "default": the gateway as deployed (0.25 vCPU, up to 2 replicas):
#   baseline, flood with limits on, flood with limits off (RateLimits__Enabled=false).
# Config "cpu050": the gateway at 0.5 vCPU / 1 GiB, to test whether the gateway's own CPU explains
#   tenant B's p95 rise with limits on: baseline, flood with limits on.
# Every temporary change to the staging gateway is undone on exit, even after a failure.
set -euo pipefail
ROUND=${1:?round name, for example round2}
REPEATS=${2:-3}
SUB=3f059998-c5af-4b64-83b6-f6e3c581b4fd
RG=rg-athlos-staging
APP=ca-athlos-gateway-staging
K6=${K6:-"/c/Program Files/k6/k6.exe"}
URL=https://$(az containerapp show --subscription $SUB -g $RG -n $APP --query properties.configuration.ingress.fqdn -o tsv)
OUT=tests/load/results/$ROUND
mkdir -p "$OUT"

update() { az containerapp update --subscription $SUB -g $RG -n $APP "$@" --query properties.latestRevisionName -o tsv; sleep 45; }
restore() {
  echo "restoring the staging gateway"
  az containerapp update --subscription $SUB -g $RG -n $APP --remove-env-vars RateLimits__Enabled --cpu 0.25 --memory 0.5Gi --query properties.latestRevisionName -o tsv || true
}
trap restore EXIT

run() { # config mode repeat
  local label="$1-$2-$3"
  echo "== $label"
  "$K6" run --quiet -e BASE_URL="$URL" -e MODE="$( [ "$2" = baseline ] && echo baseline || echo flood )" -e LABEL="$ROUND/$label" tests/load/noisy-neighbor.js > /dev/null
  python -c "import json; d=json.load(open('$OUT/$label.json')); b=d['tenantB']; a=d['tenantA'] or {}; print(f\"B p50={b['p50Ms']:.0f} p95={b['p95Ms']:.0f} err={b['errorRate']:.3f}  A refused={a.get('rejectedRate', 0):.3f}\")"
}

for i in $(seq 1 "$REPEATS"); do run default baseline "$i"; run default limits-on "$i"; done
update --set-env-vars RateLimits__Enabled=false
for i in $(seq 1 "$REPEATS"); do run default limits-off "$i"; done
update --remove-env-vars RateLimits__Enabled --cpu 0.5 --memory 1.0Gi
for i in $(seq 1 "$REPEATS"); do run cpu050 baseline "$i"; run cpu050 limits-on "$i"; done
