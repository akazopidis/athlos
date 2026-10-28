# Restore drill (backlog item 22)

Run twice on 28 October 2026 against `psql-athlos-shared` (v1.18.0 in prod, the same build on staging and dev). Raw output: [`run 1`](../../tests/evaluation/results/restore-drill-2026-10-28-run1.txt), [`run 2`](../../tests/evaluation/results/restore-drill-2026-10-28-run2.txt). **The server restores in 7–8 minutes and answers queries after 10, with every stage's twelve databases, tables, migrations and data.**

## Why a point-in-time restore, not `pg_dump`

Every table forces RLS (design §3.2), and FORCE binds the table's owner too. Azure has no superuser, and no app or migrator role has `BYPASSRLS`, so a logical dump made by any role reads no tenant rows. The dump would restore an empty shop. The only backup that holds everything is the server's own: the 7-day point-in-time backups of `psql-athlos-shared` (`backupRetentionDays: 7`, design §10).

## What the drill does

[`tests/evaluation/restore_drill.sh`](../../tests/evaluation/restore_drill.sh), run by hand with `az` signed in and psql or Docker available:

1. Restores `psql-athlos-shared` as it was 15 minutes earlier (or `[minutes-ago]`) into a new server, `psql-athlos-drill-<time>`, in `rg-athlos-shared`. This is what a bad migration or a dropped table would need.
2. Makes the person running it the temporary server's Entra admin and opens its firewall to their IP only.
3. For every `athlos_*` database (4 services × dev, staging, prod), counts the tables, the tables holding data on disk, their size with indexes and the applied migrations. All of it comes from the system catalog and file sizes, which RLS does not filter, so no tenant is entered and no customer row is read.
4. Prints the table below and the timings, then deletes the temporary server, even if a step failed.

Cost: a B1ms server for well under an hour, a few cents (backlog, "After the thesis is published" holds the off-server copies that would cost every month).

## Results

| | Run 1 | Run 2 |
|---|---|---|
| Restore point (15 minutes back) | 2026-10-28 09:01:57 UTC | 2026-10-28 09:14:29 UTC |
| Restore (`az ... restore` until the server is ready) | 430 s | 492 s |
| Ready to query (Entra admin and firewall added) | 570 s | 629 s |
| Temporary server deleted afterwards | yes | yes |

Run 2, per database (run 1 had the same tables and migrations):

| Database | Tables | Tables holding data | Size with indexes | Applied migrations |
|---|---|---|---|---|
| `athlos_catalog_dev` | 4 | 3 | 288 kB | 10 |
| `athlos_catalog_prod` | 4 | 0 | 96 kB | 10 |
| `athlos_catalog_staging` | 4 | 3 | 280 kB | 10 |
| `athlos_enrollment_dev` | 7 | 5 | 440 kB | 7 |
| `athlos_enrollment_prod` | 7 | 0 | 240 kB | 7 |
| `athlos_enrollment_staging` | 7 | 4 | 408 kB | 7 |
| `athlos_ordering_dev` | 9 | 5 | 448 kB | 10 |
| `athlos_ordering_prod` | 9 | 1 | 240 kB | 10 |
| `athlos_ordering_staging` | 9 | 6 | 472 kB | 10 |
| `athlos_tenancy_dev` | 7 | 5 | 312 kB | 7 |
| `athlos_tenancy_prod` | 7 | 3 | 256 kB | 7 |
| `athlos_tenancy_staging` | 7 | 4 | 312 kB | 7 |

- **Schema:** every database has the tables and the migrations its service's code has (EF model snapshots: tenancy 7 tables and 7 migrations, catalog 4 and 10, ordering 9 and 10, enrollment 7 and 7), so all three stages came back at v1.18.0's schema.
- **Data:** dev and staging, where the demo coaches, programs and test purchases live, have data in most tables; prod has almost none, as expected for a shop with no real coaches yet. "Tables holding data" counts tables whose heap has pages on disk. It is the honest measure here: no role can count rows through FORCE RLS without entering each tenant.
- **Run 1 measured rows with `pg_class.reltuples`** and got 0 for tables known to hold rows (staging's tenants). That estimate stays −1 until ANALYZE, which autovacuum skips for small tables, so the script now reads on-disk sizes instead.
- **Two fixes were found while preparing the first run:** `az ad` commands do not take `--subscription`, and `firewall-rule create` names the server `--server-name`. The unrun script would have failed at the first one, after paying for the restore.

## What it shows and what it does not

- It measures the recovery time (RTO) of the one failure the shop can recover from today, a mistake inside the server undone within 7 days: about 10 minutes until a restored copy answers. The recovery point (RPO) is the restore time asked for; Azure keeps the write-ahead log, so it is minutes.
- A restore makes a new server. Moving the apps to it means changing `postgresServerName` in `infra/env.bicep` and redeploying, which this drill does not time.
- A lost region still loses everything: the backups stay in `italynorth` (limitation 10).
