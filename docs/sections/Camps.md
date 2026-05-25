<!-- freshness:triggers
  src/Humans.Application/Services/Camps/**
  src/Humans.Domain/Entities/Camp.cs
  src/Humans.Domain/Entities/CampSeason.cs
  src/Humans.Domain/Entities/CampLead.cs
  src/Humans.Domain/Entities/CampMember.cs
  src/Humans.Domain/Entities/CampImage.cs
  src/Humans.Domain/Entities/CampHistoricalName.cs
  src/Humans.Domain/Entities/CampSettings.cs
  src/Humans.Infrastructure/Data/Configurations/Camps/**
  src/Humans.Infrastructure/Data/Configurations/Camps/CampMemberConfiguration.cs
  src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs
  src/Humans.Infrastructure/Repositories/Camps/CampRoleRepository.cs
  src/Humans.Web/Controllers/CampController.cs
  src/Humans.Web/Controllers/CampAdminController.cs
  src/Humans.Web/Controllers/CampApiController.cs
  src/Humans.Web/Authorization/Requirements/CampAuthorizationHandler.cs
  src/Humans.Web/Authorization/Requirements/CampOperationRequirement.cs
-->
<!-- freshness:flag-on-change
  Camp/Season lifecycle, lead/membership authorization, public-year settings, and notification triggers — review when Camp services/entities/controllers/auth handlers change.
-->

# Camps — Section Invariants

Themed community camps (Barrios) with per-year season registrations, leads, images, and renaming history.

## Concepts

- A **Camp** (also called "Barrio") is a themed community camp. Each camp has a unique URL slug, one or more leads, and optional images.
- A **Camp Season** is a per-year registration for a camp, containing the year-specific name, description, community info, and placement details.
- A **Camp Lead** is a human responsible for managing a camp. Lead authorization flows through a `CampRoleAssignment` against the `CampRoleDefinition` whose `SpecialRole = CampSpecialRole.Lead`. The legacy `CampLead` entity / `camp_leads` table remains in place transitionally — the auth handler dual-sources from both during the Camp Lead retirement (issue nobodies-collective/Humans#753); a follow-up PR drops the legacy entity and table once all environments have run the "Seed system roles" admin button.
- A **Workshop Lead** is a human authorized to submit camp events on behalf of their camp via `BarrioEventsController` (`/Barrios/{slug}/Events/*`), without inheriting general camp-management authority. Authority flows through a `CampRoleAssignment` against the `CampRoleDefinition` whose `SpecialRole = CampSpecialRole.Workshop`. Camp Leads automatically have Workshop authority because the event-management check is the OR of {Lead, Workshop} — no separate inheritance link.
- A **Camp Member** is a human's post-hoc, per-season affiliation with a camp. The app does **not** admit humans to a camp — each camp runs its own process. A CampMember row exists so the app knows who belongs to which camp for per-camp roles (e.g. LNT lead), Early Entry allocations, and notifications. Status: Pending → Active → Removed. `Removed` is a soft-delete tombstone so re-requesting creates a new row.
- A **Camp Role Definition** is a CampAdmin-managed catalogue row describing a per-camp role with a slot count, compliance threshold (`MinimumRequired`), and sort order. `MinimumRequired = 0` means the role is optional and not tracked in the compliance report; `MinimumRequired ≥ 1` means the compliance report tracks it with that threshold. The catalogue ships empty — CampAdmin creates every definition. Soft-deleted via `DeactivatedAt` so historical assignments survive removal from the active catalogue.
- A **Camp Role Assignment** is a per-season binding of a `CampMember` to a `CampRoleDefinition`. "Camp Lead" and "Workshop Lead" **are** `CampRoleDefinition` rows (special, `SpecialRole != None`); the legacy `CampLead` entity is retained transitionally and read as a fallback by the auth handler until the follow-up table-drop PR lands.
- **Camp Settings** is a singleton controlling which year is public (shown in the directory) and which seasons accept new registrations.

## Data Model

### Camp

Core entity: contact info, slug, flags.

**Table:** `camps`

Cross-domain nav `Camp.CreatedByUser` is declared on the entity but never read by Camps code. Pre-existing; tracked for cross-cutting cleanup with the User nav strip in design-rules §15i.

### CampSeason

Per-year season data (name, blurbs, community info, placement). `EeSlotCount` (int, default 0) tracks the Early Entry slot cap for the season; managed by CampAdmin.

**Table:** `camp_seasons`

Cross-domain nav `CampSeason.ReviewedByUser` is declared on the entity but never read by Camps code. Pre-existing; tracked for cross-cutting cleanup.

### CampLead

Lead assignments with Primary or CoLead roles.

**Table:** `camp_leads`

Cross-domain nav `CampLead.User` is **stripped** (PR for issue nobodies-collective/Humans#542). Lead display names resolve via `IUserService.GetByIdsAsync`.

### CampImage

Image metadata; files are stored on disk via the shared `IFileStorage` abstraction (key `uploads/camps/{campId}/{guid}{.ext}`, served as static files at `/uploads/camps/...`). Display order is tracked per camp.

**Table:** `camp_images`

### CampHistoricalName

Name history for tracking renames.

**Table:** `camp_historical_names`

### CampSettings

Singleton settings: public year, open seasons, and Early Entry start date. `EeStartDate` (LocalDate?, nullable) is the global date from which EE access begins; set by CampAdmin/Admin. Name-lock dates are per-season fields on `CampSeason` (`NameLockDate`, `NameLockedAt`), not here.

**Table:** `camp_settings`

### CampMember

Per-season, post-hoc human/camp affiliation. Status: Pending → Active → Removed (soft-delete tombstone). Partial unique index on `(CampSeasonId, UserId) WHERE Status <> 'Removed'` so removed rows retain audit history and allow re-requesting.

**Table:** `camp_members`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid | FK → CampSeason |
| UserId | Guid | FK → User (scalar; no nav per §6) |
| Status | CampMemberStatus | Pending, Active, Removed |
| RequestedAt | Instant | When the request was created |
| ConfirmedAt | Instant? | Set on approve |
| ConfirmedByUserId | Guid? | Lead who approved (scalar) |
| RemovedAt | Instant? | Set on remove/withdraw/leave/reject |
| RemovedByUserId | Guid? | Actor who closed the row (scalar) |
| HasEarlyEntry | bool | Default false; cleared on Removed transition |

### CampRoleDefinition

CampAdmin-managed catalogue of per-camp roles. Soft-deleted via `DeactivatedAt`; historical assignments survive deactivation. Owned by `CampRoleService` (separate from `CampService`).

**Table:** `camp_role_definitions`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string | Unique (case-insensitive) |
| Slug | string | Kebab-case identifier, unique case-insensitive, NOT NULL. Used in `/Camps/Admin/Roles/{slug}` URLs and as the per-role component of the derived Google Group key (`barrios-{year}-{slug}@{domain}`). Backfilled from `Name` in migration `20260517140206_AddCampRoleSlug` (issue nobodies-collective/Humans#740). |
| Description | string? | Markdown |
| SlotCount | int | Default 1; soft cap enforced in service, not in DB |
| MinimumRequired | int | Default 1; cross-field validation enforces `0 ≤ MinimumRequired ≤ SlotCount` |
| SortOrder | int | Display order on Camp Edit roles panel |
| DeactivatedAt | Instant? | Null = active; non-null hides from new-assignment UI |
| SpecialRole | CampSpecialRole | Default `None`. Marker for special, system-managed role definitions (`Lead`, `Workshop`) seeded by the CampAdmin "Seed system roles" action (issue nobodies-collective/Humans#753). `CampRoleService` rejects rename / slug change / sort-order change / min-required change / deactivation when `SpecialRole != None`; only `SlotCount` and `Description` are admin-mutable. Stored as string via `HasConversion<string>()`; column default `'None'` backfills existing rows on the AddColumn migration. |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

Aggregate-local nav: `CampRoleDefinition.Assignments` (back-ref).

### CampRoleAssignment

Per-season binding of a `CampMember` to a `CampRoleDefinition`.

**Table:** `camp_role_assignments`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid | FK → CampSeason (`OnDelete(Cascade)`) |
| CampRoleDefinitionId | Guid | FK → CampRoleDefinition (`OnDelete(Restrict)` — deactivate, don't delete) |
| CampMemberId | Guid | FK → CampMember (`OnDelete(Cascade)` — hard-delete cascades; soft-delete cleared in service) |
| AssignedAt | Instant | |
| AssignedByUserId | Guid | Scalar; no nav per design-rules §6 |

**Unique index:** `(CampSeasonId, CampRoleDefinitionId, CampMemberId)` — a human cannot hold the same role twice in the same season.

Aggregate-local navs: `CampRoleAssignment.CampSeason`, `CampRoleAssignment.Definition`, `CampRoleAssignment.CampMember` (all within the Camps section).

### Camp enums

| Enum | Values |
|------|--------|
| CampSeasonStatus | Pending, Active, Full, Rejected, Withdrawn |
| CampLeadRole | Primary, CoLead |
| CampMemberStatus | Pending, Active, Removed |
| CampSpecialRole | None, Lead, Workshop |
| CampVibe | Adult, ChillOut, ElectronicMusic, Games, Queer, Sober, Lecture, LiveMusic, Wellness, Workshop |
| CampNameSource | Manual, NameChange |
| YesNoMaybe | Yes, No, Maybe |
| KidsVisitingPolicy | Yes, DaytimeOnly, No |
| PerformanceSpaceStatus | Yes, No, WorkingOnIt |
| AdultPlayspacePolicy | Yes, No, NightOnly |
| SpaceSize | Sqm150, Sqm300, Sqm450, Sqm600, Sqm800, Sqm1000, Sqm1200, Sqm1500, Sqm1800, Sqm2200, Sqm2800 |
| SoundZone | Blue, Green, Yellow, Orange, Red, Surprise |
| ElectricalGrid | Yellow, Red, Norg, OwnSupply, Unknown |

All stored as strings via `HasConversion<string>()`. `Vibes` stored as jsonb array.

## Routing

Three controllers serve this section. The MVC URL surface is dual-routed under `/Camps/*` (English) and `/Barrios/*` (Spanish); the API surface is dual-routed under `/api/camps/*` and `/api/barrios/*`. The dual-route alias is governed by an invariant below — no other section may add aliases.

| Route | Controller | Purpose |
|-------|------------|---------|
| `/Camps` | `CampController` | Public directory |
| `/Camps/{slug}` | `CampController` | Camp detail (current season, leads, images, history) |
| `/Camps/{slug}/Season/{year}` | `CampController` | Past-season detail |
| `/Camps/{slug}/Contact` | `CampController` | Facilitated message to camp leads |
| `/Camps/{slug}/Edit` | `CampController` | Lead-only edit of season copy / images / leads (links through to Members for role/membership management) |
| `/Camps/{slug}/Edit/Members` | `CampController.Members` | Lead-only members + roles management (pending requests, active members, role assignments) |
| `/Camps/Register` | `CampController` | New camp registration |
| `/Camps/{slug}/OptIn/{year}`, `.../Withdraw/{seasonId}`, `.../Rejoin/{seasonId}` | `CampController` | Per-season participation toggles |
| `/Camps/{slug}/Leads/*` | `CampController` | Lead add/remove |
| `/Camps/{slug}/Members/*` | `CampController` | Member request/approve/reject/remove/leave |
| `/Camps/{slug}/Roles/*` | `CampController` | Per-camp role assignment/unassignment |
| `/Camps/{slug}/Images/*` | `CampController` | Image upload/delete/reorder |
| `/Camps/{slug}/HistoricalNames/*` | `CampController` | Historical-name add/remove |
| `/Camps/Admin` | `CampAdminController` | CampAdmin-only directory + season management |
| `/Camps/Admin/Roles/*` | `CampAdminController` | `CampRoleDefinition` CRUD |
| `/Camps/Admin/Roles/{slug}` | `CampAdminController.RolesDrillDown` | Cross-camp roster for one role definition (issue nobodies-collective/Humans#740): per-camp-season assignees with name + Google email and a `mailto:` to the derived group email; year-picker drop-down. CampAdmin only. |
| `/Camps/Admin/Compliance` | `CampAdminController` | Per-season role compliance report |
| `/Camps/Admin/Export` | `CampAdminController` | CSV export |
| `/Camps/Admin/{Approve,Reject,OpenSeason,CloseSeason,SetPublicYear,SetNameLockDate,Reactivate,UpdateRegistrationInfo,Delete}/...` | `CampAdminController` | Season lifecycle actions |
| `/api/camps/{year}` | `CampApiController` | Year directory JSON |
| `/api/camps/{year}/placement` | `CampApiController` | Placement-data JSON |
| `/Camps/{slug}/Members/{campMemberId}/EarlyEntry` | `CampController` | Grant / revoke EE on a camp member |
| `/Camps/Admin/SetCampSeasonEeSlotCount/{seasonId}` | `CampAdminController` | Set a season's EE slot cap |
| `/Camps/Admin/SetEeStartDate` | `CampAdminController` | Set the global EE start date |

Admin pages live under `/Camps/Admin/*` — never `/Admin/Camps/*` (per `docs/architecture/design-rules.md` § "Admin is not a section": `/Admin/*` is a nav holder for actions whose services live in their owning sections).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | Browse the camps directory, view camp details and season details |
| Any authenticated human | Register a new camp (which creates a new season in Pending status). Request to join a camp for its open season; withdraw their own pending request; leave their own active membership. |
| Camp lead | Edit their camp's details, manage season registrations, manage co-leads, upload/manage images, manage historical names. Approve / reject pending membership requests for their camp. Remove active members. Add an active member directly to their camp (lead-driven shortcut). Assign / unassign per-camp role assignments for their camp. |
| CampAdmin, Admin | All camp lead capabilities on all camps. Approve/reject season registrations. Reactivate a Full or Withdrawn season. Manage camp settings (public year, open seasons, name lock dates). Update registration info copy. View withdrawn seasons on the admin dashboard. Export camp data as CSV. Manage the role-definition catalogue (create, edit, deactivate, reactivate). View the required-role compliance report. |
| Admin | Delete camps |

## Invariants

- Each camp has a unique slug used for URL routing.
- Camp season status follows: Pending then Active, Full, Rejected, or Withdrawn. Only CampAdmin can approve or reject a season.
- Only camp leads or CampAdmin can edit a camp.
- Camp images are stored on disk via the shared `IFileStorage` abstraction (key prefix `uploads/camps/{campId}/`); metadata and display order are tracked per camp.
- Historical names are recorded when a camp is renamed.
- Camp settings control which year is shown publicly and which seasons accept registrations.
- Resource-based authorization per design-rules §11: `CampAuthorizationHandler` + `CampOperationRequirement` gate all admin writes.
- Membership is **per-season**. One live (`Pending`/`Active`) row per `(CampSeasonId, UserId)` enforced by a partial unique index. `Removed` rows are kept for audit and do not block re-requests.
- Membership mutations (approve, reject, remove) are **scoped to the authorizing camp**. A lead or CampAdmin operating on camp A cannot mutate a member row whose season belongs to camp B even if they know the row id.
- Membership state is **never rendered on anonymous or public views**. It is only shown to the human themselves and to leads/CampAdmin of the camp.
- A `CampRoleAssignment` requires the linked `CampMember` to have `Status = Active` for the same `CampSeasonId`. Service rejects with `MemberNotActive` or `MemberSeasonMismatch` otherwise.
- A human cannot hold the same role twice in the same season — enforced by unique index on `(CampSeasonId, CampRoleDefinitionId, CampMemberId)`.
- All role-assignment data is private (no anonymous render). The public Camp Details page does not expose role assignments.
- Leave/Withdraw/Remove cascades clear role assignments via `ICampRoleService.RemoveAllForMemberAsync` before the soft-delete. Hard-delete of a `CampMember` row cascades through the FK directly.
- Camp Lead authz flows through `CampRoleAssignment` against the Camp Lead role definition (`SpecialRole = Lead`). The legacy `CampLead` entity is read as a transitional fallback by `CampService.IsUserCampLeadAsync` / `GetCampLeadSeasonIdForYearAsync` / `GetPendingMembershipCountForLeadAsync` until the follow-up table-drop PR lands.
- Camp-event submission authz (`BarrioEventsController` at `/Barrios/{slug}/Events/*`) flows through `ICampService.IsUserCampEventManagerAsync` — true when the user holds a `CampRoleAssignment` whose `CampRoleDefinition.SpecialRole` is `Lead` OR `Workshop` (CampAdmin / Admin retain blanket authority). Camp Leads automatically satisfy the check; Workshop Leads do not gain general camp-management authority. Moderation of submitted events remains global GuideModerator / Admin.
- The `/Camps ↔ /Barrios` and `/api/camps ↔ /api/barrios` dual-route aliases are the **only sanctioned URL aliases in the codebase**. No other section may add URL aliases without explicit owner approval.
- Early Entry slot count is per-season (`CampSeason.EeSlotCount`, CampAdmin-managed). The EE start date is global per year (`CampSettings.EeStartDate`).
- A `CampMember.HasEarlyEntry` grant requires `Status = Active`. Granting beyond `EeSlotCount` is rejected; lowering `EeSlotCount` below current grants is allowed (no auto-revoke; overflow flagged in UI).
- Member-removal transitions (Remove / Leave / Withdraw / Reject) clear `HasEarlyEntry` in the same `SaveChangesAsync` as the status flip.
- EE state is **never** rendered on anonymous or public views — only on `/Camps/Admin` and `/Camps/{slug}/Edit/Members` for CampAdmin/leads.

## Negative Access Rules

- Regular humans **cannot** edit camps they do not lead.
- Camp leads **cannot** approve or reject season registrations — that requires CampAdmin or Admin.
- CampAdmin **cannot** delete camps. Only Admin can delete a camp.
- Anonymous visitors **cannot** register camps or edit any camp data.
- Anonymous visitors **cannot** see role assignments — the public Camp Details page does not render the roles section.
- Camp leads **cannot** manage the role-definition catalogue (create, edit, deactivate). Only CampAdmin or Admin can.
- A camp lead **cannot** assign or unassign roles on a camp other than their own (controller verifies `assignment.CampSeasonId` is in the set of season IDs on the resolved `CampLookup` before delegating to the service).
- Anyone **cannot** assign a role to a human who is not an Active CampMember of the same season — service rejects with `MemberNotActive` / `MemberSeasonMismatch`.
- Camp leads **cannot** edit `EeSlotCount` (CampAdmin/Admin only).
- Anyone **cannot** grant EE to a non-Active member (service rejects with `MemberNotActive`).

## Triggers

- When a camp is registered, its initial season is created with Pending status.
- Season approval or rejection is performed by CampAdmin.
- Approving a membership request sends a `CampMembershipApproved` notification to the requester.
- Rejecting a membership request sends a `CampMembershipRejected` notification to the requester.
- When a season is rejected or withdrawn, pending requesters receive a `CampMembershipSeasonClosed` notification. Their membership rows are **not** auto-mutated — the notification is the only side effect, so if the season is later reactivated the request is still live.
- Camp leads do **not** receive a per-request stored notification when humans request to join. Instead a `NotificationMeter` ("N humans want to join your camp") shows the live pending count; it updates immediately on approve/reject/withdraw and drops to zero when the season is closed.
- Active leads appear in the camp's active-members list automatically, tagged with an `IsLead` flag. They do not need a `CampMember` row to be shown as part of the camp.
- When a CampMember is removed (Leave / Withdraw / Remove paths set `RemovedAt`), `ICampService` calls `ICampRoleService.RemoveAllForMemberAsync` before the soft-delete to clear any role assignments held by that member.
- When a lead uses the "add active member" shortcut at `/Camps/{slug}/Members/Add`, `ICampService.AddCampMemberAsLeadAsync` creates `CampMember(Status=Active)` directly and writes a `CampMemberAddedByLead` audit entry.
- Assigning a per-camp role writes a `CampRoleAssigned` audit entry and sends a best-effort `CampRoleAssigned` notification to the assignee. Unassign writes `CampRoleUnassigned` and does **not** notify.
- Definition CRUD (`CampRoleDefinitionCreated` / `Updated` / `Deactivated` / `Reactivated`) writes audit entries; ordering is `repo.Add` then `SaveChangesAsync` then `auditLog.LogAsync`.
- When an account merge accepts, `ICampService.ReassignAssignmentsToUserAsync` re-FKs `CampLead.UserId` and `CampRoleAssignment.AssignedByUserId` from source to target. Called only by `IAccountMergeService.AcceptAsync` (Profiles section). **Known gap:** `CampMember.UserId` is **not** currently folded — `CampMember` rows attached to a source remain attributed to the tombstoned source after merge.
- Granting / revoking EE writes `CampEarlyEntryGranted` / `CampEarlyEntryRevoked` audit entries. Idempotent set writes no audit row.
- Changing `EeSlotCount` writes `CampSeasonEeSlotCountChanged`; changing `EeStartDate` writes `CampSettingsEeStartDateChanged`.

## Cross-Section Dependencies

- **Users/Identity:** `IUserService.GetByIdsAsync` — lead and assignee display names (stitched in memory after `CampLead.User` strip; `CampRoleAssignment.AssignedByUserId` is scalar-only).
- **Admin:** Camp settings management is restricted to CampAdmin and Admin (resource-based auth handler).
- **City Planning:** CampSeason is the anchor for `camp_polygons`; City Planning reads camp data via `ICampService` but writes its own tables only.
- **Containers:** `Camp` is read by the Containers section via FK (`Container.CampId → camps.Id`) and via `ICampService` (`IsUserCampLeadAsync`, `GetCampBySlugAsync`, `GetCampsForYearAsync`, `GetCampsWithLeadsForYearAsync`, etc.) for authorization and display. Containers are year-agnostic and have no `CampSeasonId`. Camps does not depend on Containers — this is a downstream dependency only.
- **Camps internal — `CampRoleService` ↔ `CampService`:** `CampRoleService` calls `ICampService` for camp/season lookup and active-membership verification, and is called back by `ICampService` from the Leave/Withdraw/Remove paths via `ICampRoleService.RemoveAllForMemberAsync`. Both services live within the Camps section.
- **Audit Log:** `IAuditLogService` — definition CRUD, role assign/unassign, and `CampMemberAddedByLead` actions.
- **Notifications:** `INotificationService` — `CampRoleAssigned` notification on assign (best-effort, try/catch in controller).
- **Google Integration:** `CampRoleService` implements `IGoogleGroupMembershipSource` (issue nobodies-collective/Humans#740) — the single contract Camps exposes to the Google sync orchestrator. For every active `CampRoleDefinition` with a non-empty `Slug` × every in-scope season year (`CampSettings.PublicYear` ∪ `OpenSeasons`), `GetExpectedAsync` claims a Google Group keyed `barrios-{year}-{slug}@{GoogleWorkspaceOptions.Domain}` whose expected members are the assignees from `camp_role_assignments` (filtered to `CampMember.Status = Active`). Definitions with empty `Slug` do not get a group and are not claimed — admins set the slug via the role-edit form when they want a group. Reconciliation is **pull-only**: the orchestrator (`GoogleGroupSyncService.ReconcileAllAsync`) enumerates membership sources on its own schedule; Camps does not push, does not call `IGoogleGroupSync.RequestSyncAsync`, and does not depend on `IGoogleGroupProvisioningClient`. Provisioning of missing groups happens inside `GoogleGroupSyncService.ReconcileClaimAsync` when a claim references a group that returns HTTP 404 from Cloud Identity — best-effort, per claim. No new email column is stored — the key is recomputed on demand from `(slug, year, domain)`.
- **Profiles:** Called by `IAccountMergeService` (Profiles section) — `ICampService.ReassignAssignmentsToUserAsync` re-FKs `CampLead` and `CampRoleAssignment` user references during account merge fold. `CampMember` is **not** folded (known gap).

## Architecture

**Owning services:** `CampService`, `CampContactService`, `CampRoleService`
**Owned tables:**
- `CampService` — `camps`, `camp_seasons`, `camp_leads`, `camp_members`, `camp_images`, `camp_historical_names`, `camp_settings`
- `CampRoleService` — `camp_role_definitions`, `camp_role_assignments`

**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#542, 2026-04-22). `CampRoleService` introduced in (A) shape from day one per issue nobodies-collective#489. T-06 (2026-05-16) added the canonical §15 caching decorator. Camp Lead retirement (issue nobodies-collective/Humans#753) moved lead authz from the `CampLead` entity onto a system `CampRoleDefinition` + `CampRoleAssignment`; legacy entity / table drop follows in a subsequent PR.

- `CampService` lives in `Humans.Application.Services.Camps.CampService` and goes through `ICampRepository` (`Humans.Application.Interfaces.Repositories`) for all data access. It never imports `Microsoft.EntityFrameworkCore` — enforced at compile time by `Humans.Application.csproj`'s reference graph. T-06: the inner service is **cache-unaware**; every read goes to the repo on every call.
- `CampRepository` lives in `Humans.Infrastructure.Repositories.Camps`, uses `IDbContextFactory<HumansDbContext>`, and is registered as Singleton.
- **Caching decorator (T-06).** `CachingCampService` (Singleton, `Humans.Infrastructure.Services.Camps`) wraps `ICampService` per design-rules §15d. It owns a `ConcurrentDictionary<Guid, CampInfo>` keyed by camp id (the canonical per-camp projection) plus a single-slot `CampSettingsInfo`. Year-keyed sub-views (`GetCampsForYearAsync`, `GetCampsWithLeadsForYearAsync`, public/placement summaries) are filtered **snapshots** of the canonical cache, not separate entries. `IsUserCampLeadAsync` (4 auth-handler call sites) answers from the snapshot. The legacy `IMemoryCache`-backed `camps_year_{N}` / `CampSettings` 5-min keys are retired.
- **Invalidation** — decorator-only. Every mutating method on `CachingCampService` delegates to the inner service and then calls `InvalidateCampAsync(campId)` (or `InvalidateSettingsAsync()`) inline. Cross-table effects ride the same path because the mutating method already knows the affected camp id — `SetEarlyEntryAsync`, `RemoveCampMemberAsync`, `LeaveCampMembershipAsync`, and the membership confirm/withdraw methods all invalidate after writing to `camp_members`, which is what keeps `CampSeasonInfo.EeGrantedCount` honest. The no-bypass rule (only the inner `CampService` / `CampRoleService` may touch `ICampRepository`) is pinned by `CampsArchitectureTests.ICampRepository_HasNoUnexpectedConsumers`, retiring the earlier `CampInfoSaveChangesInterceptor` backstop.
- **Warmup.** `CampInfoWarmupHostedService` populates the dict at startup; failures log + swallow (non-fatal — lazy population still works on first read).
- **Cache size budget.** ~5 MB at the 100-camp / 500-user steady state — comfortably under the §15 50-MB ceiling. Per-entry footprint dominated by season blurbs (~2 KB/season); warmup loads only the seasons referenced by `CampSettings.PublicYear` + `OpenSeasons` + the current real-world year — typically 1–3 seasons per camp, not full history.
- **Leads invariant (T-06).** `CampInfo.Leads` is non-null on every code path. The legacy "null means leads were not loaded" branch retired. `GetCampsWithLeadsForYearAsync` is `[Obsolete]`-marked — new callers use `GetCampsForYearAsync` and filter by `Status` in-memory.
- Filesystem I/O for camp images is abstracted behind the shared `IFileStorage` abstraction (Application interface + `FileSystemFileStorage` implementation in `Humans.Infrastructure`, rooted at `wwwroot/`); the service never touches `System.IO`.
- Filesystem I/O for camp images is abstracted behind the shared `IFileStorage` abstraction (Application interface + `FileSystemFileStorage` implementation in `Humans.Infrastructure`, rooted at `wwwroot/`); the service never touches `System.IO`.
- **Cross-domain navs stripped:** `CampLead.User` (issue nobodies-collective/Humans#542) — consumers route through `IUserService.GetByIdsAsync(...)`.
- `CampContactService` has no owned DB tables and does not inject `HumansDbContext`; it retains its `IMemoryCache` rate-limit usage since that's a request-acceleration cache, not canonical domain data.
- `CampRoleService` lives in `Humans.Application.Services.Camps.CampRoleService` and goes through `ICampRoleRepository` (`Humans.Application.Interfaces.Repositories`) for all data access. It owns `camp_role_definitions` and `camp_role_assignments` and never imports `Microsoft.EntityFrameworkCore`. Display-name stitching for `AssignedByUserId` routes through `IUserService.GetByIdsAsync`. Plain pass-through (no caching decorator); add `IMemoryCache` later if list-of-definitions reads dominate.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/CampsArchitectureTests.cs`.
- **Read/write interface split.** `ICampServiceRead` (5 methods: GetCampsForYearAsync, GetCampBySlugAsync, GetCampSeasonByIdAsync, GetSettingsAsync, SearchAsync) is the cross-section read surface — returns only CampInfo-family projections (CampInfo with computed `Active`, CampSeasonInfo), CampSettingsInfo, CampSearchHit; no EF entities. `ICampService : ICampServiceRead` adds writes, cache invalidation, per-user/lead/membership reads, and Camps-internal reads. External sections inject `ICampServiceRead`. CampLookup/CampSeasonLookup were folded into CampInfo/CampSeasonInfo. See `memory/architecture/section-read-write-split.md`.

### Touch-and-clean guidance

- `Camp.CreatedByUser` and `CampSeason.ReviewedByUser` are declared but never read. Safe targets for the cross-cutting User nav strip when the wider effort lands.
- The legacy `CampLead` entity, `CampLeadRole` enum, `CampLeadConfiguration`, `ICampService.AddLeadAsync` / `RemoveLeadAsync`, `ICampRepository` lead methods, the `CampService.ReassignAsync` lead branch, and the dual-source fallback branches in `IsUserCampLeadAsync` / `GetCampLeadSeasonIdForYearAsync` / `GetPendingMembershipCountForLeadAsync` are pending removal in the follow-up PR to issue nobodies-collective/Humans#753 — drop them once every environment has run the "Seed system roles" admin button on `/Camps/Admin` and the role-side data is verified.
- `CampMemberConfiguration.cs` is now located in
  `src/Humans.Infrastructure/Data/Configurations/Camps/` with other Camps entity configuration.
