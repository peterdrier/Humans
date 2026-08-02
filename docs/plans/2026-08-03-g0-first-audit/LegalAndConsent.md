# LegalAndConsent — G0 First Audit

**Section:** LegalAndConsent (surface-score.json config key: `Consent`) · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | PASS | `reforge ownership-violations --owner LegalAndConsent --tables legal_documents,document_versions,consent_records` → 0 violations. `ILegalDocumentRepository` and `IConsentRepository` are the sole `DbContext` consumers. |
| 2 | One writer-service per table | **FAIL — known, currently open, parallel fix in flight** | `legal_documents`/`document_versions` currently have a 2–3-way writer situation: `AdminLegalDocumentService` (admin CRUD), `LegalDocumentSyncService` (GitHub sync), and `LegalDocumentSaveChangesInterceptor` (`src/Humans.Infrastructure/Data/LegalDocumentSaveChangesInterceptor.cs`, still present, still tested via `tests/.../Infrastructure/LegalDocumentSaveChangesInterceptorTests.cs`). This is issue #751, tracked in this repo's active work as **Lane 3: "consolidate legal document services, drop interceptor" — currently `in_progress` in a parallel lane tonight**. Recording current state honestly as instructed; not double-counting it as new since it's already a tracked, staffed fix. |
| 3 | No EF entity leaks across boundary | **FAIL** | `LegalDocument.Team` (`LegalDocument.cs:30`) is a **live, unstripped** cross-domain nav — not even `[Obsolete]`-marked. `LegalDocumentRepository.GetActiveRequiredDocumentsForTeamsAsync` (`LegalDocumentRepository.cs:101`) still does `.Include(d => d.Team)`, and `ConsentService.GetConsentDashboardAsync` reads `g.First().Team` off it. `Team.LegalDocuments` reverse nav (`Team.cs:160`) also still lives, cross-domain, on the Teams entity. This is worse than Issues'/GoogleIntegration's `[Obsolete]`-marked navs — it's actively walked in a production read path (the fallback/cache-miss path for the T-04 cache). Doc's own "Cross-domain navs still declared (strip deferred)" section already names this exact gap. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | No LegalAndConsent/Legal/Consent rows in any of the 5 existing baseline files — but note this doesn't catch the live `.Include(d => d.Team)` above; that's a different analyzer's blind spot, not evidence the join is fine. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | **FAIL** | See predicate 3 — `LegalDocument.Team`/`Team.LegalDocuments` are live (not even downgraded to `[Obsolete]`). No `[Grandfathered]` hits in controllers. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | No `Grandfathered` hits on `LegalController.cs`, `ConsentController.cs`, `AdminLegalDocumentsController.cs`. |
| 7 | `docs/sections/LegalAndConsent.md` current | PASS | Exceptionally detailed and current — it already documents predicates 2 and 3's gaps itself, down to file:line, with an explicit "Touch-and-clean guidance" section. The doc is ahead of the code here, not behind it. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | FAIL | `tests/.../Repositories/ConsentRepositoryTests.cs:27` and `LegalDocumentRepositoryTests.cs:23` both call `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | PASS | `ConsentServiceTests.cs`, `LegalDocumentServiceTests.cs`, `AdminLegalDocumentServiceTests.cs`, `Services/Consent/CachingConsentServiceTests.cs` — no `HumansDbContext` references (grep scoped to `Services/Legal/**` and `Services/Consent/**` found none). `LegalDocumentSaveChangesInterceptorTests.cs` does use a real `HumansDbContext` — expected/necessary, since it's testing an EF interceptor directly, not counted against this predicate. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively mapped. The DB-trigger immutability invariant (`prevent_consent_record_update/delete`) is pinned at the interface level by `ConsentArchitectureTests.IConsentRepository_HasNoUpdateOrDeleteOrRemoveMethods`, but that only tests the C# surface, not that the Postgres triggers themselves still exist/fire — that would need an integration test against real Postgres, which is a separate ask from G3. Flagging as a gap: no test exercises the actual DB trigger. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` anywhere in `tests/`. |
| 5 | Tests grouped under section | PARTIAL | Service tests are correctly split under `Services/Consent/` and top-level `Services/*LegalDocument*`/`*Consent*`; but the two repository tests sit in the shared `tests/.../Repositories/` folder rather than a section folder. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| 2–3-way writer on `legal_documents`/`document_versions` (#751) | `AdminLegalDocumentService`, `LegalDocumentSyncService`, `LegalDocumentSaveChangesInterceptor` | **Already staffed** — Lane 3 in current sprint, in progress: consolidate writers, drop the interceptor. | y |
| `LegalDocument.Team` / `Team.LegalDocuments` live cross-domain navs | `LegalDocument.cs:30`, `Team.cs:160`, `LegalDocumentRepository.cs:101` | Strip nav, convert to typed-FK, move `ConsentService.GetConsentDashboardAsync`'s `Team` read to `ITeamService.GetTeamNamesByIdsAsync` (doc already prescribes this). | y |
| No test exercises the Postgres append-only trigger directly | `tests/` | Add one integration test (once #764's shared Postgres fixture is available) that attempts an UPDATE/DELETE against a real `consent_records` row and asserts the DB rejects it. | y |

## G2 queue notes

Once #751 lands and the `LegalDocument.Team` nav is stripped, this section is otherwise schema-clean — no dead columns/tables spotted.

## Verdict

`G1: 3 gaps (1 already in flight) · G3: 2 gaps (+1 PARTIAL) — headline gap: live LegalDocument.Team nav is the worst entity-leak found in this batch; #751 writer consolidation already staffed`
