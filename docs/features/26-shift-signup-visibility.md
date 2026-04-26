<!-- freshness:triggers
  src/Humans.Application/Services/Shifts/ShiftManagementService.cs
  src/Humans.Application/Services/Shifts/ShiftSignupService.cs
  src/Humans.Web/Controllers/ShiftsController.cs
  src/Humans.Web/Controllers/ShiftAdminController.cs
  src/Humans.Web/Authorization/ShiftRoleChecks.cs
  src/Humans.Web/Views/Shifts/**
  src/Humans.Web/Views/ShiftAdmin/**
  src/Humans.Web/ViewComponents/UserAvatarViewComponent.cs
-->
<!-- freshness:flag-on-change
  Privileged-viewer signup display (name list / avatar row), the includeSignups flag, or column wiring on the browse/admin pages may have changed.
-->

# 26 — Shift Signup Visibility

## Business Context

Coordinators and admins managing shifts cannot currently see who has signed up for upcoming shifts — only aggregate fill counts are visible. Individual signup names only appear for past shifts on the coordinator admin page. This makes it harder to coordinate teams, approve pending signups with context, and ensure the right people are on the right shifts.

## Authorization

Signup names/avatars use existing per-page auth checks — no new authorization logic needed:

- **Browse page (`/Shifts`):** Uses the existing `isPrivileged` variable in `ShiftsController` — true for Admin, NoInfoAdmin, or any department coordinator (`GetCoordinatorDepartmentIdsAsync().Count > 0`).
- **Admin page (`/Teams/{slug}/Shifts`):** Uses the existing `CanApproveAsync` helper in `ShiftAdminController` — true for Admin, NoInfoAdmin, VolunteerCoordinator, or coordinator of that specific team.

Regular volunteers see only fill counts (existing behavior, unchanged).

## User Stories

### US-1: See who signed up for Event shifts
**As a** coordinator or admin browsing shifts,
**I want to** see the names of volunteers signed up for each Event (hourly) shift,
**so that** I can coordinate team composition and make informed approval decisions.

**Acceptance Criteria:**
- A "Signed Up" column appears to the right of the Filled column on both `/Shifts` (browse) and `/Teams/{slug}/Shifts` (admin) for Event rotas
- Column shows comma-separated display names, each linked to `/Human/{userId}`
- Confirmed names render in normal weight
- Pending names render in muted/italic with a "(pending)" label
- Only Confirmed and Pending signups are shown — Refused, Bailed, NoShow, and Cancelled are excluded
- Empty cell when no signups (Filled column already shows "0/N")
- Column only renders for coordinator/admin viewers — omitted entirely for regular volunteers
- Applies to both future and current shifts (past shifts retain existing collapsible pattern on admin page)

### US-2: See who signed up for Build/Strike shifts
**As a** coordinator or admin browsing shifts,
**I want to** see avatar thumbnails of volunteers signed up for each Build/Strike (daily) shift,
**so that** I can see at a glance who's coming on each build/strike day.

**Acceptance Criteria:**
- A "Signed Up" column appears after the Status column on both `/Shifts` (browse) and `/Teams/{slug}/Shifts` (admin) for Build/Strike rotas
- Column shows a row of small circular avatar thumbnails (~24-28px), reusing `UserAvatarViewComponent` at reduced size
- Each avatar links to `/Human/{userId}` with `title` attribute showing display name
- Confirmed avatars render at full opacity
- Pending avatars render at 50% opacity with a dashed border
- Only Confirmed and Pending signups are shown
- Avatars wrap naturally when many signups are present (no truncation or "+N more" needed at ~500-user scale)
- Column only renders for coordinator/admin viewers

## Data Model

**No schema changes.** `ShiftSignup` already carries `UserId`, `Status`, and navigation to `User` (with `DisplayName`, profile picture data). Signup data is already loaded on the admin page via navigation properties.

### Service Layer

Pass `includeSignups: true` to `GetBrowseShiftsAsync` when the viewer is privileged. The service includes `User` navigation on `ShiftSignup` entities and filters to Confirmed + Pending status only. When `includeSignups` is false (regular volunteers), `Signups` is an empty list.

### ViewModel Changes

Add signup user data to `ShiftDisplayItem` (or a new nested DTO):

```csharp
public record ShiftSignupInfo(Guid UserId, string DisplayName, SignupStatus Status, string? ProfilePictureUrl);
```

`ProfilePictureUrl` is the route `/Human/{userId}/Picture` (or null if no picture uploaded). This is passed directly to `UserAvatarViewComponent`.

`ShiftDisplayItem` gains: `IReadOnlyList<ShiftSignupInfo> Signups` — always populated when viewer is privileged (empty list otherwise). The view chooses name-list vs avatar display based on the parent rota's `RotaPeriod`.

## Affected Pages

| Page | Route | Rota Type | Display Pattern |
|------|-------|-----------|----------------|
| Shift browse | `/Shifts` | Event | Name list column |
| Shift browse | `/Shifts` | Build/Strike | Avatar row column |
| Shift admin | `/Teams/{slug}/Shifts` | Event | Name list column |
| Shift admin | `/Teams/{slug}/Shifts` | Build/Strike | Avatar row column |

## Pages NOT Affected

- `/Shifts/Mine` — personal view, unchanged
- `/Shifts/Dashboard` — has its own UX, unchanged
- Homepage shift cards — unchanged
- Team detail shift summary — unchanged

## Localization

~2-3 new keys across 5 languages:
- Column header: "Signed Up" / "Apuntados" / "Angemeldet" / "Inscrits" / "Iscritti"
- Pending label: "(pending)" / "(pendiente)" / "(ausstehend)" / "(en attente)" / "(in attesa)"

## Related Features

- [25 — Shift Management](25-shift-management.md): parent feature
- [09 — Administration](09-administration.md): admin role checks
- [17 — Coordinator Roles](17-coordinator-roles.md): coordinator authorization
