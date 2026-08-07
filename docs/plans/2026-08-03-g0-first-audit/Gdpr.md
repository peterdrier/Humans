# Gdpr — G0 First Audit

**Section:** Gdpr · **Kind:** orchestrator (frozen-inventory ruling: "manages the export/erasure bits across sections") · **Audited:** 2026-08-05 @ 94535e688

Gdpr owns zero database tables — it fans out to every registered `IUserDataContributor` and merges the results — so G1 predicates 1/2/4 are vacuously satisfied, the same pattern as Guide/Scanner in the original G0 pass.

**Scope caveat — this scorecard audits the export half only.** The frozen inventory says Gdpr "manages the export/erasure bits across sections", and the erasure half *is* built: `IAccountDeletionService` (`src/Humans.Application/Interfaces/Users/IAccountDeletionService.cs`) / `AccountDeletionService` (`src/Humans.Application/Services/Users/AccountLifecycle/AccountDeletionService.cs`) implement the deletion/anonymization cascade — `RequestDeletionAsync`, `CancelDeletionAsync`, `PurgeAsync` (L60), `AnonymizeExpiredAccountAsync` (L77) — driven by `ProcessAccountDeletionsJob`. It lives under **Users**, not Gdpr, so it is neither scored for ownership/grouping below nor covered by the G3 rows. Whether it moves to Gdpr or Gdpr's charter narrows to export-only is an open ruling, queued as G1 gap #2 rather than silently scored as "not built".

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository, in-section | **N/A** | No owned tables; `GdprExportService` (`src/Humans.Application/Services/Gdpr/GdprExportService.cs`) injects only `IEnumerable<IUserDataContributor>`, `IClock`, `ILogger` — no repository, no `HumansDbContext`. |
| 2 | One writer-service per table | **N/A** | Same — the Gdpr-namespaced surface never writes; it's a read-only export fan-out. Erasure *is* built but lives under Users (`AccountDeletionService`), so it isn't scored here — see the scope caveat above and G1 gap #2. |
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
| 3 | Invariants/triggers each have a test | **PARTIAL** | No `docs/sections/Gdpr.md` exists (predicate 7). The section's one real invariant — *every user-scoped section service must contribute to the export, and the wiring must not silently drop one* — is tested thoroughly but **not exhaustively** by `GdprExportDependencyInjectionTests.cs`: it reflects over `Humans.Infrastructure`+`Humans.Application` for every `IUserDataContributor` implementer, cross-checks against an explicit `ExpectedContributorTypes` allowlist (21 types), verifies each is registered in DI, and resolves the real forwarding factories to catch a duplicated/mis-forwarded registration a naive count wouldn't (`EveryIUserDataContributorFactoryForwardsToAnExpectedConcreteType`). **The uncaught case is documented in the test file itself** (L68-72): a new user-scoped section whose owning service never implements `IUserDataContributor` at all leaves reflection nothing to enumerate, so the tests pass vacuously and the section is silently missing from every export. The only guardrail there is prose (`design-rules.md` §8a). Tracked as G3 gap #1. `GdprExportServiceTests.cs` covers duplicate-section detection, contributor-exception propagation ("never swallow"), and the `ExportedAt` clock stamp. |
| 4 | No skipped tests without an issue ref | **PASS** | No `Skip\s*=` in `GdprExportServiceTests.cs` or `GdprExportDependencyInjectionTests.cs`. |
| 5 | Tests grouped under the section | **PASS** | Both files sit in `tests/Humans.Application.Tests/Services/Gdpr/`. (`ExpenseReportServiceGdprTests.cs` under `Services/Expenses/` is Expenses' own contributor test, correctly grouped with Expenses, not Gdpr.) |

## G1 gap list

1. **`docs/sections/Gdpr.md` doesn't exist** (predicate 7). Fix: write it — the section is small and the "orchestrator, fans out to `IUserDataContributor`" shape is already well-documented in code comments (`IGdprExportService.cs`, `GdprExportDependencyInjectionTests.cs`'s class summary), so this is largely a transcription. No-migration-needed: **y**.
2. **Erasure is built but sits outside the section** (scope caveat above). `AccountDeletionService` implements the GDPR right-to-deletion cascade under Users, while the frozen inventory assigns "export/erasure" to Gdpr. Nothing is broken — the code works and is owned — but the boundary as documented and the boundary as coded disagree, so the erasure orchestrator is currently scored by neither section's G1 ownership pass. Fix: rule on it — either move `AccountDeletionService` under Gdpr, or amend the frozen inventory to scope Gdpr to export and leave erasure as a Users-owned lifecycle concern. Needs Peter's ruling; no code change until then. No-migration-needed: **y**.
**Not a gap — correction to the frozen-inventory doc, now applied there.** It listed Gdpr as still needing `reforge.surface-score.json` back-propagation; Gdpr is in fact already present (`'Gdpr' in data['sections']` → `True`, mapping `Interfaces/Gdpr/**` + `Services/Gdpr/**` + `Gdpr*`/`IGdpr*` symbols to `IGdprExportService`), as is Search. Gate/Settings/Development are the three genuinely outstanding. Recording the correction here wasn't enough on its own — the canonical follow-up list is what an agent works from — so **follow-up #1 in [`2026-08-03-proposed-frozen-section-inventory.md`](../2026-08-03-proposed-frozen-section-inventory.md) now strikes Gdpr/Search directly**. Nothing to fix.

## G3 gap list

1. **The contributor-coverage invariant can pass vacuously** (predicate 3) — a new user-scoped section that never implements `IUserDataContributor` is invisible to the reflection-based test, so it would be omitted from every GDPR export with a green suite. The gap is called out in `GdprExportDependencyInjectionTests.cs` L68-72 and guarded only by prose in `design-rules.md` §8a. Fix: cross-check the contributor list against the section/table-ownership map (the same source `surface-score.json` uses) so a section with user-scoped tables and no contributor fails the test rather than going unnoticed. No-migration-needed: **y**.

Beyond that, Gdpr's test coverage is thorough for the export surface. Erasure (G1 gap #2) carries its own coverage, scored under Users, and enters this ladder if the ownership ruling moves it here.

## G2 queue notes

Gdpr owns no tables — nothing to demolish or rename. `docs/plans/2026-08-03-demolition-inventory.md:604-607` names `Development`, `Gdpr` and `Search` as the genuinely unchecked G2 surfaces (its 2026-08-03 correction, which also clears `Gate` and `Settings` as already swept). **This scorecard is that check, and its answer is "nothing to demolish": Gdpr owns zero tables**, so the inventory entry is satisfied rather than still open — no sweep is owed and none should be requeued from it.
