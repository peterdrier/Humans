# Teams — G0 First Audit

**Section:** Teams · **Kind:** vertical (read-split reference implementation, PR #678) · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner Teams --tables teams,team_members,team_join_requests,team_join_request_state_history,team_role_definitions,team_role_assignments,team_early_entry_grants` → **0 violations**. Note `google_resources` is documented under Teams.md as a "Team Resources sub-aggregate" but is **actually owned and repo'd from the GoogleIntegration section** (`IGoogleResourceRepository`, `Humans.Infrastructure.Repositories.GoogleIntegration`) — correctly excluded from the ownership-violation check above and correctly not claimed as a Teams-owned table in the doc's "Owned tables" line. |
| 2 | One writer-service per table | PASS | `TeamRepository` (6 tables) + `GoogleResourceRepository` (the sub-aggregate, GoogleIntegration-owned) — no interceptor pattern found for Teams tables. |
| 3 | No EF entity leaks across boundary | **FAIL** | `ApplicationServiceEntityReadReturns.baseline.txt` carries **5 rows**: `ITeamService.GetAllTeamsAsync/GetByIdsWithParentsAsync/GetTeamByIdAsync/GetTeamEntityBySlugAsync → Team`, `GetUserTeamsAsync → TeamMember`. This is despite Teams being the **read-split reference implementation** — the doc is explicit that `ITeamServiceRead` (5 methods, DTO-only: `GetTeamsAsync`, `GetTeamAsync`, `GetTeamBySlugAsync`, `SearchAsync`, `GetUserCoordinatedTeamIdsAsync`) is the intended external contract, while the entity-returning methods above are meant to be **internal-only** on `ITeamService : ITeamServiceRead`. The baseline ratchet doesn't currently distinguish "internal use only" from "externally callable" — worth checking whether any of these 5 are in fact called cross-section (would be a live boundary violation, not just a ratchet artifact) versus genuinely Teams-internal (would mean the ratchet ought to exempt them, or they should move off the public interface entirely). Not resolved this pass. |
| 4 | No cross-section EF joins | **FAIL — corrected 2026-08-03** | No `CrossSectionEfJoinAnalyzer` baseline entries, but HUM0024 is **attribute**-allowlisted, not baseline-file-based, so that grep can't establish a pass. Four configs carry active `[Grandfathered("HUM0024", …)]` markers over the same User relationships predicate 5 lists as `[Obsolete]` navs: `TeamMemberConfiguration.cs` (`:30`), `TeamJoinRequestConfiguration.cs` (`:36,41`), `TeamJoinRequestStateHistoryConfiguration.cs` (`:33`), `TeamRoleAssignmentConfiguration.cs` (`:48`). |
| 5 | No `[Obsolete]` navs / `[Grandfathered]` / owned baseline rows | **PARTIAL** | 5 cross-domain navs are `[Obsolete]`-marked (not stripped): `TeamMember.User`, `TeamJoinRequest.User`, `TeamJoinRequest.ReviewedByUser`, `TeamRoleAssignment.AssignedByUser`, `TeamJoinRequestStateHistory.ChangedByUser`. This is **honestly documented, in-progress debt** (doc: "populated in memory by `TeamService`… Razor views and controllers still read through these navs under file-wide `#pragma warning disable CS0618` blocks… cleared when the consumers migrate to service-layer DTOs — tracked as the User-entity nav-strip follow-up"). Plus `DisplaySortInControllers.baseline.txt` carries 3 rows for `TeamRepository.cs` (`OrderBy`×1, `ThenBy`×2) — pre-existing sort-in-repository debt, baselined not fixed. Plus the 5 `ApplicationServiceEntityReadReturns` rows from predicate 3. Every item has a name and a rationale, but the sheer count (5 Obsolete navs + 3 sort-baseline + 5 entity-leak) makes this the largest G1.5 debt surface of the 9 sections. |
| 6 | Controllers thin (no HUM0031 grandfathers) | **FAIL (tracked)** | `TeamController.cs` (1 grandfather, "33 statements, cc 19") and `TeamAdminController.cs` (1 grandfather, "23 statements, cc 19") both carry `issueRef: "nobodies-collective/Humans#857"`. |
| 7 | `docs/sections/Teams.md` current | PASS (high confidence) | Very detailed, references PR #678 read-split, #824 sensitive-flag Admin-only gate, Early Entry (`EETeamAdmin`, `EarlyEntryEnabled`). Explicitly and honestly documents its own remaining nav-strip debt (unusual candor, matches what was found in code). |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | **FAIL** | `TeamRepositoryTests.cs:32` uses `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL (mixed)** | `TeamServiceTests.cs` and `CachingTeamServiceTests.cs` both reference `ServiceTestHarness` (DbContext-backed). `TeamServiceTests.cs` also directly demonstrates good invariant coverage (`CreateTeamAsync_ParentIsSystemTeam_Throws`, `AddSeededMemberAsync_SystemTeam_Throws`) despite the harness choice — the G3.2 violation is about test *architecture* (real DbContext vs mocked repo), not test *quality*. |
| 3 | Invariants/triggers each have a test | PASS (spot-check) | System-team manual-add/remove block explicitly tested (`AddSeededMemberAsync_SystemTeam_Throws`); parent-is-system-team rejection tested. Good signal for the section's highest-value invariants. |
| 4 | No skipped tests without issue ref | PASS (tentative) | No hits found. |
| 5 | Tests grouped under section | **PARTIAL** | `TeamServiceTests.cs`, `CachingTeamServiceTests.cs`, `CachingTeamServiceGetTeamDetailTests.cs`, `TeamRoleServiceTests.cs`, `TeamResourceServiceDeactivateTests.cs`, `TeamServiceSlugRaceTests.cs`, `TeamEarlyEntryProjectionTests.cs`, `SystemTeamSyncJobBarrioLeadsTests.cs` all sit flat at `tests/Humans.Application.Tests/Services/` root rather than in a `Services/Teams/` subfolder (a `Teams/` subfolder exists but appears thin next to the flat files). Same repo-wide pattern noted in Profiles/Users. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| 5 entity-returning `ITeamService` methods baselined as leaks despite the `ITeamServiceRead` split existing | `Interfaces.Teams.ITeamService` | Audit actual callers of `GetAllTeamsAsync`/`GetByIdsWithParentsAsync`/`GetTeamByIdAsync`/`GetTeamEntityBySlugAsync`/`GetUserTeamsAsync` — if all callers are Teams-internal, this may be a ratchet false-positive worth exempting; if any cross-section caller exists, it's a live boundary violation needing a DTO projection. | y |
| 5 `[Obsolete]`-marked cross-domain navs still read via `#pragma warning disable CS0618` | `TeamMember`, `TeamJoinRequest`×2, `TeamRoleAssignment`, `TeamJoinRequestStateHistory` | Already tracked as "the User-entity nav-strip follow-up" per the doc — no new action from this audit, just confirming it's real and current. | y |
| **Added 2026-08-03:** 4 HUM0024 configuration grandfathers | `TeamMemberConfiguration.cs`, `TeamJoinRequestConfiguration.cs`, `TeamJoinRequestStateHistoryConfiguration.cs`, `TeamRoleAssignmentConfiguration.cs` | The EF-configuration side of the nav-strip row above (predicate 4 was scored off baseline-file greps, which can't see attribute allowlisting). Retire the markers as part of the same nav-strip follow-up; the physical FK cuts are schema-queue work. | y (attribute work); FK cut is schema-queue work |
| 3 `DisplaySortInControllers` baseline rows on `TeamRepository.cs` | `src/Humans.Infrastructure/Repositories/Teams/TeamRepository.cs` (`OrderBy`, `ThenBy`×2) | Move display sort into the controller/view-model layer per `memory/architecture/display-sort-in-controllers.md`. | y |
| `TeamController`/`TeamAdminController` HUM0031 grandfathers | `src/Humans.Web/Controllers/` | Tracked under #857 (Lane 2 tonight). | y |

## Schema demolition queue

No dead columns/tables named in the doc beyond the nav-strip follow-up (which is code-shape, not schema). `google_resources` FK/ownership boundary with GoogleIntegration is clean and intentional, not debt.


**Added 2026-08-03 — cross-section FK cuts belong in this queue.** Retiring `[Obsolete]` navs or `[Grandfathered(HUM0024)]` markers is a code-shape change; it does **not** drop the physical constraint. Per the demolition inventory, this section owns **5** cross-section FKs across 4 tables: `team_members`, `team_role_assignments`, `team_join_requests` (×2) and `team_join_request_state_history` → `AspNetUsers`, behind the four HUM0024 configurations listed in the G1 gap list. All are cross-section FK cuts — without them listed here, a schema batch driven by this scorecard can complete while every cross-section database dependency survives.

## Headline

Largest G1.5 debt surface by row-count of any section audited — but all items are named/documented rather than silent.
