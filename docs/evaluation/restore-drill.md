# Restore drill (backlog item 22)

Not run yet. The script is ready; the numbers go here after the first run.

## Why a point-in-time restore, not `pg_dump`

Every table forces RLS (design §3.2), and FORCE binds the table's owner too. Azure has no superuser, and no app or migrator role has `BYPASSRLS`, so a logical dump made by any role reads no tenant rows. The dump would restore an empty shop. The only backup that holds everything is the server's own: the 7-day point-in-time backups of `psql-athlos-shared` (`backupRetentionDays: 7`, design §10).

## What the drill does

[`tests/evaluation/restore_drill.sh`](../../tests/evaluation/restore_drill.sh), run by hand with `az` signed in and psql or Docker available:

1. Restores `psql-athlos-shared` as it was 15 minutes earlier (or `[minutes-ago]`) into a new server, `psql-athlos-drill-<time>`, in `rg-athlos-shared`. This is what a bad migration or a dropped table would need.
2. Makes the person running it the temporary server's Entra admin and opens its firewall to their IP only.
3. For every `athlos_*` database (4 services × dev, staging, prod), counts the tables, sums the row estimates in `pg_class.reltuples` and counts the applied migrations. The estimates come from the catalog, which RLS does not filter, so no tenant is entered and no customer row is read.
4. Prints the table below and the timings, then deletes the temporary server, even if a step failed.

Cost: a B1ms server for well under an hour, a few cents (backlog, "After the thesis is published" holds the off-server copies that would cost every month).

## Results

| Database | Tables | Rows (estimate) | Applied migrations |
|---|---|---|---|
| (after the first run) | | | |

- Restore point:
- Restore took (recovery time for the server):
- Ready to query after:

## What it shows and what it does not

- It measures the recovery time (RTO) of the one failure the shop can recover from today: a mistake inside the server, undone within 7 days. The recovery point (RPO) is the restore time asked for; Azure keeps the write-ahead log, so it is minutes.
- A restore makes a new server. Moving the apps to it means changing `postgresServerName` in `infra/env.bicep` and redeploying, which this drill does not time.
- A lost region still loses everything: the backups stay in `italynorth` (limitation 10).
