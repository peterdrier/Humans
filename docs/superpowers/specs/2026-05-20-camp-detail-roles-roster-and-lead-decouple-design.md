# Camp detail page: Roles + Roster cards, and decoupling from legacy `camp_leads`

**Date:** 2026-05-20
**Branch:** `feat/camp-roster-roles`
**Section:** Camps
**Related:** `nobodies-collective/Humans#753` (CampLead → CampRoleAssignment, PR #657), `nobodies-collective/Humans#774` (deferred physical drop)

## Problem

The camp detail page `/Barrios/{slug}` (`CampController.Details`) still renders its **Leads** card from the legacy `camp_leads` table (`BuildLeadSummariesAsync(camp.Leads)`), while the management page `/Barrios/{slug}/Edit/Members` renders leads/roles from the **new** `CampRoleAssignment` system (`campRoleService.BuildPanelAsync`). Result: a lead added through the new Roles panel (e.g. "Hannah") appears on `/Edit/Members` but **not** on the public detail page.

Authorization (`IsUserCampLeadAsync` → `CampAuthorizationHandler`) and the Barrio-Leads system-team sync were already migrated to the role system in #657, with legacy fallbacks retained. But a cluster of **display, notification, directory, dev-seed, account-merge, and camp-creation** paths still read from or write to `camp_leads`.

## Goals

1. **Fix the detail page** to source leads/roles from `CampRoleAssignment` (the new way), consistent with `/Edit/Members`.
2. **Add two cards** to the detail page (modeled on `/Teams/{slug}` → `_RosterSection.cshtml`):
   - **Roles** (right column, replacing the Leads card)
   - **Roster** (left column, above About)
3. **Decouple the full lead lifecycle from `camp_leads`** — creation, reads, account-merge, dev-seed — and **strip the transitional legacy fallbacks**, so no live code path depends on the legacy table.

## Non-goals (deferred to `nobodies-collective#774`)

Only the **physical** removal is deferred, because it changes the EF snapshot/schema and is governed by the `no-column-drops-for-decoupling` hard rule (drop waits for a separate PR after prod verification):

- The `DropCampLeads` EF migration.
- Deleting the `CampLead` entity, `CampLeadRole` enum, `CampLeadConfiguration`, and the `Camp.Leads` navigation / `CampLeads` DbSet.
- Retiring `SeedSystemRolesAndMigrateLeadsAsync` + `EnsureActiveMemberForMigrationAsync` (one-time migration plumbing).
- Final `git grep CampLead` zero-hit + docs.

**The dividing line:** *anything whose removal changes the EF model snapshot → #774; everything else → this PR.* After this PR the legacy entity/table remain mapped but **orphaned** (no reads, no writes).

**Pre-condition (confirmed by Peter, 2026-05-20):** every environment has run "Seed system roles" on `/Camps/Admin`, so existing legacy leads already exist as role assignments. This is what makes the fallback removal safe.

## Part A — UI: Roles + Roster cards

### Layout (`Views/Camp/Details.cshtml`)

```
LEFT (col-md-8)                       RIGHT (col-md-4)
  [carousel / name / badges]            Links & Contact
  ★ Roster   (NEW)                      Times participating
  About (blurb)                         ★ Roles   (replaces Leads)
  Community / Vibes / Culture           My membership
  Placement (lead/admin/cityplanning)   Actions
```

### Visibility matrix

| Viewer | Roles card | Roster card |
|--------|-----------|-------------|
| Anonymous | hidden (login gate kept) | hidden |
| Logged-in, not a member, not CampAdmin | **Camp Lead role only** (assignees) | hidden |
| Active member of this camp | **all active roles** + assignees + open slots (read-only) | shown |
| CampAdmin (any camp) | all active roles (read-only) | shown |

- "Can see the rest" gate: `isCampAdmin || Membership.Status == Active`. (Leads are Active members post-migration, so they see everything.)
- Management controls stay on `/Edit/Members`; these cards are **read-only**.
- The Roles card replaces the existing Leads card. For a non-member it is functionally today's Leads card, but sourced correctly (so Hannah shows).

### Data flow

- Controller `Details`/`SeasonDetails` build a read-only roles view model from `campRoleService.BuildPanelAsync(currentSeason.Id)` (same source as `/Edit/Members`), plus an Active-member roster from `campService.GetCampMembersAsync(currentSeason.Id).Active`.
- Built only when authenticated and a current season exists. Roster fetched only when the viewer passes the "see the rest" gate.

### New view pieces

- `Views/Camp/_CampRolesCard.cshtml` — read-only roles table (Role → assignee `<vc:human>` / "Open"), filtered to the Camp Lead row for non-members. Mirrors `Team/_RosterSection.cshtml` styling.
- `Views/Camp/_CampRosterCard.cshtml` — `<vc:human layout="Card">` grid of Active members.
- `CampDetailViewModel` gains `RolesPanel` (reuse `CampRolesPanelViewModel` with `CanManage=false`) and `Roster` (list of user ids / `CampMemberRowViewModel`). The legacy `Leads` property is removed from the view model once the card is gone.

## Part B — Fix the source (camp creation)

`CampService.CreateCampAsync` currently writes a `CampLead` for the creator (the reason new camps would otherwise show no leads under role-only reads). Change it to:

- Create an Active `CampMember` for the creator on the new season, then a `CampRoleAssignment` for the Camp Lead special role (reuse the same path the seed/`AssignAsync` use).
- If the Camp Lead role **definition** is missing (un-seeded env), log a warning and still create the camp — never block registration (`no-startup-guards`).
- Stop writing the legacy `CampLead` row.

## Part C — Repoint remaining legacy **reads** to the role system

| Site | Current (legacy) | Change |
|------|------------------|--------|
| `Camp/Details.cshtml` ← `BuildCampDetailDataAsync` → `BuildLeadSummariesAsync(camp.Leads)` | reads `camp.Leads` | drop; detail page uses `BuildPanelAsync` (Part A) |
| `CampController.Contact` (line ~209) | `camp.Leads.Select(UserId)` for notify recipients | role-holders of Camp Lead for the season |
| `CampService.GetCampEditDataAsync` (line ~239) | `BuildLeadSummariesAsync(camp.Leads)` → Edit page `model.Leads` | source from role assignments (or drop if `/Edit/Members` already covers it) |
| `CampService.GetCampDirectoryAsync` → `CampRepository.GetCampsByLeadUserIdAsync` | `b.Leads.Any(...)` | query `CampRoleAssignment` (Camp Lead) |
| `CampService.GetCampLeadSeasonIdForYearAsync` → `CampRepository.GetCampLeadSeasonIdForYearAsync` (used by `StoreService`, `CityPlanningController`) | joins `ctx.CampLeads` | join `CampRoleAssignment`/`CampMember` for the year |
| `CampAdminPageBuilder` (line ~90) + `CampAdmin/Index.cshtml` (line ~418) | `c.Leads` | role-holders |
| `CampCsvExportBuilder` (lines ~19, ~37) | `c.Leads` | role-holders |
| `CityPlanningController` (line ~296) + `CityPlanningApiController` (line ~46) | `c.Leads.Any(...)` | `IsUserCampLeadAsync` / role query |
| `CampService.ContributeForUserAsync` (GDPR, line ~1858) | `GetAllLeadAssignmentsForUserAsync` (legacy slice) | drop the legacy `CampLeadAssignments` slice (role slice already emitted) |
| `CachingCampService.GetCampsWithLeadsForYearAsync` path | `.Include(c => c.Leads...)` | drop the unused `Leads` include |

Already repointed in #657 (no work): `CampRepository.GetActiveLeadUserIdsAsync`, `IsLeadAnywhereAsync` (Barrio-Leads sync).

## Part D — Remove legacy **writes** and **fallback** branches

- **Strip fallbacks** (role assignment becomes the sole source): legacy `_repo.IsUserActiveLeadAsync` branch in `CampService.IsUserCampLeadAsync` and `IsUserCampEventManagerAsync`; legacy branch in `GetPendingMembershipCountForLeadAsync`; legacy join in `GetCampLeadSeasonIdForYearAsync` (Part C).
- **Account merge:** `CampService.ReassignAsync` → drop the `ReassignLeadsToUserAsync` call; merge is covered by reassigning the role side (`CampMember.UserId`). Verify no orphaned legacy rows matter (table is orphaned post-seed).
- **Dev seed:** `DevPersonaSeeder` (line ~383) → write `CampMember` + `CampRoleAssignment` (Camp Lead) instead of `AddLeadAsync`.
- **Remove now-dead C# methods** (pure C#, no schema change): `ICampService.AddLeadAsync`/`RemoveLeadAsync` (+ `CampService` impls + `CachingCampService` decorators), `ICampRepository.IsUserActiveLeadAsync`/`CountActiveLeadsAsync`/`AddLeadAsync`/`GetLeadForMutationAsync`/`UpdateLeadAsync`/`GetAllLeadAssignmentsForUserAsync`/`ReassignLeadsToUserAsync`/`GetCampsByLeadUserIdAsync`(legacy form), and the `CampLeadInfo`/`CampLeadSummary`/`CampLeadViewModel` DTOs once unused.
- **Keep** (schema-bound → #774): `CampLead` entity, `Camp.Leads` nav, `CampLeads` DbSet, `CampLeadConfiguration`, `CampLeadRole` enum, `EnsureActiveMemberForMigrationAsync` + the seed button.

## Authorization

No new authorization surface. Detail-page cards reuse the existing computed `(isLead, isCampAdmin)` + `Membership` state already resolved in `Details`. Read-only — no new mutating endpoints. CampAdmin breadth comes from the existing `RoleChecks.IsCampAdmin` / `CampAuthorizationHandler`.

## Testing

- Service tests (`ServiceTestHarness`): `CreateCampAsync` creates an Active member + Camp Lead assignment (not a legacy row); `IsUserCampLeadAsync` true via role only (no legacy fallback); directory pin, store/cityplanning season lookup, contact recipients resolve via role holders; account-merge moves lead via role side.
- Detail-page view-model tests: non-member sees Camp Lead row only; member sees all roles + roster; anonymous sees neither; CampAdmin sees both for any camp.
- Architecture tests stay green (Camps section boundaries).
- No EF migration in this PR → EF reviewer N/A here; runs on #774.

## Rollout / risk

- Reversible: legacy table left intact; only C# read/write paths change. If a regression appears, revert the PR — data is unchanged.
- Safe because all envs are seeded (confirmed) — role assignments are complete for existing leads.
- QA auto-deploys on merge to peter `main`; prod via the promote PR. No operator step required at deploy (seed already run).

## Open item

`nobodies-collective#774` currently bundles the code decouple **and** the physical drop. This PR takes the code-decouple half; #774 should be **re-scoped to the physical drop only** and reference this branch. (Confirm before editing the issue.)
