# Team Hierarchy (Departments) and Leads → Coordinators Rename

**Issue:** #130
**Date:** 2026-03-16

## Overview

Three interconnected changes to the team system:

1. **Rename Leads → Coordinators** across code, UI, localization, and database
2. **Replace name-based role identification with `IsManagement` flag** on `TeamRoleDefinition`
3. **Add one-level team hierarchy** via `ParentTeamId` on `Team`

## 1. Rename Leads → Coordinators

### Enum Changes

| Enum | Old | New | Value (unchanged) |
|------|-----|-----|-------------------|
| `TeamMemberRole` | `Lead` | `Coordinator` | `1` |
| `SystemTeamType` | `Leads` | `Coordinators` | `2` |

Both enums are stored as **strings** in PostgreSQL (via `HasConversion<string>()`). The migration must update existing rows:
- `UPDATE team_members SET "Role" = 'Coordinator' WHERE "Role" = 'Lead'`
- `UPDATE teams SET "SystemTeamType" = 'Coordinators' WHERE "SystemTeamType" = 'Leads'`

The `EnumStringStabilityTests` must be updated to expect the new names.

### ContactFieldVisibility.LeadsAndBoard

`ContactFieldVisibility.LeadsAndBoard` (also stored as string) is renamed to `CoordinatorsAndBoard`. Migration updates `contact_fields` and `user_emails` tables accordingly.

### System Team Rename

The system team with ID `00000000-0000-0000-0001-000000000002`:
- Name: "Leads" → "Coordinators"
- Slug: "leads" → "coordinators"
- Description: "All team leads" → "All team coordinators"

Updated in both seed data and a SQL migration for the existing row.

### Code Renames

- `SystemTeamIds.Leads` → `SystemTeamIds.Coordinators`
- `SyncLeadsTeamAsync` → `SyncCoordinatorsTeamAsync`
- `SyncLeadsMembershipForUserAsync` → `SyncCoordinatorsMembershipForUserAsync`
- All `TeamMemberRole.Lead` references → `.Coordinator`
- All `SystemTeamType.Leads` references → `.Coordinators`
- `ContactFieldVisibility.LeadsAndBoard` → `.CoordinatorsAndBoard`
- `IsLeadRole` computed property — removed (replaced by `IsManagement`)
- `ISystemTeamSync` interface method signatures renamed to match
- `SetMemberRoleAsync` — removed (promotion/demotion is now implicit via management role assignment)
- `POST Members/{userId}/SetRole` endpoint in `TeamAdminController` — removed
- All hardcoded `Name == "Lead"` string checks in `TeamService` (including `UnassignFromRoleAsync`) → replaced with `IsManagement` flag checks

### Localization (all 5 locales)

"Coordinator" is kept in English across all locales (same pattern as "humans"). All "Lead"/"Leads"/"Team Lead" strings updated to use "Coordinator"/"Coordinators".

The "Promote to Lead" / "Demote from Lead" UI actions are **removed** — promotion/demotion is now implicit via management role assignment (see section 2).

### Out of Scope

`CampLead` / `CampLeadRole` — separate domain, not renamed. This includes camp view model properties like `CampDetailViewModel.IsCurrentUserLead` which reference CampLead, not team coordination.

## 2. IsManagement Flag on TeamRoleDefinition

### New Property

`bool IsManagement` on `TeamRoleDefinition` (default `false`).

Replaces the computed `IsLeadRole` property (which checked `Name == "Lead"`) and all hardcoded "Lead" name matching in `TeamService`.

### Constraints

- At most one role definition per team can have `IsManagement = true` (enforced in TeamService)
- No database-level constraint — app-level only, consistent with existing patterns

### Default Behavior

- New teams auto-create a management role named "Coordinator" with `IsManagement = true`, `SlotCount = 1`
- Teams can **rename** their management role to anything (Chair, Director, etc.) — the name is no longer significant
- Teams can **reassign** which role is the management role (clear `IsManagement` on old, set on new) — but `IsManagement` cannot be toggled on a role that currently has assigned members. Unassign all members first, then change the flag. This avoids complex batch-update logic for `TeamMemberRole` sync.
- Management role can be deleted if no one is assigned; a new one can be designated
- A team can legitimately have zero management roles — this just means no members get `TeamMemberRole.Coordinator` from that team, and the team contributes no one to the System Coordinators team
- The old restrictions (cannot rename/delete/manually create "Lead" role) are all removed

### Auto-Sync with TeamMemberRole

