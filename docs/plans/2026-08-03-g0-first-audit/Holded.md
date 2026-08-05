# Holded — G0 First Audit

**Section:** Holded · **Kind:** vertical (thin external API client; owns no Humans tables in v1) · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | N/A | Doc: "Holded owns no Humans tables in v1." No repository exists for this section. |
| 2 | One writer-service per table | N/A | No owned tables. |
| 3 | No EF entity leaks across boundary | PASS | `IHoldedClient` surfaces plain DTOs from the Holded HTTP API, not EF entities. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | No Holded rows in any baseline file. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | PARTIAL | `NoDestructiveMigrationOps.baseline.txt` lists a "Peter-approved per-incident (2026-06-15): Holded ledger single-source redesign" set of `DropColumn`/`DropTable` entries (`holded_creditor_balances`, `holded_payments`). These tables are **not owned by the `Holded` section** per its own doc ("Holded owns no Humans tables") — they belong to the `Finance` section, which owns `holded_expense_docs` / `holded_ledger_lines` / `holded_creditor_contacts` per `docs/sections/Finance.md`. Flagging for cross-reference with Finance's own G1 audit rather than counting against Holded. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | Holded has no controllers/UI in v1 (doc: "None. Holded has no UI in v1."). |
| 7 | `docs/sections/Holded.md` current | PASS | Matches code: 11-method `IHoldedClient` surface enumerated in the doc lines up with the client's actual method set; "Evolution" section correctly narrates the Finance/Holded sync build-out as already-built, consistent with the Finance-owned baseline entries noted above. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | N/A | No repository, no tables. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | PASS | `tests/Humans.Application.Tests/Services/Holded/HoldedClientTests.cs`, `HoldedClientContactTests.cs`, `HoldedClientReadTests.cs` — grep for `HumansDbContext` across `Services/Holded/**`: no matches. Client tests exercise `HttpClient` behavior (retry/permanent-vs-transient classification), not EF. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively mapped, but the doc's core invariants (EUR-only, `HOLDED_API_KEY` env-var-only, transient-vs-permanent exception classification) look plausibly covered by the existing client test files by name; no line-level verification done. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` anywhere in `tests/`. |
| 5 | Tests grouped under section | PASS | `tests/Humans.Application.Tests/Services/Holded/**` — well grouped. |

## G1 gap list

No G1 gaps owned by Holded itself. One note flagged for cross-reference: the `NoDestructiveMigrationOps` baseline's Holded-named migration entries belong to Finance's table set, not this section's — Finance's own audit should account for them, not Holded's.

## G3 gap list

1. **Invariant→test mapping not completed (predicate 3).** The doc's core invariants (EUR-only,
   `HOLDED_API_KEY` env-var-only, transient-vs-permanent exception classification) look plausibly
   covered by the existing client test files by name, but no line-level verification was done.
   The gate ladder defines a section as reaching a gate only when every predicate holds, so an
   inferred mapping can't score as met. Fix: complete the mapping (a read, not new tests, unless
   it turns up real holes). No-migration-needed: **y**.

## Schema demolition queue

None for this section (no owned tables). Finance's forthcoming schema pass should account for the already-approved `holded_creditor_balances`/`holded_payments` drops recorded in the baseline.

## Headline

Cleanest possible shape: pure client wrapper, zero DB surface, well-tested. One cross-reference note: Finance's own audit should account for the Holded-named baseline entries.
