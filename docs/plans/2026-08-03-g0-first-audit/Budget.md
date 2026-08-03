# Budget — G0 First Audit

**Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | Owns `BudgetYear`/`BudgetGroup`/`BudgetCategory`/`BudgetLineItem`/`BudgetAuditLog`. `reforge ownership-violations --owner Budget --tables BudgetAuditLog,BudgetCategory,BudgetGroup,BudgetLineItem,BudgetYear` → 0. `reforge injected IBudgetRepository` → 1 consumer: `BudgetService`. |
| 2 | One writer-service per table (no interceptor workarounds) | PASS | Sole consumer of `IBudgetRepository` is `BudgetService` itself — no interceptor/second writer found. |
| 3 | No EF entity leaks; other sections consume DTOs via read surface | PASS (spot-checked) | `BudgetService` cross-section reads go through `ITeamServiceRead`/`IUserServiceRead` (proper read-interfaces, not full services) — `src/Humans.Application/Services/Budget/BudgetService.cs:19-24`. Outward-facing methods return DTOs (`BudgetYearSummarySnapshot`, `BudgetGroupSummarySnapshot`, `BudgetYearDetail`), not entities. Not all 44 methods individually verified — spot-check only. |
| 4 | No cross-section EF joins (zero baseline entries) | **FAIL** | Grepped all 5 `Baselines/*.baseline.txt` files for `Budget` — zero matches (correct, HUM0024 isn't baseline-file-based). But see predicate 5 — 3 configs carry an active `[Grandfathered("HUM0024", ...)]` marker, which **is** this analyzer's allowlist mechanism (attribute-based). |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]`/`[DontFix]` / owned baseline rows | **FAIL** | **Missed in the original pass** (grep only covered `Services/`, `Repositories/`, controllers, view models — not `Infrastructure/Data/Configurations/`): `BudgetLineItemConfiguration.cs:8`, `BudgetCategoryConfiguration.cs:8`, and `BudgetAuditLogConfiguration.cs:8` all carry `[Grandfathered(ruleId: "HUM0024", justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.", since: "2026-05-25", issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]` — the same marker found on 5 other sections' configs in this audit pass. Separately, `docs/sections/Budget.md` itself flags `BudgetLineItem.ResponsibleTeam` as a cross-domain nav that is **not yet** `[Obsolete]`-marked, still read directly by the Finance CategoryDetail view under `#pragma warning disable CS0618` — a live, doc-acknowledged debt item. |
| 6 | Controllers thin, no HUM0031 grandfathers | PASS | Grepped `BudgetController.cs` for `HUM0031`/`Grandfathered` — zero matches. |
| 7 | `docs/sections/Budget.md` exists and matches reality | PASS | **Correction:** `docs/sections/Budget.md` DOES exist (a prior glob call with brace-expansion syntax returned a false negative — confirmed via `ls` and direct `Read`; it's a 254-line, highly detailed doc: full 4-level hierarchy, `TicketingProjection`, restricted/ticketing group visibility rules). No drift found — the doc is self-aware, explicitly flagging both its own missing `BudgetArchitectureTests.cs` and the `ResponsibleTeam` nav debt (see predicate 5). It does not mention the 3 HUM0024 grandfathers, which is a minor omission shared by every section audited in this pass (no section doc documents its HUM0024 markers). |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests on real Postgres, zero EF-InMemory | **FAIL (worse: no coverage at all)** | No `BudgetRepositoryTests.cs` (or equivalent) exists anywhere under `tests/Humans.Application.Tests/Repositories/`. `BudgetRepository` — the sole writer/reader of all 5 Budget tables — has **zero dedicated repository-level test coverage**. |
| 2 | Service tests mock repo/`I*ServiceRead`, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | Grepping these files for the literal `HumansDbContext` is a false negative: they inherit their EF setup. `BudgetServiceTests` extends `ServiceTestHarness` and constructs a concrete repository. `ServiceTestHarness` (`tests/Humans.Application.Tests/Infrastructure/ServiceTestHarness.cs:54,71`) builds a real `HumansDbContext` over `.UseInMemoryDatabase(...)` and exposes it as `Db`/`DbFactory`, so a harness-derived test constructing a concrete repository is exactly the EF-InMemory service test this predicate forbids. Same false-negative pattern corrected across Auth, Budget, Camps, CityPlanning, Feedback, Governance and Consent in this pass. |
| 3 | Invariants/triggers from section doc each have a test | PARTIAL | Using the real `docs/sections/Budget.md`: "only one year Active at a time, auto-close on activate" — plausibly covered by `BudgetServiceTests.cs` given its breadth, not line-verified. "Every mutation generates a `BudgetAuditLog` entry" (append-only, §12) has some indirect coverage via `BudgetServiceTests.cs` and `BudgetAuditLog` references in `Humans.Integration.Tests/AccountMerge/*` fixtures, but no dedicated immutability test found, and `reforge audit-immutable` returned no Budget-tagged findings either way. Restricted/ticketing-group visibility rules (Forbid on drill-in) not traced to a specific test in this pass. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` found in `BudgetServiceTests.cs`. |
| 5 | Tests grouped under the section | **FAIL** | `BudgetServiceTests.cs` lives under generic `tests/Humans.Application.Tests/Services/`, not a `tests/.../Budget/` folder. No repository test exists to even mis-group. |

## G1 gap list

1. **3 HUM0024 cross-section EF join grandfathers** (`BudgetLineItemConfiguration`, `BudgetCategoryConfiguration`, `BudgetAuditLogConfiguration`) — where: `src/Humans.Infrastructure/Data/Configurations/Budget/*.cs:8`. No queued G2 items beyond the generic doc anchor. No-migration-needed: **y** (pending liveness verification).
2. **`IBudgetService` entity-leak coverage is spot-checked, not exhaustive** — 44 methods on the service, only a handful traced. Worth a full `reforge audit-surface` pass before declaring G1 predicate 3 fully met. No-migration-needed: **y**.
3. **`BudgetLineItem.ResponsibleTeam` nav not yet `[Obsolete]`-marked**, still read by the Finance CategoryDetail view under `#pragma warning disable CS0618` — self-flagged in `docs/sections/Budget.md`'s own "Touch-and-clean guidance". Fix: mark `[Obsolete]` and drop the pragma once the view is migrated to `ITeamService`. No-migration-needed: **y**.

## G3 gap list (folded in above, restated for action)

3. **`BudgetRepository` has zero test coverage** — the highest-priority G3 item across both of this fork's sections: no `BudgetRepositoryTests.cs` exists at all (not even an EF-InMemory one to convert). Fix: write repository tests against the shared Postgres fixture directly (skip the InMemory-then-convert detour other sections need). No-migration-needed: **y**.
4. **No dedicated immutability test for `budget_audit_logs` append-only invariant** — docstring claims append-only (§12) but no test enforces it (`reforge audit-immutable` silent on Budget). Fix: add a test (and/or confirm `reforge audit-immutable` config covers `BudgetAuditLog`). No-migration-needed: **y**.
5. **Budget tests not grouped under a section folder** — `Services/BudgetServiceTests.cs`. Fix: move into `tests/Humans.Application.Tests/Budget/` (created alongside the new repository tests from gap #3). No-migration-needed: **y**.


**Added 2026-08-03 — harness-inherited EF-InMemory (G3.2).** `BudgetServiceTests` extend `ServiceTestHarness`, which stands up a real `HumansDbContext` over `.UseInMemoryDatabase(...)`; the original pass missed this because it grepped for a literal `HumansDbContext` the files never name. Fix: convert to `Substitute.For<IBudgetRepository>()` per #766, or move these off the harness. No-migration-needed: **y**.

## G2 queue notes

Budget hierarchy (`BudgetYear → BudgetGroup → BudgetCategory → BudgetLineItem`) plus `BudgetAuditLog` reads as a clean, purpose-built schema — no dead columns spotted in this pass. `docs/features/budget/budget.md` explicitly scopes it as "not an accounting system" / "not real-time," so no schema growth pressure expected before G2.

**Corrected 2026-08-03** — "nothing destructive queued" conflated *dead columns* with *all* G2 work. Absence of dead columns says nothing about FK cuts or renames, and the demolition inventory in this same commit records four schema actions (this scorecard's own G1 findings already acknowledge all three HUM0024 configurations):

- **Cut `budget_categories.TeamId → teams`** — live nav `HasOne(c => c.Team)` (`BudgetCategoryConfiguration.cs:28`).
- **Cut `budget_line_items.ResponsibleTeamId → teams`** (`BudgetLineItemConfiguration.cs`).
- **Cut `budget_audit_logs.ActorUserId → AspNetUsers`** — live nav `HasOne(a => a.ActorUser)` (`BudgetAuditLogConfiguration.cs:28`).
- **Rename `ticketing_projections` → `budget_ticketing_projections`** (`TicketingProjectionConfiguration.cs:11`) — the one table in the section without the `budget_` prefix.
