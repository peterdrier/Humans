# Gdpr — Data Access

## Gdpr

Folder: `src/Sections/Humans.Gdpr/Services/`. No owned DB tables —
the export orchestrator runs over per-section `IUserDataContributor`
fan-out.

### GdprExportService (Scoped)

No repository, no direct DB access, no cache. Injects
`IEnumerable<IUserDataContributor>`; every section that owns per-user
tables implements that interface and registers itself beside the service
that owns the data, so who is in the list is never decided here. The
roster of contributors and the export section each one emits is kept in
one place — [`docs/features/global/gdpr-export.md`](../../../../docs/features/global/gdpr-export.md);
each contributor's own section also lists it in that section's
`data-access.md`.

---
