# Tickets — G0 First Audit

**Section:** Tickets · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner Tickets --tables ticket_orders,ticket_attendees,ticket_sync_states,ticket_transfer_requests` → **0 violations**. Single `ITicketRepository`. |
| 2 | One writer-service per table | PASS | `TicketQueryService` (read) + `TicketSyncService` (write/sync) + `TicketTransferService` all route through the one repository; `TicketingBudgetService` is explicitly repository-free (reads via `ITicketServiceRead`, writes delegate to `IBudgetService`) per the #815 removal of its former dedicated `ITicketingBudgetRepository`. No interceptor pattern. |
| 3 | No EF entity leaks across boundary | PASS | No `Ticket*` rows in `ApplicationServiceEntityReadReturns.baseline.txt`. `MatchedUser` navs are stripped from both `TicketOrder` and `TicketAttendee` (FK-only, joined in-memory via `IUserService.GetByIdsAsync`). |
| 4 | No cross-section EF joins | PASS | No `CrossSectionEfJoinAnalyzer` baseline entries. |
| 5 | No `[Obsolete]` navs / `[Grandfathered]` / owned baseline rows (or queued item) | **PARTIAL** | No entity-leak or Obsolete-nav debt, but `DisplaySortInControllers.baseline.txt` carries **17 rows** for `TicketRepository.cs` (9× `OrderBy`, 8× `OrderByDescending`) — by far the largest single-file baseline count of any section in this batch. All 17 are pre-existing sort-in-repository debt with no queued G2 item found. |
| 6 | Controllers thin (no HUM0031 grandfathers) | PASS | None of `TicketController`, `TicketTransferController`, `TicketsContactsAdminController` appear in the HUM0031 grep hit list. |
| 7 | `docs/sections/Tickets.md` current | PASS (high confidence) | Very detailed, references recent work (#815 `ITicketingBudgetRepository` removal, gate-terminal login integration, transfer void/reissue automation). No drift observed. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | **FAIL** | `TicketRepositoryTests.cs:23` and `TicketRepository_OrderDriftTests.cs:22` both use `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL (partial)** | `TicketQueryServiceTests.cs`, `TicketSyncServiceTests.cs`, `TicketSyncServiceNullOrderTests.cs` all reference `ServiceTestHarness`. `TicketingBudgetServiceTests.cs`, `TicketTailorServiceTests.cs`/`TicketTailorServiceCachingTests.cs`/`TicketTailorServiceWriteTests.cs` not individually re-verified this pass but showed no positive `HumansDbContext` hits in the broad section-directory scan. |
| 3 | Invariants/triggers each have a test | PARTIAL | Spot-check for `NormalizingEmailComparer`/auto-matching in `TicketSyncServiceTests.cs` by that exact name came back empty — the invariant (verified-email-only auto-matching with collision handling) may be tested under different naming/setup rather than absent; **not confirmed either way** this pass. The async-payment-adjacent invariants (VAT computation, "has a ticket" definition) were not spot-checked. Recommend a targeted follow-up given this section's auto-matching logic is safety-relevant (mismatched ticket data has real-world door/access implications). |
| 4 | No skipped tests without issue ref | PASS (tentative) | No hits found. |
| 5 | Tests grouped under section | **PARTIAL** | `TicketQueryServiceTests.cs`, `TicketSyncServiceTests.cs`, `TicketSyncServiceNullOrderTests.cs`, `TicketTailorServiceTests.cs` (+2 more), `TicketingBudgetServiceTests.cs` all sit flat at `Services/` root; a `Services/Tickets/` subfolder exists but the bulk of the named test files live outside it. Same repo-wide pattern as Profiles/Users/Teams. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| 17 `DisplaySortInControllers` baseline rows on `TicketRepository.cs` | `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs` | Largest single-file sort-in-repository debt in the codebase by this audit's count. Move display sorts (orders list, attendee list, sales aggregates, gate list) into the controller/view-model layer per `memory/architecture/display-sort-in-controllers.md`. Worth flagging as a discrete G1/tech-debt-ledger item given the row count. | y |

## G3 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| `TicketRepositoryTests.cs` / `TicketRepository_OrderDriftTests.cs` use EF-InMemory | `tests/Humans.Application.Tests/Repositories/` | Migrate to shared Postgres fixture per #764/#766. | y |
| `TicketQueryServiceTests.cs`/`TicketSyncServiceTests.cs`/`TicketSyncServiceNullOrderTests.cs` use `ServiceTestHarness` (DbContext-backed) instead of mocked `ITicketRepository` | `tests/Humans.Application.Tests/Services/` | Migrate to `Substitute.For<ITicketRepository>()` pattern, matching Store's clean example. | y |
| Auto-matching (`NormalizingEmailComparer`) invariant test presence unconfirmed | `TicketSyncServiceTests.cs` | Confirm coverage exists (search under alternate naming) or add an explicit test for the "collision among verified emails leaves both unmatched + LogError" edge case — this is a data-integrity-error path that's easy to leave untested since it should never trigger in practice. | y |

## G2 queue notes

`TicketTransferRequest.VendorStepsJson` is unused, dormant, named as pending a post-soak drop PR — already a tracked demolition-inventory item.

## Verdict

**G1: 1 gap (17-row sort-in-repository baseline, largest in the batch) · G3: 3 gaps (EF-InMemory repo tests ×2, DbContext-backed service tests ×3, unconfirmed auto-matching invariant coverage)**
