# Guide — G0 First Audit

**Section:** Guide · **Kind:** vertical (crosscut-shaped: read-only content service, owns no tables) · **Audited:** 2026-08-03 @ 5a9bbe198

Guide owns zero database tables (content is fetched from GitHub, cached in `IMemoryCache`), so G1 predicates 1/2/4 are vacuously satisfied — there is no table to violate ownership on.

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | N/A | No owned tables. |
| 2 | One writer-service per table | N/A | No owned tables. |
| 3 | No EF entity leaks across boundary | PASS | No entities owned. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | No baseline entries for Guide in any of the 5 baseline files. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | **FAIL, but already fixed in code — doc is stale** | `docs/sections/Guide.md` "Current violations" section claims `GuideRoleResolver.cs:57` does `_db.TeamMembers.AnyAsync(...)` (a raw cross-section `DbContext` read). Read the actual file: `src/Humans.Infrastructure/Services/GuideRoleResolver.cs` — it takes `ITeamServiceRead` in its constructor and resolves coordinator status from the cached `TeamInfo` snapshot (`teamService.GetTeamsAsync(...)`, line 53). **The violation the doc describes no longer exists in code.** This is a doc-freshness gap, not a code gap. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | No `Grandfathered` hits on `GuideController.cs`. |
| 7 | `docs/sections/Guide.md` current | **FAIL** | See predicate 5 — the doc's "Current violations" and "Touch-and-clean guidance" sections describe a cross-section DbContext read that has already been fixed (now goes through `ITeamServiceRead`). Needs a freshness-sweep pass to remove the stale violation callout and update the Architecture section's "(B) Partially migrated" status if this was the last item. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | N/A | No repository, no tables. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | PASS | `tests/Humans.Application.Tests/Services/GuideRoleResolverTests.cs` mocks `ITeamService` via NSubstitute (`Substitute.For<ITeamService>()`), consistent with the fixed code — confirms the doc, not the code, is out of date. `GuideContentServiceTests.cs`, `GuideFilterTests.cs`, `GuideRendererTests.cs`, `GuideMarkdownPreprocessorTests.cs`, `GuideHtmlPostprocessorTests.cs`, `GuideRolePrivilegeMapTests.cs` — no DB dependency, N/A. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively mapped; the within-file Coordinator-superset rule and role-parenthetical scoping look covered by `GuideFilterTests.cs` / `GuideRolePrivilegeMapTests.cs` by name, but no line-level check was done. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` anywhere in `tests/`. |
| 5 | Tests grouped under section | PASS | All Guide tests sit under `tests/Humans.Application.Tests/Services/Guide*.cs` — reasonably grouped by filename prefix, though not in a dedicated `Guide/` subfolder like the newer sections. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| Doc claims a fixed violation still exists | `docs/sections/Guide.md` §"Current violations" / §"Touch-and-clean guidance" | Freshness-sweep pass: remove the stale `_db.TeamMembers` callout, confirm no other violations remain, and consider promoting Status from "(B) Partially migrated" to "(A) Migrated" if this was the only item. | y |
| No architecture test file exists for Guide | `tests/Humans.Application.Tests/Architecture/GuideArchitectureTests.cs` (absent) | Doc already flags this ("Add one when migrating"). Low priority since section owns no tables/EF surface, but would pin the no-DbContext-in-services shape going forward. | y |

## G3 gap list

1. **Invariant→test mapping not completed (predicate 3).** The within-file Coordinator-superset
   rule and role-parenthetical scoping look covered by `GuideFilterTests.cs` /
   `GuideRolePrivilegeMapTests.cs` by name, but no line-level check was done. The gate ladder
   defines a section as reaching a gate only when every predicate holds, so an inferred mapping
   can't score as met. Fix: complete the mapping (a read, not new tests, unless it turns up real
   holes). No-migration-needed: **y**.

## G2 queue notes

None — no owned tables to demolish/rename.

## Headline

`docs/sections/Guide.md` needs a freshness-sweep pass, not a code fix.
