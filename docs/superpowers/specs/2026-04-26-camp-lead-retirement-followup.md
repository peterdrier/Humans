# Retire CampLead entity, fold into CampRoleAssignment

## Context

Camp Lead is currently tracked via the `CampLead` entity (its own table, enum, repo methods, and authorization handler). Issue [nobodies-collective#489](https://github.com/nobodies-collective/Humans/issues/489) introduced `CampRoleAssignment` for per-camp roles via PR [peterdrier#338](https://github.com/peterdrier/Humans/pull/338); this issue completes the unification by making "Camp Lead" a system-managed `CampRoleDefinition` and retiring the bespoke `CampLead` entity.

This was deliberately split out of #489 — that PR was already a re-implementation against drift; folding the Camp Lead retirement in would have doubled the blast radius. Camp authorization currently flows through `CampAuthorizationHandler.IsUserCampLeadAsync` which queries the `camp_leads` table. Repointing it at `CampRoleAssignment` is a focused authz refactor that benefits from its own PR.

## Acceptance criteria

- [ ] Seed a system role definition "Camp Lead" with a stable, well-known GUID. Properties: `MinimumRequired = 1`, default `SlotCount = 2`. Marked **undeletable** (and unrenameable) — CampAdmin can adjust `SlotCount` but cannot deactivate, rename, or remove the row.
- [ ] Data migration: for every existing row in `camp_leads` (active rows only), create a corresponding `CampMember(Status = Active)` for the camp's open season (or most recent open season if none) and a `CampRoleAssignment` for the Camp Lead definition. Migration is idempotent and reversible; deletion of the Camp Lead role row is impossible while assignments exist (FK with `OnDelete(Restrict)` already in place).
- [ ] Repoint `CampAuthorizationHandler.IsUserCampLeadAsync` from `CampLead` lookup to `CampRoleAssignment` lookup against the Camp Lead role definition.
- [ ] `ICampService.IsUserCampLeadAsync(userId, campId)` becomes a pass-through to `ICampRoleService` lookup. `GetCampsByLeadUserIdAsync` and `GetCampLeadSeasonIdForYearAsync` similarly switch their data source.
- [ ] Drop the `camp_leads` table (`DropTable`), `CampLead` entity, `CampLeadRole` enum, `AddLeadAsync`/`RemoveLeadAsync` repo methods, `CampLeadConfiguration`, and the lead-related card on `Views/Camp/Edit.cshtml`.
- [ ] Replace the existing "Add lead / Remove lead" UX on the camp Edit page with the unified "Assign / Unassign Camp Lead role" rows from the Roles panel introduced by peterdrier#338. Camp Lead appears at the top of the Roles panel (lowest `SortOrder`).
- [ ] Existing `CampLeadJoinRequestsBadge` cache continues to function (re-source from `CampRoleAssignment` lookup).
- [ ] Tests covering: the auth flow before/after migration, the data migration is idempotent and produces the expected `CampMember` + `CampRoleAssignment` rows, no dangling references to `CampLead` remain anywhere in `src/`.
- [ ] EF migration reviewer agent passes with no CRITICAL findings.

## Out of scope

- Modifying lead authorization semantics beyond the data-source change. Same humans grant the same `Manage` capability after the migration.
- Changing notification fan-out for lead-driven actions.

## Risks

- **Authorization blast radius.** Every `CampOperationRequirement.Manage` check flows through this handler. A regression silently grants/denies camp-management access to humans across the org. Recommend QA on a preview env (Coolify per-PR deploy) before merging — manual checks: lead-of-camp-A can edit camp A and not camp B; CampAdmin can edit any camp; non-lead non-admin gets 403 on camp-A Edit GET.
- **Data migration edge cases.** `CampLead` is per-camp (not per-season), so a lead exists across all seasons of their camp. The migration must pick the open season (or most recent if no season is open) for the new `CampRoleAssignment.CampSeasonId`. If a camp has no seasons at all, log and skip — those leads are stale and should be reviewed manually.
- **Role-definition immutability.** The Camp Lead row must be undeletable / unrenameable. Either enforce in `CampRoleService` (reject mutation if `Id == camplead-known-guid`) or add an `IsSystem: bool` column on `CampRoleDefinition` and gate mutations on `!IsSystem`. The latter is more general and matches Teams' system-team handling.

## Related

- Spec for the role infrastructure: `docs/superpowers/specs/2026-04-26-issue-489-camp-roles-reimpl-design.md`
- Predecessor PR: peterdrier#338 (closes nobodies-collective#489)
- Section invariant: `docs/sections/Camps.md` (Architecture footer needs an update once `CampLead` is gone)
- Related design rule that drifted before this PR and should be cleaned up here: `docs/architecture/design-rules.md §8` doesn't currently list `camp_members` or the new role tables under Camps' owned tables.

## Section

`section:Camps`
