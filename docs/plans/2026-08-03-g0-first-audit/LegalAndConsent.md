# LegalAndConsent — G0 First Audit

**Section:** LegalAndConsent (surface-score.json config key: `Consent`) · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | PASS | `reforge ownership-violations --owner LegalAndConsent --tables legal_documents,document_versions,consent_records` → 0 violations. `ILegalDocumentRepository` and `IConsentRepository` are the sole `DbContext` consumers. |
| 2 | One writer-service per table | **FAIL — known, currently open, parallel fix in flight** | `legal_documents`/`document_versions` currently have a 2–3-way writer situation: `AdminLegalDocumentService` (admin CRUD), `LegalDocumentSyncService` (GitHub sync), and `LegalDocumentSaveChangesInterceptor` (`src/Humans.Infrastructure/Data/LegalDocumentSaveChangesInterceptor.cs`, still present, still tested via `tests/.../Infrastructure/LegalDocumentSaveChangesInterceptorTests.cs`). This is issue #751, tracked in this repo's active work as **Lane 3: "consolidate legal document services, drop interceptor" — currently `in_progress` in a parallel lane tonight**. Recording current state honestly as instructed; not double-counting it as new since it's already a tracked, staffed fix. |
| 3 | No EF entity leaks across boundary | **FAIL** | `LegalDocument.Team` (`LegalDocument.cs:30`) is a **live, unstripped** cross-domain nav — not even `[Obsolete]`-marked. `LegalDocumentRepository.GetActiveRequiredDocumentsForTeamsAsync` (`LegalDocumentRepository.cs:101`) still does `.Include(d => d.Team)`, and `ConsentService.GetConsentDashboardAsync` reads `g.First().Team` off it. `Team.LegalDocuments` reverse nav (`Team.cs:160`) also still lives, cross-domain, on the Teams entity. This is worse than Issues'/GoogleIntegration's `[Obsolete]`-marked navs — it's actively walked in a production read path (the fallback/cache-miss path for the T-04 cache). Doc's own "Cross-domain navs still declared (strip deferred)" section already names this exact gap. |
| 4 | No cross-section EF joins (zero baseline entries) | **FAIL — corrected 2026-08-03** | No LegalAndConsent/Legal/Consent rows in any of the 5 baseline files, but HUM0024 is **attribute**-allowlisted, so those files can't prove either configuration clean. Both `ConsentRecordConfiguration.cs` (`HasOne<User>()` on `UserId` → `AspNetUsers`, `:49`) and `LegalDocumentConfiguration.cs` (`HasOne<Team>()` on `TeamId` → `teams`, `:44`) carry active `[Grandfathered("HUM0024", …)]` markers. The `consent_records.UserId` FK is independent of the `LegalDocument.Team` nav found in predicate 3, so stripping that nav does **not** leave the section schema-clean. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | **FAIL** | See predicate 3 — `LegalDocument.Team`/`Team.LegalDocuments` are live (not even downgraded to `[Obsolete]`). The original "no `[Grandfathered]` hits" claim came from grepping controllers only; both EF configurations are in fact grandfathered under HUM0024 (see predicate 4). |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | No `Grandfathered` hits on `LegalController.cs`, `ConsentController.cs`, `AdminLegalDocumentsController.cs`. |
| 7 | `docs/sections/LegalAndConsent.md` current | PASS | Exceptionally detailed and current — it already documents predicates 2 and 3's gaps itself, down to file:line, with an explicit "Touch-and-clean guidance" section. The doc is ahead of the code here, not behind it. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | FAIL | `tests/.../Repositories/ConsentRepositoryTests.cs:27` and `LegalDocumentRepositoryTests.cs:23` both call `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | The `HumansDbContext` grep is a false negative: `ConsentServiceTests` and `AdminLegalDocumentServiceTests` extend `ServiceTestHarness` and construct concrete repositories. `ServiceTestHarness` (`tests/Humans.Application.Tests/Infrastructure/ServiceTestHarness.cs:54,71`) builds a real `HumansDbContext` over `.UseInMemoryDatabase(...)` and exposes it as `Db`/`DbFactory`, so the EF setup is inherited rather than declared. Same false-negative pattern corrected across Auth, Budget, Camps, CityPlanning, Feedback and Governance in this pass. (`LegalDocumentSaveChangesInterceptorTests.cs` still doesn't count — testing an EF interceptor genuinely needs a context.) |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively mapped. The DB-trigger immutability invariant (`prevent_consent_record_update/delete`) is pinned at the interface level by `ConsentArchitectureTests.IConsentRepository_HasNoUpdateOrDeleteOrRemoveMethods`, but that only tests the C# surface, not that the Postgres triggers themselves still exist/fire — that would need an integration test against real Postgres, which is a separate ask from G3. Flagging as a gap: no test exercises the actual DB trigger. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` anywhere in `tests/`. |
| 5 | Tests grouped under section | PARTIAL | Service tests are correctly split under `Services/Consent/` and top-level `Services/*LegalDocument*`/`*Consent*`; but the two repository tests sit in the shared `tests/.../Repositories/` folder rather than a section folder. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| 2–3-way writer on `legal_documents`/`document_versions` (#751) | `AdminLegalDocumentService`, `LegalDocumentSyncService`, `LegalDocumentSaveChangesInterceptor` | **Already staffed** — Lane 3 in current sprint, in progress: consolidate writers, drop the interceptor. | y |
| `LegalDocument.Team` / `Team.LegalDocuments` live cross-domain navs | `LegalDocument.cs:30`, `Team.cs:160`, `LegalDocumentRepository.cs:101` | Strip nav, convert to typed-FK, move `ConsentService.GetConsentDashboardAsync`'s `Team` read to `ITeamService.GetTeamNamesByIdsAsync` (doc already prescribes this). | y |
| **Added 2026-08-03:** 2 HUM0024 configuration grandfathers | `ConsentRecordConfiguration.cs:49` (`consent_records.UserId → AspNetUsers`), `LegalDocumentConfiguration.cs:44` (`legal_documents.TeamId → teams`) | See predicate 4. The Users FK cut is independent of the `LegalDocument.Team` nav-strip row above and must be queued separately; retire the markers once both are cut. | y (attribute work); FK cuts are schema work |

## G3 gap list

> Added 2026-08-03: this list was missing, and the append-only-trigger item below was
> miscounted under G1. Whether a Postgres trigger has an integration test is predicate
> **G3.3**, not a G1 ownership predicate — the G3 table already scores it PARTIAL.

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| No test exercises the Postgres append-only trigger directly | `tests/` | Add one integration test (once #764's shared Postgres fixture is available) that attempts an UPDATE/DELETE against a real `consent_records` row and asserts the DB rejects it. | y |


**Added 2026-08-03 — harness-inherited EF-InMemory (G3.2).** `ConsentServiceTests` and `AdminLegalDocumentServiceTests` extend `ServiceTestHarness`, which stands up a real `HumansDbContext` over `.UseInMemoryDatabase(...)`; the original pass missed this because it grepped for a literal `HumansDbContext` the files never name. Fix: convert to `Substitute.For<IConsentRepository>()` per #766, or move these off the harness. No-migration-needed: **y**.

## Schema demolition queue

Two cross-section FK cuts are queued (see the G1 gap list): `consent_records.UserId → AspNetUsers`
and `legal_documents.TeamId → teams`, both currently HUM0024-grandfathered. Once #751 lands, the
`LegalDocument.Team` nav is stripped and both FKs are cut, this section is schema-clean — no dead
columns/tables spotted.

## Headline

The live `LegalDocument.Team` nav is the worst entity-leak found in this batch; #751 writer consolidation is already staffed.
