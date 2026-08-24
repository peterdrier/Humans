# Finance — G0 First Audit

Section: Finance · Kind: vertical · Audited 2026-08-03 @ 5a9bbe198

**Scope note:** `reforge.surface-score.json`'s `Finance` block maps `repositoryInterfaces: [IHoldedRepository]` / `serviceInterfaces: [IHoldedFinanceService]` and includes `Holded*`/`IHolded*` in its symbols — i.e. the JSON treats "Finance" as owning the Holded-integration surface. The G0 section tracker (the Q3 transition plan, historical/deleted) listed only `Finance` as a row (no separate `Holded` row). However, `docs/sections/Holded.md` documents a **separate, already-built section** — a thin `IHoldedClient`/`HoldedClient` HTTP-only surface with **zero owned tables** ("Holded owns no Humans tables in v1"), consumed by Expenses and by Finance's `HoldedFinanceService`. This audit scores the tracker's `Finance` row (the treasurer surface + `HoldedExpenseDoc`/`HoldedCategoryMap`/`HoldedSyncState`/`HoldedLedgerLine`/`HoldedCreditorContact` tables + `IHoldedFinanceService`), and separately notes Holded-the-HTTP-client's health since the JSON's symbol glob (`Holded*`) sweeps it in. **Recommend the G0 tracker gain an explicit `Holded` row** since it is a distinct, already-implemented horizontal-ish utility section with its own doc, not a Finance sub-concept — flag for Peter.

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository | PASS | `reforge ownership-violations --owner Finance --tables holded_expense_docs,holded_category_map,holded_sync_states,holded_ledger_lines,holded_creditor_contacts` → `0 ownership-violations`. Holded-the-client owns zero tables (n/a). |
| 2 | One writer-service per table (no interceptor workarounds) | PASS | `reforge injected IHoldedRepository` → single consumer `HoldedFinanceService` (`src/Humans.Application/Services/Finance/HoldedFinanceService.cs:18`). No interceptor pattern documented or found. |
| 3 | No EF entity leaks across the boundary | PASS | `docs/sections/Finance.md` §"Owned repository": "No cross-domain navs: `BudgetCategoryId` and `HoldedCreditorContact.UserId` are FK-only, no navigation property." Cross-section read surface to Expenses (`GetCreditorStatusAsync`, `GetCreditorLedgerAsync`) returns `HoldedCreditorStatus`/`HoldedPaymentInfo` DTOs, not entities. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | Grep for `Grandfathered`/`Obsolete` across `Holded*.cs` entities and configs → zero matches. |
| 5 | No `[Obsolete]` cross-section navs, no `[Grandfathered]`, no owned baseline rows | PASS | Same grep as #4; docs' own "Current violations" section states "None." |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | `grep HUM0031 src/Humans.Web/Controllers/Finance*.cs` → zero matches. |
| 7 | `docs/sections/<Section>.md` exists and matches reality | PASS | `Finance.md` and `Holded.md` both exist, both current and detailed (Feature 1/Feature 2 phased build, attribution chain, ledger cache). Matches code structure verified above. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests use real Postgres, zero EF-InMemory | FAIL | Confirmed by solution-wide grep (`tests/**`) for `HoldedRepositoryTests`/`IHoldedRepository`/`HoldedRepository` outside `Humans.Application.Tests/Finance/` and `Services/Holded/` → **no repository-level test exists at all** for `IHoldedRepository`. Zero EF-InMemory usage trivially holds (nothing to violate it), but the predicate's intent — real-Postgres repository coverage — is unmet: there is no coverage of `holded_expense_docs`/`holded_category_map`/`holded_sync_states`/`holded_ledger_lines`/`holded_creditor_contacts` CRUD at the repository layer at all. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | PASS | No `HumansDbContext` match in `tests/Humans.Application.Tests/Finance/`. `HoldedFinanceServiceTests.cs` mocks `IHoldedRepository` (confirmed via grep match on "HoldedRepository" string, consistent with a `Mock<IHoldedRepository>` pattern). |
| 3 | Invariants/triggers each have a test (spot-check) | PASS (spot-check) | Attribution chain covered: `NormalizeTag_strips_separators_and_lowercases`, Account-match (`r.Source.Should().Be(HoldedMatchSource.Account)`), Tag-match (`r.Source.Should().Be(HoldedMatchSource.Tag)`) in `HoldedMatcherTests.cs`. Not directly spot-checked: additive-only provisioning, `ApprovedAt IS NOT NULL` actuals filter, ledger-line idempotent upsert on `(EntryNumber, Line)`. |
| 4 | No skipped tests without an issue ref | PASS | `Skip\s*=` grep on `tests/Humans.Application.Tests/Finance/` → no matches. |
| 5 | Tests grouped under the section | PASS | `tests/Humans.Application.Tests/Finance/` (2 files: `HoldedFinanceServiceTests.cs`, `HoldedMatcherTests.cs`) + `tests/Humans.Application.Tests/Services/Holded/` (3 files: `HoldedClientContactTests.cs`, `HoldedClientReadTests.cs`, `HoldedClientTests.cs` — these test the Holded HTTP client, consistent with Holded being a distinct section per the scope note above). Both groupings are folder-scoped. |

## G1 Gap List

None — Finance is clean on G1. (Tech debt already self-documented and out of scope for G1: `IHoldedFinanceService` depends on the full `IBudgetService` rather than a future `IBudgetServiceRead`; `FinanceController` similarly consumes full `IBudgetService`/`ITicketServiceRead` — both are read-heavy cross-section calls through proper service interfaces already, just not read-split yet. Not a G1 violation since it already routes through the service layer.)

## G3 Gap List

1. **No repository-level test exists for `IHoldedRepository`** — where: expected at `tests/Humans.Application.Tests/Repositories/Finance/HoldedRepositoryTests.cs` or similar; confirmed absent solution-wide. Suggested fix: add a real-Postgres repository test for `holded_expense_docs`/`holded_category_map`/`holded_sync_states`/`holded_ledger_lines`/`holded_creditor_contacts` CRUD + the ledger idempotent-upsert-on-`(EntryNumber, Line)` invariant. No-migration-needed: **y**.

## Schema demolition queue (light)

- `docs/sections/Finance.md` explicitly documents `20260525_HoldedCreditorData` as "superseded" by the ledger single-source redesign — confirm this migration/its dropped tables (`holded_creditor_balances`, `holded_payments`, seen in `NoDestructiveMigrationOps.baseline.txt`) are fully retired with no lingering references; looks already clean per the doc.
- Soft boundary noted in doc: `TicketingProjection`/`TicketingBudgetService` conceptually belong to Finance-adjacent "actuals materialization" but live in Budget — explicitly flagged by the doc as deliberate, not an active violation; leave as-is unless Budget's own audit disagrees.
- **Process note for Peter:** consider adding a `Holded` row to the G0 section tracker table, separate from `Finance`, since `docs/sections/Holded.md` describes it as an already-built, distinct, table-less section (pure HTTP client) rather than a Finance sub-concept.

