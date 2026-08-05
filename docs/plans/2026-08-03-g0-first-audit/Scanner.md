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
| 3 | Invariants/triggers each have a test | PASS (spot-check) — **corrected 2026-08-03** | The original PARTIAL rested on not locating a file that does exist: `tests/Humans.Web.Tests/Controllers/ScannerControllerTests.cs`, with dedicated cases for the matched, unmatched, void-transfer and door-context card paths, all driven through mocked read interfaces. Because the controller is exercised entirely through read-only substitutes, the safety-critical "never writes state" invariant is structurally pinned by that suite rather than untested. Not line-mapped against every documented invariant, hence spot-check rather than full PASS. |
| 4 | No skipped tests without issue ref | PASS (tentative) | No evidence of skips found. |
| 5 | Tests grouped under section | PASS — **corrected 2026-08-03** | `tests/Humans.Web.Tests/Controllers/ScannerControllerTests.cs` is the section's test file and is section-named; it sits with the other Web controller tests, the repo-wide convention. |

## G1 gap list

None found.

## G3 gap list

**None — corrected 2026-08-03 (was 1).** The single gap here was "no located test confirming the
read-only/no-check-in-write invariant", justified purely by not finding the file during the pass.
`tests/Humans.Web.Tests/Controllers/ScannerControllerTests.cs` exists and covers the matched,
unmatched, void-transfer and door-context card paths through mocked read interfaces. The gap was
an artifact of the search, not a real coverage hole, so it is withdrawn rather than rescheduled.

Residual (not counted as a gap): that suite pins the read-only invariant *structurally* — every
injected interface is read-only — rather than with an explicit "receives no write calls"
assertion. Adding one would be nice-to-have hardening, not a G3 predicate failure.

## Schema demolition queue

Owns no tables, no schema debt. Nothing queued.
