#!/usr/bin/env bash
# Cold start from zero replicas (design §15.3: "measured and reported, not optimized").
#   tests/evaluation/cold_start.sh <env> [samples]
# Waits until the gateway and catalog-api have scaled to zero, then times the first request of a
# storefront program list (gateway -> catalog -> PostgreSQL) and a second, warm one right after.
set -euo pipefail
ENV=${1:-dev}
SAMPLES=${2:-3}
SUB=3f059998-c5af-4b64-83b6-f6e3c581b4fd
RG=rg-athlos-$ENV
URL=$(az containerapp show --subscription $SUB -g $RG -n ca-athlos-gateway-$ENV --query properties.configuration.ingress.fqdn -o tsv)
replicas() { az containerapp replica list --subscription $SUB -g $RG -n "$1" --query "length(@)" -o tsv 2>/dev/null || echo 1; }

echo "sample,cold_seconds,warm_seconds,status"
for i in $(seq 1 "$SAMPLES"); do
  until [ "$(replicas ca-athlos-gateway-$ENV)" = "0" ] && [ "$(replicas ca-athlos-catalog-api-$ENV)" = "0" ]; do sleep 30; done
  cold=$(curl -s -o /dev/null -m 120 -w "%{time_total},%{http_code}" "https://$URL/api/v1/marketplace/programs?coach=demo-forge")
  warm=$(curl -s -o /dev/null -m 120 -w "%{time_total}" "https://$URL/api/v1/marketplace/programs?coach=demo-forge")
  echo "$i,${cold%,*},$warm,${cold#*,}"
done
