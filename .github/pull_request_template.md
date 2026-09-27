## What

<!-- One or two sentences. Link the design section this follows (docs/design.md §...). -->

## Checklist

- [ ] Conventional Commit title (`feat:`, `fix:`, `chore:`, `docs:`, `test:`, `refactor:`)
- [ ] New tenant-owned entities implement `ITenantOwned` and have an RLS policy (`ENABLE` + `FORCE`)
- [ ] Every new endpoint has a case in `tests/Athlos.IsolationTests` (another tenant's resource → `404`)
- [ ] No `IgnoreQueryFilters()` outside the cross-tenant slices in design §3.2
- [ ] No secrets, no email addresses in logs
- [ ] OpenAPI documents updated if an endpoint changed
