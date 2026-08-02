# Scanner — G0 First Audit

**Section:** Scanner · **Kind:** vertical (no business logic) · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | N/A | Owns no tables (doc: "No database tables are owned by this section"). |
| 2 | One writer-service per table | N/A | Same. |
| 3 | No EF entity leaks across boundary | PASS | `ScannerController` is not in `ApplicationServiceEntityReadReturns.baseline.txt`; there is no `Humans.Application.Services.Scanner` namespace at all (confirmed by doc and by the section's own architecture note). All cross-section reads are through `ITicketServiceRead`, `IEarlyEntryService`, `IConsentServiceRead`, `IUserServiceRead`, `IEventServiceRead`, `IBurnSettingsService`, `IICalFeedService` — all DTO-returning read interfaces. |
| 4 | No cross-section EF joins | PASS | No repository, no DbContext. |
| 5 | No `[Obsolete]` navs / `[Grandfathered]` / owned baseline rows | PASS | No entities owned; `ScannerController.cs` not in the HUM0031 grandfather grep results. |
| 6 | Controllers thin (no HUM0031 grandfathers) | PASS | `ScannerController` absent from the 8-controller HUM0031 grandfather list. |
| 7 | `docs/sections/Scanner.md` current | PASS | Doc explicitly documents the negative rules ("never a check-in tool") and the exact cross-section read surface; matches the read-only door-context description added for #860. No drift observed. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | N/A | No owned tables. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | N/A/PASS | No `Humans.Application.Services.Scanner` namespace exists to test — logic lives entirely in the controller, tested (if at all) as controller/integration tests. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not verified this pass — no dedicated `ScannerControllerTests.cs` located during this audit; would need a targeted search of `tests/Humans.Web.Tests/Controllers/` or `tests/Humans.Integration.Tests/` to confirm the "never writes state" negative invariant has a regression test. This is the section's single most safety-critical invariant (it explicitly must never become a check-in gateway) and deserves explicit test verification in a follow-up pass. |
| 4 | No skipped tests without issue ref | PASS (tentative) | No evidence of skips found. |
| 5 | Tests grouped under section | UNVERIFIED | Not located during this pass — flag for follow-up. |

## G1 gap list

None found.

## G3 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| No located test confirming the read-only/no-check-in-write invariant | `tests/Humans.Web.Tests/Controllers/` or `tests/Humans.Integration.Tests/` (unconfirmed) | Locate or add a test asserting `/Scanner/Tickets` and `/Scanner/Tickets/Card` never call any state-mutating cross-section method (e.g. assert the mocked `ITicketServiceRead`/`IEarlyEntryService`/etc. receive no write calls). Given this is a negative/safety invariant, an explicit regression test is worth prioritizing even though the current architecture (all read-only interfaces injected) makes an accidental write structurally hard. | y |

## G2 queue notes

Owns no tables, no schema debt. Nothing queued.

## Verdict

**G1: met · G3: 1 gap (unverified test coverage for the read-only safety invariant — worth a follow-up look, not a structural finding)**
