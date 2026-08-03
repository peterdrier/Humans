# Tickets — G0 First Audit

**Section:** Tickets · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS (corrected evidence) | `reforge ownership-violations --owner Tickets --tables ticket_orders,ticket_attendees,ticket_sync_states,ticket_transfer_requests` → **0 violations** — predicate itself still holds, but the evidence text was wrong: there are **two** repositories in-section, not one. `ITicketRepository` (`ticket_orders`/`ticket_attendees`/`ticket_sync_states`) and a separate `ITicketTransferRepository`/`TicketTransferRepository` (`Interfaces/Repositories/ITicketTransferRepository.cs`, `Infrastructure/Repositories/Tickets/TicketTransferRepository.cs`) owning `ticket_transfer_requests`. Each table still maps to exactly one repository, so the predicate itself is unaffected — but see predicate 2. |
| 2 | One writer-service per table | **FAIL — corrected 2026-08-03** | `ticket_transfer_requests` has two writer-services, not one: `TicketTransferService` (its primary owner, injects `ITicketTransferRepository` as `transferRepo`) **and** `TicketSyncService.ReassignAsync` (`TicketSyncService.cs:193-200`), which calls `transferRepository.ReassignUserAsync` (`:197`) as part of the account-merge fold, alongside `ticketRepository.ReassignToUserAsync` (`:196`) for the other two tables. `TicketQueryService`/`TicketSyncService`/`TicketTransferService` "all route through the one repository" was true for `ITicketRepository`'s three tables but not for `ticket_transfer_requests`, which now has a second writer. |
| 3 | No EF entity leaks across boundary | PASS | No `Ticket*` rows in `ApplicationServiceEntityReadReturns.baseline.txt`. `MatchedUser` navs are stripped from both `TicketOrder` and `TicketAttendee` (FK-only, joined in-memory via `IUserService.GetByIdsAsync`). |
| 4 | No cross-section EF joins | PASS | No `CrossSectionEfJoinAnalyzer` baseline entries. |
| 5 | No `[Obsolete]` navs / `[Grandfathered]` / owned baseline rows (or queued item) | **PARTIAL** | No entity-leak or Obsolete-nav debt, but `DisplaySortInControllers.baseline.txt` carries **18 rows** for `TicketRepository.cs` (9× `OrderBy`, 9× `OrderByDescending` — corrected 2026-08-03; the original transcription said 17/8 and dropped one descending entry) — by far the largest single-file baseline count of any section in this batch. All 18 are pre-existing sort-in-repository debt with no queued G2 item found. |
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
| 18 `DisplaySortInControllers` baseline rows on `TicketRepository.cs` (corrected 2026-08-03, was 17) | `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs` | Largest single-file sort-in-repository debt in the codebase by this audit's count. Move display sorts (orders list, attendee list, sales aggregates, gate list) into the controller/view-model layer per `memory/architecture/display-sort-in-controllers.md`. Worth flagging as a discrete G1/tech-debt-ledger item given the row count. | y |
| **Added 2026-08-03:** Second writer-service on `ticket_transfer_requests` | `TicketSyncService.ReassignAsync` (`TicketSyncService.cs:197`) alongside `TicketTransferService` | Both call `ITicketTransferRepository`. The account-merge fold reassigning transfer requests belongs conceptually to the merge flow already living in `TicketSyncService`, but it's still a second writer-service against a table `TicketTransferService` otherwise owns — either route the fold through `ITicketTransferService`'s own interface or explicitly document this as an accepted account-merge-fold exception (same shape as Campaigns' `ReassignGrantsToUserAsync`). | y |

## G3 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| `TicketRepositoryTests.cs` / `TicketRepository_OrderDriftTests.cs` use EF-InMemory | `tests/Humans.Application.Tests/Repositories/` | Migrate to shared Postgres fixture per #764/#766. | y |
| `TicketQueryServiceTests.cs`/`TicketSyncServiceTests.cs`/`TicketSyncServiceNullOrderTests.cs` use `ServiceTestHarness` (DbContext-backed) instead of mocked `ITicketRepository` | `tests/Humans.Application.Tests/Services/` | Migrate to `Substitute.For<ITicketRepository>()` pattern, matching Store's clean example. | y |
| Auto-matching (`NormalizingEmailComparer`) invariant test presence unconfirmed | `TicketSyncServiceTests.cs` | Confirm coverage exists (search under alternate naming) or add an explicit test for the "collision among verified emails leaves both unmatched + LogError" edge case — this is a data-integrity-error path that's easy to leave untested since it should never trigger in practice. | y |

## G2 queue notes

`TicketTransferRequest.VendorStepsJson` is unused, dormant, named as pending a post-soak drop PR — already a tracked demolition-inventory item.

## Verdict

**G1: 2 gaps (corrected 2026-08-03, was 1 — added: second writer-service on `ticket_transfer_requests`; 18-row sort-in-repository baseline, largest in the batch) · G3: 3 gaps (EF-InMemory repo tests ×2, DbContext-backed service tests ×3, unconfirmed auto-matching invariant coverage)**
