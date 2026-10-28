# Old code on a new schema (backlog item 41)

Migrations only go forward (design §12.2, expand then contract). While a deploy rolls out, and after a rollback, the previous release's apps run on the newer schema. `MigrationRuleTests` checks the rule on each migration's code; this checks the behaviour: the previous release's own backend tests, run unchanged against the next release's schema.

## How

`tests/Athlos.Testing/AthlosDatabases.cs` (the fixture every backend suite shares) takes `ATHLOS_TEST_NEWER_MIGRATOR`, the path to a newer release's built db-migrator. With it set, each fresh test database is first prepared by that migrator (roles, every migration, RLS, grants), then the fixture's own, older migrations find nothing left to apply, and the tests run as usual. The `previous-release` job in `ci.yml` does this on every release PR into main: it checks out the latest release tag, builds the PR's db-migrator, and runs the release's `dotnet test`. A release from before the seam existed (v1.19.0 and older) is skipped with a notice, so the job takes effect from the first release that carries it.

## First check: v1.18.0 on v1.19.0's schema (28 October 2026, by hand)

The v1.18.0 tag in a worktree, with only the fixture's seam copied in, against the db-migrator built from dev after v1.19.0. The schema change between them is v1.19.0's `email_suppressions` table in tenancy (design §5.3).

| v1.18.0 suite | Tests | Passed |
|---|---|---|
| Tenancy integration | 80 | 80 |
| Isolation (including the schema-wide RLS check, which now also sees `email_suppressions`) | 266 | 266 |
| Ordering integration | 61 | 61 |
| Catalog integration | 38 | 38 |
| Enrollment integration | 38 | 38 |
| **Total** | **483** | **483** |

Negative control: pointing `ATHLOS_TEST_NEWER_MIGRATOR` at a migrator that does not exist fails every test with "The newer db-migrator failed", so the newer migrator really prepared the databases in the run above.

## What it does not show

- The apps against the schema in Azure: the tests use the same code paths (EF, RLS, the app roles) but not the deployed containers.
- A contract step: when a release finally drops a column, the job is what proves the release before it no longer reads it.
