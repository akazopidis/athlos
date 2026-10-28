#!/usr/bin/env bash
# The restore drill (backlog item 22, docs/evaluation/restore-drill.md): restores the shared
# PostgreSQL server to a point in time on a temporary server, checks that every stage's databases
# came back with their rows, times it, and deletes the temporary server. Costs a few cents (a B1ms
# server for well under an hour).
#
# Why not pg_dump: every table forces RLS, which binds even the tables' owner, and Azure has no
# superuser, so a logical dump reads no tenant rows. The physical point-in-time restore is the
# backup that holds everything.
#
# The check reads what RLS cannot hide, so no tenant is entered and no row is read: the tables and
# applied migrations, how many tables hold data pages on disk (pg_relation_size of the heap) and the
# tables' size with indexes. Row estimates would not do: pg_class.reltuples stays -1 until ANALYZE, which
# autovacuum skips for small tables, and the recovery resets the live-row statistics. To connect, the
# script makes you the temporary server's Entra admin and opens its firewall to your IP only.
#
# Run it yourself, signed in with az (Azure for Students), with psql installed or Docker running:
#     bash tests/evaluation/restore_drill.sh [minutes-ago]     (default 15: restore to 15 minutes ago)
# It prints a Markdown table for the evaluation and always deletes the temporary server at the end.
set -euo pipefail

SUBSCRIPTION=3f059998-c5af-4b64-83b6-f6e3c581b4fd
GROUP=rg-athlos-shared
SOURCE=psql-athlos-shared
DRILL="psql-athlos-drill-$(date -u +%Y%m%d%H%M)"
MINUTES_AGO=${1:-15}
RESTORE_TO=$(date -u -d "-$MINUTES_AGO minutes" +%Y-%m-%dT%H:%M:%SZ)

az_() { az "$@" --subscription "$SUBSCRIPTION"; }

# psql from PATH, or from the postgres:17-alpine image.
psql_() {
  if command -v psql >/dev/null; then psql "$@"; else docker run --rm -i -e PGPASSWORD -e PGSSLMODE postgres:17-alpine psql "$@"; fi
}

cleanup() {
  echo "Deleting $DRILL..."
  az_ postgres flexible-server delete --resource-group "$GROUP" --name "$DRILL" --yes --output none 2>/dev/null || true
}
trap cleanup EXIT

echo "Restoring $SOURCE as it was at $RESTORE_TO into $DRILL..."
started=$(date +%s)
az_ postgres flexible-server restore --resource-group "$GROUP" --name "$DRILL" --source-server "$SOURCE" \
  --restore-time "$RESTORE_TO" --output none
restored=$(date +%s)
echo "Restored in $((restored - started)) s."

me=$(az ad signed-in-user show --query id --output tsv)
upn=$(az ad signed-in-user show --query userPrincipalName --output tsv)
ip=$(curl -s https://api.ipify.org)
az_ postgres flexible-server microsoft-entra-admin create --resource-group "$GROUP" --server-name "$DRILL" \
  --object-id "$me" --display-name "$upn" --type User --output none
az_ postgres flexible-server firewall-rule create --resource-group "$GROUP" --server-name "$DRILL" \
  --name drill-client --start-ip-address "$ip" --end-ip-address "$ip" --output none

host=$(az_ postgres flexible-server show --resource-group "$GROUP" --name "$DRILL" --query fullyQualifiedDomainName --output tsv)
export PGPASSWORD
PGPASSWORD=$(az_ account get-access-token --resource-type oss-rdbms --query accessToken --output tsv)
export PGSSLMODE=require

databases=$(psql_ "host=$host dbname=postgres user=$upn" -Atc "SELECT datname FROM pg_database WHERE datname LIKE 'athlos_%' ORDER BY datname")
ready=$(date +%s)
echo
echo "| Database | Tables | Tables holding data | Size with indexes | Applied migrations |"
echo "|---|---|---|---|---|"
for db in $databases; do
  read -r tables holding size < <(psql_ "host=$host dbname=$db user=$upn" -Atc \
    "SELECT count(*), count(*) FILTER (WHERE pg_relation_size(c.oid) > 0), replace(pg_size_pretty(coalesce(sum(pg_total_relation_size(c.oid)), 0)), ' ', '') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory'" | tr '|' ' ')
  migrations=$(psql_ "host=$host dbname=$db user=$upn" -Atc "SELECT count(*) FROM \"__EFMigrationsHistory\"" 2>/dev/null || echo "?")
  echo "| \`$db\` | $tables | $holding | $size | $migrations |"
done
echo
echo "Restore point: $RESTORE_TO. Restore took $((restored - started)) s; ready to query after $((ready - started)) s."
