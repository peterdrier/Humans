# Store — G0 First Audit

**Section:** Store · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

> Note: nobodies-collective/Humans#815 (Store auth-threading removal) is **in flight in a parallel lane tonight**. This audit records the architecture as observed at read time (resource-based `StoreOrderAuthorizationHandler` + `StoreOrderOperationRequirement`) — re-check once that lane lands, as it may change the write paths and their test coverage.

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner Store --tables store_products,store_orders,store_order_lines,store_payments,store_invoices,store_treasury_sync_state` → **0 violations**. Single `IStoreRepository` / `StoreRepository`. |
| 2 | One writer-service per table | PASS | `StoreService` is the sole owning service; no interceptor pattern found. |
| 3 | No EF entity leaks across boundary | PASS | No `Store*` entries in `ApplicationServiceEntityReadReturns.baseline.txt`. Cross-section linkage fields (`CampSeasonId`, `TeamId`, `ProductId`, `AddedByUserId`, `RecordedByUserId`, `IssuedByUserId`) are bare `Guid`/`Guid?` — no navigation properties, born clean per the doc. |
| 4 | No cross-section EF joins | PASS | No `CrossSectionEfJoinAnalyzer` baseline entries. |
| 5 | No `[Obsolete]` navs / `[Grandfathered]` / owned baseline rows | PASS | None found. |
| 6 | Controllers thin (no HUM0031 grandfathers) | PASS | None of `StoreController`/`StoreAdminController`/`StoreStripeWebhookController` appear in the HUM0031 grep hit list. |
| 7 | `docs/sections/Store.md` current | **PARTIAL — one stale claim found** | The doc states under Architecture: *"Architecture test: none yet. `tests/Humans.Application.Tests/Architecture/StoreArchitectureTests.cs` is not present — gap to fill in a follow-up."* This is **incorrect** — `StoreArchitectureTests.cs` **does exist** (2 tests: `StoreService_DoesNotReferenceEntityFrameworkCore`, `StoreRepository_ImplementsIStoreRepository`, styled explicitly as "mirrors `TeamsArchitectureTests`/`ShiftManagementArchitectureTests`"). The doc needs a one-line freshness fix once confirmed non-transient (i.e. not itself an artifact of tonight's #815 lane). |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | **FAIL** | `StoreRepositoryTests.cs:18` uses `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **PASS** | `StoreServiceTests.cs:23`, `StoreServiceTeamOrdersTests.cs:26`, `StoreServiceStripeReconciliationTests.cs:21` all construct `IStoreRepository _repo = Substitute.For<IStoreRepository>()` — properly mocked, zero `ServiceTestHarness`/`HumansDbContext` hits. This is the cleanest G3.2 result of any section-with-tables audited in this batch. |
| 3 | Invariants/triggers each have a test | PASS (spot-check) | The single-counterparty invariant is tested (`StoreServiceTests.cs` `Counterparties`/`CounterpartyType` assertions); team-order non-billable rejection has a dedicated test file (`StoreServiceTeamOrdersTests.cs`). Good coverage signal given the doc's unusually precise invariant list (async-payment state machine, deadline gate, etc.) — not every invariant individually traced this pass. |
| 4 | No skipped tests without issue ref | PASS (tentative) | No hits found. |
| 5 | Tests grouped under section | PASS | All three service test files + `StoreRepositoryTests.cs` are correctly scoped (`Services/Store/`, `Repositories/`). |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| `docs/sections/Store.md` claims `StoreArchitectureTests.cs` doesn't exist; it does | `docs/sections/Store.md` Architecture section | One-line doc fix: remove the "gap to fill" note, or replace it with an accurate description of the 2 existing tests (and note if further coverage — e.g. pinning the decorator-less decision, or the `[Section("Store")]` repository tag — is still wanted). | y |

## G3 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| `StoreRepositoryTests.cs` uses EF-InMemory | `tests/Humans.Application.Tests/Repositories/StoreRepositoryTests.cs` | Migrate to the shared Postgres fixture pattern (once identified/confirmed elsewhere in the codebase — no reference "good" example was located in this pass; worth checking with whoever owns #764/#766). | y |

## Schema demolition queue

`StoreOrder.Label` — `[Obsolete]`, retained but unused after #816, never set on write. Already a named demolition-inventory item in the doc. Phase 5 (manual payments, invoice issuance, treasury sync) is unimplemented (`NotSupportedException("Phase 5")`) — not a G1/G3 concern, just incomplete feature scope.