- Assigning a member to a role slot where `IsManagement = true` → sets their `TeamMemberRole` to `Coordinator`
- Unassigning a member from a management role slot → reverts their `TeamMemberRole` to `Member`
- Leaving or being removed from a team → also triggers coordinator sync for that user (clears `Coordinator` status if they have no other management roles elsewhere)
- This replaces the old explicit "Promote to Lead" / "Demote from Lead" actions

### System Coordinators Team Sync

- `SyncCoordinatorsTeamAsync` queries all users with `TeamMemberRole.Coordinator` on non-system teams (same logic as today, new enum name)
- Single-user sync triggered on role assignment/unassignment changes

### Migration Data

- Add `is_management` column (bool, default false)
- UPDATE existing role definitions where `Name = 'Lead'` → set `IsManagement = true`
- Rename existing "Lead" role definitions to "Coordinator"

## 3. Team Hierarchy — One-Level Departments

### Schema

- `ParentTeamId` (nullable `Guid?` FK) on `Team`, self-referencing to `Team.Id`
- Navigation properties: `Team.ParentTeam` and `Team.ChildTeams`
- ON DELETE: RESTRICT (prevents hard-delete of a team with children)

### Constraints (app-level in TeamService)

- A team with a parent cannot have children added to it (one-level max)
- A team with children cannot have a parent set on it
- System teams cannot be parents or children
- Cannot set self as parent
- Soft-delete (`DeleteTeamAsync`): must also check for active children and reject if any exist — orphaned children under an inactive parent are not allowed

### UI — New Departments Page (`Teams/Departments`)

A new public-facing page showing teams in a vertical tree layout (similar visual density to `/Teams/Summary`):

- Departments (teams with children) shown as collapsible branch headers
- Child teams nested under their department
- Standalone teams (no parent, no children) shown at root level
- Each entry links to the team's detail page

### Reserved Slugs

Add `"departments"` to the `reservedSlugs` list in `CreateTeamAsync` to prevent a user-created team from shadowing the new route.

### Cache

Extend `CachedTeam` to include `ParentTeamId` and `ChildTeamIds` so hierarchy data is available from the team cache without extra queries.

### UI — Team Index (`Team/Index`)

Unchanged — remains a flat card grid of all teams with no hierarchy awareness.

### UI — Team Details (`Team/Details`)

- If the team has a parent: breadcrumb/link to parent department
- If the team has children: "Sub-teams" section listing child teams

### UI — Create/Edit Team

- Optional "Parent team" dropdown
- Only shows eligible parents: user-created teams with no parent of their own
- Board/Admin/TeamsAdmin permissions (same as current edit permissions)

### UI — MyTeams

- Group user's teams under their parent departments for visual coherence

## Migration Summary

Single EF Core migration covering all schema changes:

1. UPDATE `team_members` SET `Role` = `'Coordinator'` WHERE `Role` = `'Lead'` (string-stored enum)
2. UPDATE `teams` SET `SystemTeamType` = `'Coordinators'` WHERE `SystemTeamType` = `'Leads'` (string-stored enum)
3. UPDATE `contact_fields` and `user_emails`: rename `'LeadsAndBoard'` → `'CoordinatorsAndBoard'` visibility values
4. Update system team row: name, slug, description
5. Add `is_management` bool column to `team_role_definitions` (default false)
6. UPDATE existing "Lead" role definitions: set `is_management = true`, rename to "Coordinator"
7. Add `parent_team_id` nullable FK column to `teams`
8. Add FK constraint with RESTRICT delete behavior

### Code Changes (non-migration)

- Update `EnumStringStabilityTests` to expect the new enum names

## Acceptance Criteria

- [ ] All "Lead" references renamed to "Coordinator" in code, UI, localization, and docs
- [ ] "Coordinator" appears untranslated in all 5 locales
- [ ] `IsManagement` bool on `TeamRoleDefinition` with max-one-per-team constraint
- [ ] Coordinator role auto-set/unset when management role assigned/unassigned
- [ ] "Promote to Lead" / "Demote from Lead" UI actions removed
- [ ] Management roles can be freely renamed (no reserved name)
- [ ] `ParentTeamId` on Team with one-level-max enforcement
- [ ] New Departments page shows tree view of department → sub-team grouping
- [ ] Team detail views show parent/child relationships
- [ ] System Coordinators team auto-syncs based on `TeamMemberRole.Coordinator` (set automatically via `IsManagement` role assignments)
- [ ] EF migration covers all schema changes
- [ ] Feature spec updated in `docs/features/`
