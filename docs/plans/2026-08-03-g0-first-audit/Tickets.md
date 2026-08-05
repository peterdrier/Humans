# Tickets — G0 First Audit

**Section:** Tickets · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS (corrected evidence) | `reforge ownership-violations --owner Tickets --tables ticket_orders,ticket_attendees,ticket_sync_states,ticket_transfer_requests` → **0 violations** — predicate itself still holds, but the evidence text was wrong: there are **two** repositories in-section, not one. `ITicketRepository` (`ticket_orders`/`ticket_attendees`/`ticket_sync_states`) and a separate `ITicketTransferRepository`/`TicketTransferRepository` (`Interfaces/Repositories/ITicketTransferRepository.cs`, `Infrastructure/Repositories/Tickets/TicketTransferRepository.cs`) owning `ticket_transfer_requests`. Each table still maps to exactly one repository, so the predicate itself is unaffected — but see predicate 2. |
| 2 | One writer-service per table | **FAIL — corrected 2026-08-03** | `ticket_transfer_requests` has two writer-services, not one: `TicketTransferService` (its primary owner, injects `ITicketTransferRepository` as `transferRepo`) **and** `TicketSyncService.ReassignAsync` (`TicketSyncService.cs:193-200`), which calls `transferRepository.ReassignUserAsync` (`:197`) as part of the account-merge fold, alongside `ticketRepository.ReassignToUserAsync` (`:196`) for the other two tables. **Two further tables fail the same predicate (added 2026-08-03)** — the original audit stopped at `ticket_transfer_requests`. `ticket_attendees` has **three** writer-services, all calling `UpsertAttendeesAsync`: `AttendeeContactImportService` (`:106`), `TicketSyncService` (`:127`) and `TicketTransferService` (`:310,443,456`). `ticket_sync_states` has two: `TicketQueryService.ResetStaleRunningStateAsync` (`:97`) alongside `TicketSyncService`'s persist/reset operations. So the "all route through the one repository" evidence was about repository count, not writer-service count, and fails on three of the section's four tables. |
| 3 | No EF entity leaks across boundary | PASS | No `Ticket*` rows in `ApplicationServiceEntityReadReturns.baseline.txt`. `MatchedUser` navs are stripped from both `TicketOrder` and `TicketAttendee` (FK-only, joined in-memory via `IUserService.GetByIdsAsync`). |
| 4 | No cross-section EF joins | **FAIL — corrected 2026-08-03** | No `CrossSectionEfJoinAnalyzer` baseline entries, but HUM0024 is **attribute**-allowlisted, so the absence of baseline rows proves nothing. `TicketOrderConfiguration.cs` (`:64`) and `TicketAttendeeConfiguration.cs` (`:59`) both carry active `[Grandfathered("HUM0024", …)]` markers over `HasOne<User>()` on `MatchedUserId` → `AspNetUsers`. The demolition inventory already queues both FK cuts. Note the absence of entity nav properties does not satisfy this predicate — the DB-level join and its allowlist marker are what it measures. |
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
| **Added 2026-08-03:** 2 HUM0024 configuration grandfathers | `TicketOrderConfiguration.cs:64`, `TicketAttendeeConfiguration.cs:59` | Both grandfather `HasOne<User>()` on `MatchedUserId` → `AspNetUsers` (see predicate 4). Verify liveness, then retire the attributes; the physical FK cuts are already queued in the demolition inventory. | y (attribute work); FK cut is schema-queue work |
| **Added 2026-08-03:** Multiple writer-services on `ticket_attendees` and `ticket_sync_states` | `AttendeeContactImportService`/`TicketSyncService`/`TicketTransferService` (all `UpsertAttendeesAsync`); `TicketQueryService.ResetStaleRunningStateAsync` alongside `TicketSyncService` | Fixing only the transfer-service conflict below leaves G1.2 failing on two more tables. Decide per table whether to funnel writes through one owning service or record an explicit accepted exception. | y |
| **Added 2026-08-03:** Second writer-service on `ticket_transfer_requests` | `TicketSyncService.ReassignAsync` (`TicketSyncService.cs:197`) alongside `TicketTransferService` | Both call `ITicketTransferRepository`. The account-merge fold reassigning transfer requests belongs conceptually to the merge flow already living in `TicketSyncService`, but it's still a second writer-service against a table `TicketTransferService` otherwise owns — either route the fold through `ITicketTransferService`'s own interface or explicitly document this as an accepted account-merge-fold exception (same shape as Campaigns' `ReassignGrantsToUserAsync`). | y |

## G3 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| `TicketRepositoryTests.cs` / `TicketRepository_OrderDriftTests.cs` use EF-InMemory | `tests/Humans.Application.Tests/Repositories/` | Migrate to shared Postgres fixture per #764/#766. | y |
| `TicketQueryServiceTests.cs`/`TicketSyncServiceTests.cs`/`TicketSyncServiceNullOrderTests.cs` use `ServiceTestHarness` (DbContext-backed) instead of mocked `ITicketRepository` | `tests/Humans.Application.Tests/Services/` | Migrate to `Substitute.For<ITicketRepository>()` pattern, matching Store's clean example. | y |
| Auto-matching (`NormalizingEmailComparer`) invariant test presence unconfirmed | `TicketSyncServiceTests.cs` | Confirm coverage exists (search under alternate naming) or add an explicit test for the "collision among verified emails leaves both unmatched + LogError" edge case — this is a data-integrity-error path that's easy to leave untested since it should never trigger in practice. | y |

## Schema demolition queue

`TicketTransferRequest.VendorStepsJson` is unused, dormant, named as pending a post-soak drop PR — already a tracked demolition-inventory item.


**Added 2026-08-03 — cross-section FK cuts belong in this queue.** Retiring `[Obsolete]` navs or `[Grandfathered(HUM0024)]` markers is a code-shape change; it does **not** drop the physical constraint. Per the demolition inventory, this section owns **2** cross-section FKs across 2 tables: `ticket_orders.MatchedUserId` and `ticket_attendees.MatchedUserId` → `AspNetUsers`, behind the two HUM0024 configurations listed in the G1 gap list. All are cross-section FK cuts — without them listed here, a schema batch driven by this scorecard can complete while every cross-section database dependency survives.

## Headline

Largest sort-in-repository baseline in the batch (`TicketRepository.cs`).
