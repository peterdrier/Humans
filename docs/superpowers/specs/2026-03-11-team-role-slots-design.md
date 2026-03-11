# Team Role Slots

**Date:** 2026-03-11
**Status:** Draft

## Summary

Add the ability for teams to define named role slots that members can fill. Each team defines its own roster of roles (e.g., "Social Media", "Designer", "Production Office") with a configurable number of slots per role, explicit priority per slot, and an optional job description. Team leads assign members to slots. A cross-team roster summary helps everyone see where gaps exist.

## Business Context

As events approach, teams need to fill specific functional positions. Currently there's no way to define what roles a team needs, track which are filled, or see gaps across teams. This feature gives teams a structured roster and gives leadership (and all members) visibility into staffing gaps sorted by priority.

## Data Model

### New Enum: `SlotPriority`

```csharp
public enum SlotPriority
{
    Critical = 0,
    Important = 1,
    NiceToHave = 2
}
```

### New Entity: `TeamRoleDefinition`

| Field | Type | Constraints |
|-------|------|-------------|
| `Id` | Guid | PK |
| `TeamId` | Guid | FK → Team, required |
| `Name` | string | Max 100, required |
| `Description` | string? | Max 2000, markdown-friendly |
| `SlotCount` | int | Required, >= 1 |
| `Priorities` | `List<SlotPriority>` | JSON column, length must equal SlotCount |
| `SortOrder` | int | Display ordering within team |
| `CreatedAt` | Instant | Set on creation |
| `UpdatedAt` | Instant | Set on creation and update |

**Indexes:**
- `(TeamId, Name)` — unique, prevents duplicate role names within a team

**Navigation:**
- `Team` (Team)
- `Assignments` (ICollection\<TeamRoleAssignment\>)

### New Entity: `TeamRoleAssignment`

| Field | Type | Constraints |
|-------|------|-------------|
| `Id` | Guid | PK |
| `TeamRoleDefinitionId` | Guid | FK → TeamRoleDefinition, required |
| `TeamMemberId` | Guid | FK → TeamMember, required |
| `AssignedAt` | Instant | Set on assignment |
| `AssignedByUserId` | Guid | FK → User, required |

**Indexes:**
- `(TeamRoleDefinitionId, TeamMemberId)` — unique, prevents assigning same person to same role twice

**Navigation:**
- `TeamRoleDefinition` (TeamRoleDefinition)
- `TeamMember` (TeamMember)
- `AssignedByUser` (User)

**Cascade deletes:**
- Deleting a `TeamRoleDefinition` deletes its assignments
- Deleting a `TeamMember` deletes their role assignments

### Slot-to-Priority Mapping

Assignments are ordered by `AssignedAt`. The first person assigned gets slot 1 with `Priorities[0]`, the second gets slot 2 with `Priorities[1]`, etc. The system enforces that the number of assignments does not exceed `SlotCount`.

### Modified Entity: `Team`

New navigation property:
- `RoleDefinitions` (ICollection\<TeamRoleDefinition\>)

### Modified Entity: `TeamMember`

New navigation property:
- `RoleAssignments` (ICollection\<TeamRoleAssignment\>)

### Lead Roles

"Lead" role definitions are informational only. They track the desired number of leads and provide a description, but `TeamMember.Role = Lead` remains the source of truth for lead permissions and Leads system team sync. The Lead role definition is not treated differently by the system — it's just a role slot like any other, with the name "Lead".

### Member Departure

When a `TeamMember` leaves a team (or is removed), their slot assignments are automatically cleared via cascade delete (since `TeamMember` deletion/soft-delete triggers). The audit log captures the historical record of who held which slots.

## Permissions

| Action | Who |
|--------|-----|
| View roster (team detail page) | Any authenticated user |
| View roster summary (cross-team) | Any authenticated user |
| View role description | Any authenticated user |
| Create/edit/delete role definitions | Team lead (own team), TeamAdmin, Board, Admin |
| Assign/unassign members to slots | Team lead (own team), TeamAdmin, Board, Admin |

This follows the existing authorization pattern used by `CanUserApproveRequestsForTeamAsync`.

## Routes

| Route | Method | Purpose | Auth |
|-------|--------|---------|------|
| `GET /Teams/{slug}` | GET | Existing detail page, now includes roster section | Authenticated |
| `GET /Teams/Roster` | GET | Cross-team roster summary, unfilled slots by priority | Authenticated |
| `GET /Teams/{slug}/Roles` | GET | Role management page (CRUD definitions) | Lead/TeamAdmin/Board/Admin |
| `POST /Teams/{slug}/Roles` | POST | Create role definition | Lead/TeamAdmin/Board/Admin |
| `POST /Teams/{slug}/Roles/{id}/Edit` | POST | Update role definition | Lead/TeamAdmin/Board/Admin |
| `POST /Teams/{slug}/Roles/{id}/Delete` | POST | Delete role definition | Lead/TeamAdmin/Board/Admin |
| `POST /Teams/{slug}/Roles/{id}/Assign` | POST | Assign member to slot | Lead/TeamAdmin/Board/Admin |
| `POST /Teams/{slug}/Roles/{id}/Unassign/{memberId}` | POST | Unassign member from slot | Lead/TeamAdmin/Board/Admin |

## UI Design

### Roster Section on Team Detail Page

Added as a new section on the existing `/Teams/{slug}` page, below the current members list:

- Displays all role definitions with their slots, sorted by `SortOrder`
- Each slot row shows: role name, slot number, priority badge (color-coded), assigned member name (or "Open")
- Open critical slots visually emphasized (e.g., red/warning badge)
- Role description shown on expand/click (markdown rendered)
- Team leads and TeamAdmin see "Edit Roles" button linking to management page
- Team leads and TeamAdmin see "Assign" buttons on open slots

### Role Management Page

Accessible via `/Teams/{slug}/Roles` for authorized users:

- List of role definitions with inline editing
- Add new role: name, description (textarea, markdown), slot count, priority per slot, sort order
- Edit existing role: same fields, with validation when reducing slot count (block if filled slots would be removed)
- Delete role: confirmation required, warns about existing assignments
- Assign/unassign members via member picker (reuses existing autocomplete search from AddMember)

### Cross-Team Roster Summary

New page at `/Teams/Roster`, accessible to all authenticated users:

- Shows unfilled slots across all active teams
- Sorted by priority (Critical first), then by team name
- Columns: Team, Role, Slot #, Priority, Status (Open/Filled)
- Filterable by priority level
- Links to team detail page for each row

## Validation Rules

- `SlotCount` must be >= 1
- `Priorities` array length must equal `SlotCount`
- Cannot assign more members than `SlotCount`
- Cannot assign a non-active team member to a slot
- Cannot assign the same member to the same role definition twice (unique constraint)
- A member CAN hold multiple different role slots on the same team
- When reducing `SlotCount`, filled slots beyond the new count must be unassigned first
- Role name must be unique within a team (case-insensitive)

## Audit Logging

All role definition and assignment changes logged via existing `IAuditLogService`:
- Role definition created/updated/deleted
- Member assigned to / unassigned from role slot

## Related Features

- **Teams & Working Groups** (`docs/features/06-teams.md`) — parent feature
- **Team Member Management** — existing lead/member role system
- **Leads System Team** — auto-synced from `TeamMember.Role = Lead`
