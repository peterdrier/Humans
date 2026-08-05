# Cantina — G0 First Audit

**Section:** Cantina · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

**Headline finding:** Cantina owns **zero tables**. It has no `Infrastructure/Repositories/Cantina` path, no `repositoryInterfaces` entry in `reforge.surface-score.json`, and `CantinaRosterService` injects no repository at all — it is a pure read-composition service over `IShiftManagementService` (Shifts, full service) and `IUserServiceRead` (Users, correct read interface). This changes how most G1 predicates apply — several are vacuously satisfied (no tables ⇒ nothing to leak/join/violate at the data layer) but the *service-layer* cross-section access pattern is the real thing to evaluate.

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository, in-section | **N/A (vacuous PASS)** | No `ICantinaRepository` exists; no `Infrastructure/Repositories/Cantina/**` path in `reforge.surface-score.json`. Cantina owns no tables — see headline finding. Open question for the G0 tracker: should Cantina even carry a G1–G5 ladder, or be reclassified as a thin composition layer? (Surfaced per the Q3 plan's open "whether Cantina/Scanner stay separate" item — not a decision for this audit.) |
| 2 | One writer-service per table | **N/A** | No tables owned. |
| 3 | No EF entity leaks across the boundary | **PASS** | `CantinaRosterService` consumes `IUserServiceRead` (DTO-only, correct) and `IShiftManagementService.GetOnSiteUserIdsForDayAsync` which returns `IReadOnlyList<Guid>` (`src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs:358`) — a primitive, not an EF entity. No entity leak. |
| 4 | No cross-section EF joins (zero baseline entries) | **PASS** | No `CrossSectionEfJoin` baseline file exists at all (zero grandfathered exceptions solution-wide); grep of the 5 existing baseline files for `Cantina` → no matches. |
| 5 | No `[Obsolete]` cross-section navs, no `[Grandfathered]`, no baseline rows owned by Cantina | **PASS** | Grep for `Grandfathered`/`[Obsolete]` in `CantinaController.cs` and `src/Humans.Application/Services/Cantina/**` → no matches. |
| 6 | Controllers thin — no HUM0031 grandfathers | **PASS** | `CantinaController.cs` delegates to `ICantinaRosterService.GetWeeklyRosterAsync` + a view-model assembler (`CantinaRosterAssembler.WithSortedPeople`); no `Grandfathered("HUM0031"...)` hits. |
| 7 | `docs/sections/Cantina.md` exists and matches reality | PARTIAL | **Correction:** `docs/sections/Cantina.md` DOES exist (a prior glob call with brace-expansion syntax returned a false negative — confirmed via `ls` and direct `Read`; it's a 96-line doc: on-site/arrival-day definitions, `MedicalConditions` exclusion invariant, architecture notes). It correctly documents the no-owned-tables shape and the cross-section read pattern (`IShiftManagementService`, `IUserServiceRead`). **But** it cites `tests/Humans.Application.Tests/Services/Cantina/CantinaRosterServiceTests.cs and CantinaAccessServiceTests.cs` as pinning the aggregation rules and access gate — `CantinaAccessServiceTests.cs` does **not exist** anywhere in the repo (the real files are `CantinaDailyRosterServiceTests.cs`/`CantinaRosterServiceTests.cs` under `Application.Tests/Services/Cantina/`, and `CantinaCsvWritersTests.cs`/`CantinaRosterAssemblerTests.cs` under `Web.Tests/Cantina/`). **Second stale reference (added 2026-08-03):** the same sentence also claims "the cross-section read is additionally pinned by `CrossSectionRepositoryInjection.baseline.txt`" — no such baseline file exists anywhere in the repo (only the analyzer `CrossSectionRepositoryInjectionAnalyzer.cs` and its own tests). Both references must be corrected together; fixing only the test filename leaves the doc stale and this predicate unresolved. |
| — | **Not in the checklist but load-bearing:** cross-section full-service injection | **PARTIAL** | Both `CantinaRosterService` (`src/Humans.Application/Services/Cantina/CantinaRosterService.cs:25`) and `CantinaController` (`src/Humans.Web/Controllers/CantinaController.cs:26,32`) inject `IShiftManagementService` — the **full** Shifts service interface, not a read-split. Per the hard rules, cross-section service calls "must be via the `I<section>ServiceRead` when available" — it isn't available here (Shifts only has `IVolunteerTrackingServiceRead`, no `IShiftManagementServiceRead`), so this isn't a rule *violation* in the strict sense, but it is a gap: Cantina only calls one read-only method (`GetOnSiteUserIdsForDayAsync`) off a service whose full surface includes writes. The `CrossSectionFullServiceInjectionAnalyzer` (HUM0032, `tests/Humans.Analyzers.Tests/CrossSectionFullServiceInjectionAnalyzerTests.cs`) only fires when a read-split interface *exists* and a caller bypasses it in favor of the full interface — since Shifts has no such split for `IShiftManagementService`, the analyzer can't catch this case at all. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests real Postgres, zero EF-InMemory | **N/A** | No repository, no repository tests. |
| 2 | Service tests mock repo/`I…ServiceRead`, zero `HumansDbContext` | **PASS** | `CantinaRosterServiceTests.cs:35-36` — `Substitute.For<IShiftManagementService>()` and `Substitute.For<IUserServiceRead>()`; grep for `HumansDbContext`/`UseInMemoryDatabase` under `tests/Humans.Application.Tests/Services/Cantina/` → no matches. |
| 3 | Invariants/triggers from `docs/sections/Cantina.md` each have a test | PASS (spot-check) | Using the real doc: "`MedicalConditions` never surfaced, DTOs have no such property" — structurally enforced (no field on `RosterPersonDto`/`DailyPersonRowDto`), plausibly covered by `CantinaRosterServiceTests.cs`. "On-site = Confirmed signup + arrival day (firstConfirmedShiftDay − 1)" and "weekly aggregates over unique humans, not summed per day" — both look like exactly what `CantinaDailyRosterServiceTests.cs`/`CantinaRosterServiceTests.cs` would target given their names; not line-verified. |
| 4 | No skipped tests without an issue ref | **PASS** | Grepped `*Cantina*Tests.cs` for `Skip\s*=` → no matches. |
| 5 | Tests grouped under the section | **PASS** | `tests/Humans.Application.Tests/Services/Cantina/CantinaRosterServiceTests.cs` and `CantinaDailyRosterServiceTests.cs` — both foldered under a `Cantina/` test namespace. No architecture test file exists for Cantina (see gap list). |

## G1 gap list

1. **Not a gate gap — a maintainability suggestion.** **No `CantinaArchitectureTests.cs`** exists to pin Cantina's shape (unlike Agent/AuditLog/Auth/Calendar/Campaigns/Camps, which all have one). Given Cantina owns no tables, there's little for a repository-pinning test to enforce, but a small architecture test could at least pin "no repository is ever injected into Cantina types" as a tripwire against future table ownership creeping in unnoticed. Fix: add `tests/Humans.Application.Tests/Architecture/CantinaArchitectureTests.cs`. No migration needed: **y**.
2. **`docs/sections/Cantina.md`'s "Architecture test" sentence has two invalid references** — the non-existent `CantinaAccessServiceTests.cs`, **and** a claim that the cross-section read is pinned by `CrossSectionRepositoryInjection.baseline.txt`, which does not exist either (corrected 2026-08-03; only the analyzer of that name exists). Fix: correct the test filenames to the real ones (`CantinaDailyRosterServiceTests.cs`/`CantinaRosterServiceTests.cs`, plus `CantinaCsvWritersTests.cs`/`CantinaRosterAssemblerTests.cs` under `Web.Tests/Cantina/`) and either drop the baseline claim or replace it with the analyzer that actually enforces the rule. No migration needed: **y**.
3. **Cross-section full-service dependency on `IShiftManagementService`** in both `CantinaRosterService` and `CantinaController`, with no read-split available to narrow it to. Fix belongs primarily to the **Shifts** section: add `IShiftManagementServiceRead` (mirroring the `IVolunteerTrackingServiceRead` pattern already in place) exposing at minimum `GetActiveAsync` (event settings) and `GetOnSiteUserIdsForDayAsync`; then narrow both Cantina call sites to the read interface. Cross-section coordination required — flag for whoever picks up Shifts' G1 audit. No migration needed: **y**.

## G3 gap list

1. **Invariant→test mapping not completed (predicate 3).** Predicate 3's own evidence says the
   mapping is a spot-check — coverage is inferred from test filenames and shape, with no
   line-level confirmation. The gate ladder defines a section as reaching a gate only when every
   predicate holds, so an inferred mapping can't score as met. Fix: complete the mapping (a read,
   not new tests, unless it turns up real holes). No-migration-needed: **y**.

Two items that were once listed here are **not** G3 gaps and stay struck: *"no architecture test
for Cantina"* (no G3 predicate requires one — see the G1 note) and *"no canonical invariant doc to
test against"* (false; `docs/sections/Cantina.md` exists per G1.7).

## Schema demolition queue

- Nothing — Cantina owns no tables, so there's no demolition-inventory surface (no dead columns, no cross-section FKs, no rename work) for this section specifically.
- **Resolved 2026-08-03 — the folding question is closed, not open.** This note originally asked whether Cantina might end up folded into whichever section owns the read surfaces it depends on. The confirmed section inventory (`2026-08-03-proposed-frozen-section-inventory.md`) rules that Cantina **is** its own section, and the `sections-are-logical-units` rule states that table-less sections stay on the ladder with table-keyed predicates marked N/A. Leaving the question open would reopen a frozen decision and invite later tracker work to undo the canonical taxonomy. The still-valid half stands: Cantina owns no tables, so there is no schema or DbContext work for this section.
