# Pleo — Data Access

## Pleo

Folder: `src/Sections/Humans.Pleo/Services/` (class `Service`). **DbContext:** `PleoDbContext`. `Repository`
(`src/Sections/Humans.Pleo/Data/Repository.cs`, implements `IPleoRepository`) injects
`IDbContextFactory<PleoDbContext>` directly. Owns `PleoEntries`, `PleoEmployees`, `PleoAccounts`, `PleoBalances`,
`PleoSyncStates`, `PleoCategories`.

### PleoService (Scoped — class name `Service`, implements `IPleoService` + `IPleoAdminService`)

Repository: `IPleoRepository`, called through `Service` and the internal `PleoMirrorSync` and `PleoMirrorReader`
it forwards to.

| Table | R/W |
|-------|-----|
| PleoEntries | R/W (replaced whole by every sweep; read whole — a few hundred rows) |
| PleoEmployees | R/W (replaced by every sweep) |
| PleoAccounts | R/W (replaced by every sweep) |
| PleoBalances | R/W (one row, replaced by every sweep) |
| PleoSyncStates | R/W (one row) |
| PleoCategories | R/W (link, unlink, add from `/Pleo/Categories`; a sweep links, adds and remaps, never deletes or unlinks) |

No cross-section service calls besides the audit log. Read by Finance through `IPleoService` (settled entries, wallet balance, receipt files) for
`/Finance` and `/Finance/Pleo`. Its only outbound is `IPleoClient`, this section's read-only connector to
`external.pleo.io` (and Pleo's receipt CDN).
