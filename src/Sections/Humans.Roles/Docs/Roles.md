<!-- freshness:triggers
  src/Sections/Humans.Roles/**
-->
<!-- freshness:flag-on-change
  Who manages an owner's roles (coordinator of the owner or an ancestor, Admin, RolesAdmin; coordinator roles admin-only), the assign path asking the owner section first, and RemoveAsync before a membership ends.
-->

# Roles — Section Invariants

Standing responsibilities on an owner (a team today, camps next): Cantina coordinator, Cantina shift planner, Power lead. A role has slots; people sit in them.

## Concepts

- An **owner** (`Owner(Kind, Id)`, in Base) is what a role belongs to. The owner's section registers the kind (`IOwnerKind` for the tree, `IRoleOwner` for membership). Teams registers `Team`.
- A **role** has a name, a slug (made from the name, plus an optional custom slug), Markdown description, slot count with a priority per slot, period, estimated hours, public flag, and a list of permission keys.
- The **coordinator role** is the one role per owner whose holders hold every permission that applies to the owner. Its key list stays empty.
- A **system role** (Auth's roles, such as Admin, ShiftsAdmin) is tied to a section. A role is tied to an owner. Roles declares one system role, **RolesAdmin**.
- Roles are not per year: they carry over from event to event.

## Pages

| Route | What | Who |
|---|---|---|
| `GET /Roles` | Open-roles board: every open slot on a role the viewer can see, most needed first. Filters: owner, period, priority. In the main nav. | Signed-in members |
| `GET /Roles/{kind}/{key}` | An owner's roles, plus the add form | Whoever manages that owner's roles |
| `GET /Roles/{kind}/{key}/{slug}` | One role: description, slots and holders, who to talk to; for managers, assign, unassign, edit and delete | Anyone who can see the role |
| `GET /Roles/Admin` | Every active owner: its coordinators, role count, open slots, hidden flag. Each links to the manage page where the viewer manages it, else the owner's own page. The admin sidebar's Roles group. English only (admin page). | Admin, RolesAdmin, Board (`RolesPolicies.Read`) |

- A team's roles card renders on the team page through Teams' `ITeamRolesPart` slot. It renders nothing when the viewer sees no roles and can't manage them.
- `{key}` is the owner's own link key (a team's slug). `{slug}` matches the slug or the custom slug, and only within that owner.
- "Who to talk to" lists the holders of the nearest coordinator role, walking up from the owner.
- Refusals and messages are localized (`RolesResource`, 7 cultures). Owner sections return their refusals already localized.

## Data Model

| Table | Columns |
|---|---|
| `role_definitions` (`Role`) | id, owner kind, owner id, name, slug, custom slug, description, slot count, priorities, sort order, period, estimated hours, public, coordinator, permission keys, created, updated |
| `role_assignments` (`RoleAssignment`) | id, role id, user id, slot, assigned at |

The holder is a user, not a team member. Unassigning deletes the row; the audit log is the history.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin, RolesAdmin | Create, edit, fill and delete any role, including every owner's coordinator role |
| Holder of an owner's coordinator role | Manage the other roles on that owner and every owner below it |
| Owner sections (Teams) | Make the person a member before an assign, or refuse; call `IRoleWrite.RemoveAsync` before ending a membership |

## Invariants

- Add/edit forms require explicit slot count, sort order, period, public visibility and coordinator flags. Binding errors (including malformed optional hours or priorities) never reach create/update: after resolving the actor and route owner (plus the role for edits), owner managers receive HTTP 400 with a Warning and non-managers receive 403. Valid writes retain service-owned coordinator authorization. Zero sort order, false flags, blank optional hours, an empty permission selection and default None priorities for new slots retain their form semantics.

- A role has one owner, of a kind a section registers.
- Name is unique per owner, ignoring case. Slug and custom slug are unique per owner across both, checked by the service (the database enforces uniqueness on ids only). Links use slugs, never the id.
- At most one coordinator role per owner. Only Admin and RolesAdmin create, edit, fill, empty or delete it, and whether a role is the coordinator role changes only while it is empty.
- Who manages an owner's roles: Admin, RolesAdmin, and holders of the coordinator role on that owner or an ancestor, counted only on active, non-system owners. Never a parent or a sibling. Holding a narrow role never lets anyone manage roles.
- A permission key goes on a role only if a section declares it, it applies to the owner's kind, and, for a top-level-only permission, the owner is top level. The shared add/edit form orders eligible permissions by title; the service returns them unordered.
- Creates and edits refuse names and normalized custom links over 100 characters, descriptions over 2000 characters, and slot priorities whose comma-separated representation exceeds 500 characters. Names and descriptions count Unicode characters. These checks precede persistence and leave refused edits untouched. After authorization, the service checks the projected priorities length before allocating slots, pads missing priorities with None and drops priorities beyond the slot count. Oversized counts are refused without padding.
- One slot per person per role; an assign takes the lowest open slot. Assignment refuses missing or tombstoned people after authorizing the actor and resolves archived IDs to the survivor before checking duplicates, asking the owner, storing the holder and linking the audit entry.
- Owner lists and team cards select the most urgent open-slot priority directly by the shared priority rank; a fully filled role has no open-priority badge.
- Before an assign, Roles asks the owner's section (`IRoleOwner.EnsureMemberAsync`), which may refuse or add the member. Teams refuses system and inactive teams.
- Every create, edit, delete, assign and unassign is audited by Roles. An edit that changes the permission list says so in the audit text.
- GDPR: export lists the person's roles with owner names; erasure unassigns them everywhere; a merge moves their slots to the survivor and drops one where the survivor already holds that role.

## Negative Access Rules

- A member **cannot** see a private role, or any role on a hidden owner, unless they manage that owner's roles or are Admin, RolesAdmin or Board. Nobody sees roles on an inactive owner, on the board or the role page.
- A member who can't manage an owner's roles **cannot** open its manage page (403).
- Only Admin, RolesAdmin and Board **can** open `/Roles/Admin`; coordinators cannot. It is read-only.

- A coordinator **cannot** manage roles on a parent or sibling owner.
- A coordinator **cannot** create, fill or empty a coordinator role, even their own.
- RolesAdmin **does not** hold any permission through being RolesAdmin.

## Triggers

- Teams calls `IRoleWrite.RemoveAsync` when a person leaves or is removed from a team, when account deletion revokes all their memberships, and for each member when a team is deactivated. A merge doesn't: Roles' own merge step moves the slots.

## Cross-Section Dependencies

- **Auth**: `ISystemRoleService.GetActiveForUserAsync` for Admin and RolesAdmin.
- **Permissions**: owner kinds (`IOwnerKind`) for the tree, section declarations (`ISectionPermissions`) for key checks. Roles registers itself as an `IPermissionSource`.
- **Owner sections**: found through DI (`IRoleOwner`, `IOwnerKind`, `IEntityNameContributor` for owner names). Roles references none of them.
- **Teams.Contracts**: `RolePeriod` and `SlotPriority`, shared with Teams' old roles until those retire; Roles fills the team page's `ITeamRolesPart` slot.

## Architecture

**Owning services:** `RoleService`, behind `CachingRoleService`
**Owned tables:** `role_definitions`, `role_assignments`
**Status:** (A) Migrated — new section, 2026-11-21. Built beside Teams' old roles, which keep working until departments have moved over by hand.

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `IRoleService` | 2 | `GetRolesAsync`, `GetRoleAsync`; whole, from the cache |
| `IRoleWrite` | 1 | `RemoveAsync(owner, userId, actorUserId)` |
| `IRoleOwner` | — | Implemented by owner sections: `EnsureMemberAsync`, `ListAsync` (owner names, link keys, hidden flag, page URL) |
| `RolesPolicies` | — | `Read`: the policy name for `/Roles/Admin` |

- `CachingRoleService` holds every role; every write clears it. It is the permission source and carries GDPR and merge, and never injects the permission service.
- Tests: `tests/Humans.Roles.Tests/RoleServiceTests.cs`.
