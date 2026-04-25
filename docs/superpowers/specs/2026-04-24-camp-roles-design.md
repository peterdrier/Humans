# Per-camp role assignments (barrio roles) — design

**Issue:** [nobodies-collective/Humans#489](https://github.com/nobodies-collective/Humans/issues/489) — Add configurable camp roles with per-role slots.
**Depends on:** [peterdrier/Humans#228](https://github.com/peterdrier/Humans/pull/228) (per-season camp membership, closes nobodies-collective#488). Work branches from `sprint/20260415/batch-15`; will rebase onto `main` if #228 merges first.
**Branch:** `sprint/2026-04-24/issue-489`.
**Date:** 2026-04-24.

## Goal

Enable camps (barrios) to designate the humans who hold key operational responsibilities each season — Consent Lead, Wellbeing Lead, LNT, Shit Ninja, Power, Build Lead. Role definitions are **global** (CampAdmin-managed), assignments are **per camp-season**, mirrored on the existing Teams role pattern (per-slot rows like `TeamRoleAssignment`).

## Non-goals

- Per-camp custom roles (CampAdmin manages one org-wide list; camps cannot add their own).
- Per-slot priority (Teams' `SlotPriority`) — omitted for simplicity; `IsRequired` on the role definition is the only criticality signal.
- Public visibility per role — all role assignments are visible to any authenticated human; none visible to anonymous visitors. No per-role `IsPublic` flag.
- Auto-promotion of assignee to `CampLead`. This feature does not interact with the existing `CampLead` role.
- Email notifications. In-app only (matches PR #228's membership notification channel).

## Architecture

Follows §15 Application-layer repository pattern already in place for Camps (see `docs/sections/Camps.md`).

| Layer | Changes |
|---|---|
| `Humans.Domain/Entities` | New `CampRoleDefinition`, `CampRoleAssignment`. |
| `Humans.Domain/Enums` | New `AuditAction` values: `CampRoleDefinitionCreated`, `CampRoleDefinitionUpdated`, `CampRoleDefinitionDeactivated`, `CampRoleDefinitionReactivated`, `CampRoleAssigned`, `CampRoleUnassigned`. New `NotificationSource.CampRoleAssigned` mapped to `MessageCategory.TeamUpdates`. |
| `Humans.Application/Interfaces/Repositories/ICampRepository` | Extend with role-definition CRUD + assignment persistence methods. No separate `ICampRoleRepository` — extends the existing camp interface, matching how `TeamRepository` handles both teams and role defs. |
| `Humans.Application/Services/Camps/CampService` | Add role-def CRUD, per-slot assign/unassign (with auto-promote transaction), compliance-report query. Inline — matches the house style of `TeamService` (2,353 lines) and existing `CampService` (1,330 lines). Estimated final size ~1,800 lines. |
| `Humans.Infrastructure/Data/Configurations` | `CampRoleDefinitionConfiguration`, `CampRoleAssignmentConfiguration`. |
| `Humans.Infrastructure/Migrations` | One migration: creates the two tables, seeds 6 global role definitions. |
| `Humans.Web/Controllers/CampAdminController` | New actions: `Roles` (list/create/edit/reorder/deactivate), `Compliance` (required-role report). |
| `Humans.Web/Controllers/CampController` | New actions on camp edit: `AssignRole`, `UnassignRole`. |
| `Humans.Web/Views/CampAdmin/Roles.cshtml` | New. |
| `Humans.Web/Views/CampAdmin/Compliance.cshtml` | New. |
| `Humans.Web/Views/Camp/Edit.cshtml` | Add "Roles" section after Leads / Members. |
| `Humans.Web/Views/Camp/Details.cshtml` | Add "Roles" section (authenticated-only). |
| `docs/features/20-camps.md`, `docs/sections/Camps.md` | Updated with data model + invariants. |

No new top-level routes. Two new CampAdmin sub-nav entries (Roles, Compliance) per the CLAUDE.md "no orphan pages" rule.

## Data model

### `CampRoleDefinition` (global)

```csharp
public class CampRoleDefinition
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;         // Unique, non-empty.
    [MarkdownContent]
    public string? Description { get; set; }

    public int SlotCount { get; set; } = 1;                  // >= 1
    public int MinimumRequired { get; set; } = 1;            // 0 <= MinimumRequired <= SlotCount
    public int SortOrder { get; set; }

    public bool IsRequired { get; set; }                     // Drives compliance report

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public Instant? DeactivatedAt { get; set; }              // Soft delete; preserves history

    public ICollection<CampRoleAssignment> Assignments { get; } = new List<CampRoleAssignment>();
}
```

No `IsPublic` — access is strictly anonymous / authenticated.
No `Priorities` / `SlotPriority` — omitted.
No `IsManagement` — not applicable to camps.

### `CampRoleAssignment` (per camp-season, per slot)

```csharp
public class CampRoleAssignment
{
    public Guid Id { get; init; }

    public Guid CampRoleDefinitionId { get; init; }
    public CampRoleDefinition CampRoleDefinition { get; set; } = null!;

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public Guid CampMemberId { get; init; }                  // FK → CampMember (PR #228)
    public CampMember CampMember { get; set; } = null!;

    public int SlotIndex { get; init; }                      // 0-based

    public Instant AssignedAt { get; init; }
    public Guid AssignedByUserId { get; init; }              // No nav prop — resolve via IUserService
}
```

### Indexes

- **Unique** `(CampSeasonId, CampRoleDefinitionId, SlotIndex)` — one occupant per slot.
- Lookup `(CampSeasonId)` — drives camp edit and camp detail renders.
- Lookup `(CampMemberId)` — drives "remove member" cascade.
- Lookup `(CampRoleDefinitionId)` — drives the compliance-report join path. Confirm during planning whether `CampSeason(Status, Year)` is already indexed (likely added by an earlier camp PR); if not, add it in this migration.

- **Unique** `(CampSeasonId, CampRoleDefinitionId, CampMemberId)` — same human cannot hold two slots of the same role in the same season. Cross-role assignments (e.g., Alice as Consent Lead + LNT) remain unrestricted.

### EF behavior

- `AssignedByUserId` — no nav prop (per design-rules §6c). Configure FK as `OnDelete(DeleteBehavior.Restrict)`, mirroring `TeamRoleAssignment.AssignedByUserId`. The migration reviewer will flag missing FK behavior otherwise.
- `CampMemberId` — `OnDelete(DeleteBehavior.Cascade)` is intentional (matches the workflow rule "remove `CampMember` → delete role assignments"); no manual cascade needed in `CampService`.
- `CampSeasonId` and `CampRoleDefinitionId` — `OnDelete(DeleteBehavior.Restrict)` (the season-cascade is handled at the service layer to also write audit rows; the role-def deactivation is soft-only).
- Repository query that loads assignments for a camp-season MUST order by `(CampRoleDefinition.SortOrder, CampRoleAssignment.SlotIndex)` — the UI assumes this order when laying out slots.

### DI wiring

No new interfaces or services are introduced. `ICampRepository` and `CampService` gain new methods inline; their existing DI registrations cover them. No `Program.cs` changes.

### Seed data

Seeded by migration (idempotent — skips if a def with the same `Name` already exists):

| Name | IsRequired | SlotCount | MinimumRequired | SortOrder |
|---|---|---|---|---|
| Consent Lead | true | 2 | 1 | 10 |
| Wellbeing Lead | true | 1 | 1 | 20 |
| LNT | true | 1 | 1 | 30 |
| Shit Ninja | true | 1 | 1 | 40 |
| Power | false | 1 | 0 | 50 |
| Build Lead | true | 2 | 1 | 60 |

`MinimumRequired < SlotCount` for **Consent Lead** and **Build Lead** captures the intent that small barrios fill one slot and bigger barrios fill both — compliance passes once the first slot is filled.

## Workflows

### Assign to slot

1. Camp Lead opens camp edit page; "Roles" section lists every **active** role definition.
2. For each empty slot, an autocomplete picker shows humans searchable by display name, scoped to the current camp-season's `CampMember`s with `Status = Active`, with an affordance *"…not in the list? Add someone"* that opens the auto-promote modal.
3. On submit:
   - **Human is Active CampMember of this season** → insert `CampRoleAssignment`, write `CampRoleAssigned` audit entry, fire `CampRoleAssigned` in-app notification to the assignee (`MessageCategory.TeamUpdates`). Notification body: "{LeadDisplayName} added you as {RoleName} for {CampName}." Click-through links to the camp detail page (`/Camps/{slug}`).
   - **Human is not yet a member, or is `Pending`, or was `Removed`** → auto-promote modal ("Add {name} as a camp member?"). On confirm:
     - In a single transaction: upsert to `CampMember(Status = Active, ConfirmedByUserId = currentLead, ConfirmedAt = now)` — insert new row if none, or mutate existing Pending/Removed row.
     - Insert `CampRoleAssignment`.
     - Audit: `CampMembershipApproved` + `CampRoleAssigned`.
     - Notifications: `CampMembershipApproved` + `CampRoleAssigned`.
4. Server-side validation (defense-in-depth beyond UI):
   - Reject if `SlotIndex >= SlotCount` (except orphan high-slot rendering, which is read-only).
   - Reject if slot already occupied (unique index backstop + friendly message).

### Unassign from slot

- Camp Lead clicks × on a filled slot → delete the `CampRoleAssignment`.
- No notification to the (former) assignee (silent unassign matches the Teams pattern).
- `CampMember` row is not affected.
- Audit: `CampRoleUnassigned`.

### Cascading auto-cleanup

| Trigger | Effect |
|---|---|
| `CampMember` → `Removed` | Delete all `CampRoleAssignment` rows for that member in that season. Audit: one `CampRoleUnassigned` row per deleted assignment with a `cascade=member-removed` reason in the metadata. |
| `CampSeason.Status` → `Rejected` or `Withdrawn` | Delete all `CampRoleAssignment` rows for that season. Audit: one `CampRoleUnassigned` row per deleted assignment with a `cascade=season-{rejected|withdrawn}` reason in the metadata (consistent with member-removal: row-per-deletion, never batched). |
| `CampRoleDefinition.DeactivatedAt` set | No cascade. Existing assignments remain visible; definition hidden from new-assignment UI and excluded from compliance report. |

### `SlotCount` lowered below current usage

Permitted. Existing assignments with `SlotIndex >= newSlotCount` remain visible on the camp edit page with a "slot {i+1} of {newSlotCount} (being phased out)" label and only an unassign action. The CampAdmin Roles edit form shows a warning when the new SlotCount is below the maximum used slot index. Compliance report treats a role as "filled" if any row exists — slot index doesn't matter for compliance.

### Deactivating a role definition

CampAdmin toggles deactivation on the global role. Existing assignments remain readable on the camp detail page (role name italicized to signal retired status). Reactivating clears `DeactivatedAt`.

### Required-role compliance report

CampAdmin page at `/CampAdmin/Compliance`. For each `CampSeason` with `Status = Active` and `Year = CampSettings.PublicYear`, list required (non-deactivated) role definitions where `COUNT(CampRoleAssignment) < MinimumRequired`. A role with `IsRequired = false` is ignored regardless of fill state. Links through to each camp's edit page.

`CampSettings.PublicYear` is an `int` matching `CampSeason.Year` directly — same scalar comparison, no off-by-one at year transitions. The report scoped to a single year by design; cross-year compliance is out of scope.

## Authorization

All gates use the existing `CampAuthorizationHandler`. No `isPrivileged` booleans.

| Action | Allowed roles |
|---|---|
| CRUD role definitions (create / edit / reorder / deactivate / reactivate) | CampAdmin, Admin |
| Assign / unassign a role slot on a camp | Camp Lead (of that camp), CampAdmin, Admin |
| Auto-promote non-member to `CampMember` + assign | Same as above (single operation) |
| View required-role compliance report | CampAdmin, Admin |
| View role assignments (camp edit page and `/Camps/{slug}`) | Any authenticated human |
| View role assignments (anonymous) | Not permitted — role section is not rendered at all |

**Negative rules (explicit for section invariants):**
- Camp leads cannot CRUD role definitions — CampAdmin/Admin only.
- Regular humans (non-lead, non-CampAdmin, non-Admin) cannot assign roles, even for camps they're members of.
- Anonymous visitors see no role section on any page.
- Deactivated role defs are invisible in assignment pickers regardless of requester role.

## UI surfaces

### New — `/CampAdmin/Roles`

Table of all role definitions (deactivated shown greyed out):

- Columns: Name, Required (✓/—), Slots, Sort, Status, Actions.
- Actions: Edit, Reorder (↑/↓), Deactivate / Reactivate. No hard delete.
- "Add role" button → modal: Name, Description (Markdown), `SlotCount`, `IsRequired`, `SortOrder`.
- Edit form warns if lowering `SlotCount` below current max-used-slot-index across any camp.

### New — `/CampAdmin/Compliance`

Table: "Camps missing required roles — year {publicYear}". Columns: Camp, Season status, Missing roles (chips). Only renders when `PublicYear` has any Active camp-seasons.

### Page split (recap)

| Page | Hosts |
|---|---|
| `/Camps/{slug}` (detail) | Approve/Reject pending join requests (lead/CampAdmin/Admin only); read-only Roles section (any authenticated human). |
| `/Camps/{slug}/Edit` | Active-member roster with Remove action; per-slot Role assignment UI (this feature). No approve/reject panel here. |

(The first row's relocation of approve/reject is a [PR #228 fix](https://github.com/peterdrier/Humans/pull/228) — tracked separately, not part of #489's scope.)

### Updated — `Views/Camp/Edit.cshtml`

New "Roles" section after the existing Leads / Members sections. Renders only if the camp has an Active season for the current `PublicYear`; otherwise shows a "No open season — roles unavailable" stub.

For each active role definition (ordered by `SortOrder`):
- Header: role name, required/optional badge, slot-usage counter ("2 / 2 filled").
- One row per slot `0..SlotCount-1`:
  - Filled: assignee display name + × unassign button.
  - Empty: autocomplete picker + "…Add someone" affordance (auto-promote modal).
- Orphan high-slot rows (when `SlotCount` was lowered): shown read-only with unassign only.

### Updated — `Views/Camp/Details.cshtml`

New "Roles" section; rendered only if `User.Identity.IsAuthenticated`. For the current `PublicYear` season only:
- For each role definition with ≥1 assignment (active or deactivated): role name, then assignees as comma-separated display names linked to each human's profile.
- Roles with zero assignments are not rendered.
- Deactivated roles with historical assignments render with italic role name.

### Auto-promote modal

Title: "Add {name} as a camp member?"
Body: "{name} isn't listed as a member of this barrio yet. Adding them will also mark them Active — they'll get a notification. Assign the role?"
Buttons: Add & assign / Cancel.
Single POST that performs both mutations in one transaction.

### Nav links

- CampAdmin sub-nav gains **Roles** and **Compliance** entries.
- No new top-level or sidebar items.
- Camp edit and detail pages gain inline sections (no new routes there).

## Tests

### Unit tests (`Humans.Application.Tests` → new `CampRoleTests.cs`)

**Role definition CRUD**
- Create with valid fields persists and appears in `GetActiveRoleDefinitionsAsync`.
- Create rejects duplicate `Name`.
- Edit updates `UpdatedAt`.
- Deactivate sets `DeactivatedAt`; def hidden from active listings.
- Reactivate clears `DeactivatedAt`.
- Lowering `SlotCount` succeeds and does not delete existing assignments.

**Slot assignment**
- Assign to empty slot writes `CampRoleAssignment` + audit log + notification.
- Assign to occupied slot fails with a friendly error.
- Assign with `SlotIndex >= SlotCount` rejected (defense-in-depth).
- Same member **cannot** hold two slots of the same role in one season — DB-level uniqueness backstop + service-level error.
- Same member **can** hold slots across multiple distinct roles in one season.

**Auto-promote to CampMember**
- Non-member → creates `CampMember(Active)` + assignment in one transaction, fires both notifications.
- Already-Active member → no duplicate CampMember row.
- Pending member → transitions to Active, fires `CampMembershipApproved` + `CampRoleAssigned`.
- Removed member → creates a new `CampMember(Active)` row (matches PR #228 re-request pattern).

**Cascades**
- Remove `CampMember` → all their assignments for that season deleted.
- Season → Rejected → all assignments for that season deleted.
- Season → Withdrawn → all assignments for that season deleted.
- Deactivate role def → existing assignments preserved.

**Compliance report**
- Camp with all required roles having `COUNT >= MinimumRequired`: not listed.
- Camp with one required role at `COUNT < MinimumRequired`: listed with that role in missing-chips.
- Required role with `MinimumRequired = 1` and `SlotCount = 2`, only first slot filled: passes (e.g., Consent Lead in a small barrio).
- Required role with `MinimumRequired = 1` and `SlotCount = 2`, neither slot filled: fails.
- Optional (`IsRequired = false`) roles, regardless of fill: not listed.
- Non-Active seasons (Pending/Rejected/Withdrawn): not listed.
- Deactivated required roles: ignored by the report.

### Integration tests (`Humans.Integration.Tests`, Testcontainers)

- Migration applies cleanly against the QA schema snapshot; 6 seed role defs exist post-migration.
- Unique index `(CampSeasonId, CampRoleDefinitionId, SlotIndex)` enforced at DB level.
- End-to-end: create camp → approve season → assign roles → withdraw season → assignments gone.

### Authorization tests (`Humans.Web.Tests`)

- Non-CampAdmin → 403 on `/CampAdmin/Roles` CRUD.
- Non-lead → 403 on slot-assign for a camp they don't lead.
- **Lead of camp A** trying to assign on **camp B** they don't lead → 403 (cross-camp authorization check, not just "is any kind of lead").
- Anonymous request of `/Camps/{slug}` does not render the role section.
- Authenticated non-lead request of `/Camps/{slug}` renders all role assignments.

## Docs to update (in-branch, per CLAUDE.md post-fix doc check)

- `docs/features/20-camps.md` — append a "Per-camp role assignments" subsection with data model, workflow, authz table, compliance rules.
- `docs/sections/Camps.md` — add invariants:
  - "Role assignments require the assignee to be an Active `CampMember` of the same season (auto-promoted if not)."
  - "Role-definition CRUD is CampAdmin-only; camp leads can only fill / empty slots."
  - "Role assignments are hidden from anonymous visitors."
  - "Deactivating a role def preserves historical assignments."
  - "Cascades: member-remove and season-reject/withdraw clear all related role assignments."
- `docs/architecture/data-model.md` — list the two new entities.

Closes nobodies-collective/Humans#489 on PR merge.

## Manual smoke plan (for PR body)

- Build clean, format clean, application tests green, EF migration reviewer green with no CRITICAL issues.
- Preview env: sign in as CampAdmin → open `/CampAdmin/Roles` → add a test role → reorder → deactivate → reactivate.
- Sign in as camp lead → assign an existing Active member to a role → verify notification → unassign.
- Assign a non-member → confirm auto-promote modal → check both `CampMembershipApproved` and `CampRoleAssigned` notifications fire.
- Withdraw the camp's season → verify all role assignments cleared.
- Sign out → verify no role section renders anywhere.
- Sign in as an unrelated authenticated human → verify role section renders on `/Camps/{slug}`.
