<!-- freshness:triggers
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Shifts/Services/ShiftSignupService.cs
  src/Sections/Humans.Shifts/Controllers/ShiftsController.cs
  src/Sections/Humans.Shifts/Controllers/ShiftAdminController.cs
  src/Humans.Base/Authorization/ShiftRoleChecks.cs
  src/Sections/Humans.Shifts/Views/Shifts/**
  src/Sections/Humans.Shifts/Views/ShiftAdmin/**
-->
<!-- freshness:flag-on-change
  Privileged-viewer signup display (name list / avatar row), the includeSignups flag, or column wiring on the browse/admin pages may have changed.
-->

# 26 — Shift Signup Visibility

## Business Context

Coordinators and admins managing shifts cannot currently see who has signed up for upcoming shifts — only aggregate fill counts are visible. Individual signup names only appear for past shifts on the coordinator admin page. This makes it harder to coordinate teams, approve pending signups with context, and ensure the right people are on the right shifts.

## Authorization

> **Policy change (under evaluation):** Signup lists on the browse page (`/Shifts`) are temporarily visible to **all** authenticated viewers — not just coordinators/admins. The `isPrivileged` computation is intentionally retained in `ShiftsController` so the gate can be reinstated by flipping `ShowSignups` and `includeSignups` back to `isPrivileged` if folks object. Acceptance criteria below are written against the current (public) policy.

- **Browse page (`/Shifts`):** Signup lists visible to every authenticated user. The `isPrivileged` variable still gates other admin-only behaviour (AdminOnly shift visibility, hidden rota visibility, browsing-while-closed).
- **Admin page (`/Teams/{slug}/Shifts`):** Uses the existing `CanApproveDepartmentAsync` helper in `ShiftAdminController` — true for Admin, NoInfoAdmin, VolunteerCoordinator, or coordinator of that specific team. Unchanged.

## User Stories

### US-1: See who signed up for Event shifts
**As a** volunteer browsing shifts,
**I want to** see avatar thumbnails of who is signed up for each Event (hourly) shift,
**so that** I can coordinate with my team at a glance and make informed signup decisions.

**Acceptance Criteria:**
- A "Signed Up" column appears to the right of the Filled column on both `/Shifts` (browse) and `/Teams/{slug}/Shifts` (admin) for Event rotas
- Column shows a row of small circular avatar thumbnails (~26px), reusing the shared `<vc:human layout="Avatar" size="26">` component (`HumanViewComponent`)
- Each avatar links to `/Profile/{userId}` with a hover popover showing display name
- Confirmed avatars render at full opacity
- Pending avatars render at 50% opacity with a dashed border (and the title carries the localized "Pending" label)
- Only Confirmed and Pending signups are shown — Refused, Bailed, NoShow, and Cancelled are excluded
- Empty cell when no signups (Filled column already shows "0/N")
- Column renders for all authenticated viewers on `/Shifts` (temporary public policy — see Authorization)
- Applies to both future and current shifts (on the admin page, past shifts' per-signup lists live in the unified Manage panel — confirmed humans plus read-only no-show/bailed history — rather than a separate Signups collapsible)

### US-2: See who signed up for Build/Strike shifts
**As a** coordinator or admin browsing shifts,
**I want to** see avatar thumbnails of volunteers signed up for each Build/Strike (daily) shift,
**so that** I can see at a glance who's coming on each build/strike day.

**Acceptance Criteria:**
- A "Signed Up" column appears after the Status column on both `/Shifts` (browse) and `/Teams/{slug}/Shifts` (admin) for Build/Strike rotas
- Column shows a row of small circular avatar thumbnails, reusing the same `<vc:human layout="Avatar" size="26">` component as the Event rota column (no separate reduced-size component)
- Each avatar links to `/Profile/{userId}` with a `title` attribute showing display name
- Confirmed avatars render at full opacity
- Pending avatars render at 50% opacity with a dashed border
- Only Confirmed and Pending signups are shown
- Avatars wrap naturally when many signups are present (no truncation or "+N more" needed at our scale)
- Column renders for all authenticated viewers on `/Shifts` (temporary public policy — see Authorization)

## Data Model

**No schema changes.** `ShiftSignup` carries `UserId` and `Status` as bare columns — the `User` navigation was stripped in #541 and the FK went with the cross-section FK cut in #992. Display data (`DisplayName`, profile picture) resolves through `IUserServiceRead.GetUserInfosAsync` from the cached user snapshot.

### Service Layer

`GetBrowseShiftsAsync` is called with `includeSignups: true` unconditionally on `/Shifts` while the public policy is in effect. The service loads signups filtered to Confirmed + Pending status only and stitches display names in memory from the user snapshot. When the policy is reverted, both `ShowSignups` and `includeSignups` flip back to `isPrivileged` together.

### ViewModel Changes

Add signup user data to `ShiftDisplayItem` (or a new nested DTO):

```csharp
public record ShiftSignupInfo(Guid UserId, string DisplayName, SignupStatus Status);
```

The avatar is rendered from `UserId` by the shared `<vc:human>` component, which resolves the profile picture via `/Profile/Picture?id={profileId}` (the `id` is the **profile** id, not the user id); the record carries no picture URL of its own.

`ShiftDisplayItem` gains: `IReadOnlyList<ShiftSignupInfo> Signups`. The view chooses name-list vs avatar display based on the parent rota's `RotaPeriod`.

## Affected Pages

| Page | Route | Rota Type | Display Pattern |
|------|-------|-----------|----------------|
| Shift browse | `/Shifts` | Event | Avatar row column |
| Shift browse | `/Shifts` | Build/Strike | Avatar row column |
| Shift admin | `/Teams/{slug}/Shifts` | Event | Name list column |
| Shift admin | `/Teams/{slug}/Shifts` | Build/Strike | Avatar row column |

## Pages NOT Affected

- `/Shifts/Mine` — personal view, unchanged
- `/Shifts/Dashboard` — has its own UX, unchanged
- Homepage shift cards — unchanged
- Team detail shift summary — unchanged

## Localization

Existing keys cover all browse-page strings — no new keys needed for the avatar-chip rendering. The existing `Shifts_SignedUp` column header and `Shifts_Pending` label (used in avatar tooltips) are already in all 6 locales.

## Related Features

- [25 — Shift Management](shift-management.md): parent feature
- [09 — Administration](../../../../../docs/features/global/administration.md): admin role checks
- [17 — Coordinator Roles](coordinator-roles.md): coordinator authorization
