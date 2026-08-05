# Gdpr — G0 First Audit

**Section:** Gdpr · **Kind:** orchestrator (frozen-inventory ruling: "manages the export/erasure bits across sections") · **Audited:** 2026-08-05 @ 94535e688

Gdpr owns zero database tables — it fans out to every registered `IUserDataContributor` and merges the results — so G1 predicates 1/2/4 are vacuously satisfied, the same pattern as Guide/Scanner in the original G0 pass.

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository, in-section | **N/A** | No owned tables; `GdprExportService` (`src/Humans.Application/Services/Gdpr/GdprExportService.cs`) injects only `IEnumerable<IUserDataContributor>`, `IClock`, `ILogger` — no repository, no `HumansDbContext`. |
| 2 | One writer-service per table | **N/A** | Same — Gdpr never writes; it's a read-only export fan-out (erasure is a separate, not-yet-built piece per the frozen-inventory description). |
| 3 | No EF entity leaks across the boundary | **PASS** | `IGdprExportService.ExportForUserAsync` returns `GdprExport` (a DTO wrapping `Dictionary<string, object?>` sections), never an EF entity. Zero Gdpr entries in `ApplicationServiceEntityReadReturns.baseline.txt`. |
| 4 | No cross-section EF joins (zero baseline entries) | **PASS** | No repository/DbContext exists to join across. Zero baseline entries in all 5 baseline files. |
| 5 | No `[Obsolete]` cross-section navs, no `[Grandfathered]`, no baseline rows owned by Gdpr | **PASS** | No `[Grandfathered]` hits under `Application/Interfaces/Gdpr/` or `Application/Services/Gdpr/`. |
| 6 | Controllers thin — no HUM0031 grandfathers | **N/A / PASS** | Gdpr owns no controller of its own; `IGdprExportService` is consumed from `ProfileController.cs` and `GuestController.cs` (other sections' controllers). The export action in `ProfileController.cs` (~line 1799-1812) is thin — resolves the current user, calls `gdprExportService.ExportForUserAsync`, serializes the result to a file download. `ProfileController.cs` *does* carry one `HUM0031` grandfather (`ProfileController.cs:288-291`, on `Edit(POST)`'s field-level validation guards), but it belongs to an unrelated, Profile-owned action, not the GDPR export path — verified by reading the grandfather's surrounding code. |
| 7 | `docs/sections/Gdpr.md` exists and matches reality | **FAIL** | Doesn't exist (`ls docs/sections/ | grep -i gdpr` → no hits). |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests real Postgres, zero EF-InMemory | **N/A** | No repository. |
| 2 | Service tests mock repository/`I…ServiceRead`, zero `HumansDbContext` | **PASS** | `GdprExportServiceTests.cs` constructs `GdprExportService` directly with fake `IUserDataContributor[]` (`FakeContributor`), a `FakeClock`, and `NullLogger` — no `HumansDbContext`, no `ServiceTestHarness` inheritance. |
| 3 | Invariants/triggers each have a test | **PASS** | No `docs/sections/Gdpr.md` exists (predicate 7), but the section's one real invariant — *every user-scoped section service must contribute to the export, and the wiring must not silently drop one* — is tested exhaustively by `GdprExportDependencyInjectionTests.cs`: it reflects over `Humans.Infrastructure`+`Humans.Application` for every `IUserDataContributor` implementer, cross-checks against an explicit `ExpectedContributorTypes` allowlist (21 types), verifies each is registered in DI, and even resolves the real forwarding factories to catch a duplicated/mis-forwarded registration that a naive count wouldn't catch (`EveryIUserDataContributorFactoryForwardsToAnExpectedConcreteType`). `GdprExportServiceTests.cs` covers duplicate-section detection, contributor-exception propagation ("never swallow"), and the `ExportedAt` clock stamp. |
| 4 | No skipped tests without an issue ref | **PASS** | No `Skip\s*=` in `GdprExportServiceTests.cs` or `GdprExportDependencyInjectionTests.cs`. |
| 5 | Tests grouped under the section | **PASS** | Both files sit in `tests/Humans.Application.Tests/Services/Gdpr/`. (`ExpenseReportServiceGdprTests.cs` under `Services/Expenses/` is Expenses' own contributor test, correctly grouped with Expenses, not Gdpr.) |

## G1 gap list

1. **`docs/sections/Gdpr.md` doesn't exist** (predicate 7). Fix: write it — the section is small and the "orchestrator, fans out to `IUserDataContributor`" shape is already well-documented in code comments (`IGdprExportService.cs`, `GdprExportDependencyInjectionTests.cs`'s class summary), so this is largely a transcription. No-migration-needed: **y**.
2. **Not yet in `reforge.surface-score.json`.** Correction to the frozen-inventory doc: it IS present (`'Gdpr' in data['sections']` → `True`, mapping `Interfaces/Gdpr/**` + `Services/Gdpr/**` + `Gdpr*`/`IGdpr*` symbols to `IGdprExportService`) — Gdpr and Search are the two rows the frozen inventory's follow-up #1 already back-propagated; Gate/Settings/Development are the ones still outstanding. No gap here.

## G3 gap list

None found — Gdpr's test coverage is thorough for the surface that exists today (export-only). If/when the "erasure" half the frozen inventory mentions is built, it enters this ladder fresh.

## G2 queue notes

Gdpr owns no tables — nothing to demolish or rename. Not in `docs/plans/2026-08-03-demolition-inventory.md` (drafted before this row was admitted).
