<!-- freshness:triggers
  src/Sections/Humans.Camps/**
  src/Sections/Humans.Camps.Contracts/**
-->
<!-- freshness:flag-on-change
  Camp/Season lifecycle, lead/membership authorization, public-year settings, and notification triggers — review when Camp services/entities/controllers/auth handlers change.
-->

# Camps — Section Invariants

Themed community camps (Barrios) with per-year season registrations, leads, images, and renaming history.

## Concepts

- A **Camp** (also called "Barrio") is a themed community camp. Each camp has a unique URL slug, one or more leads, and optional images.
- Newly registered camps keep their submitted display name and receive a non-empty URL slug. Names with no ASCII letters/digits use `camp` as the base; collisions append numeric suffixes, shortening the base as needed to fit the 256-character slug column without a trailing separator. Existing camp slugs are unchanged.
- A **Camp Season** is a per-year registration for a camp, containing the year-specific name, description, community info, and placement details.
- A **Camp Lead** is a human responsible for managing a camp. Lead authorization flows **solely** through a `CampRoleAssignment` against the `CampRoleDefinition` whose `SpecialRole = CampSpecialRole.Lead` (exposed on the read model as `CampSeasonInfo.LeadUserIds` and checked via `CampInfo.IsLead`). There is no `CampLead` entity — the legacy entity and its `camp_leads` table were dropped in issue nobodies-collective/Humans#774.
- A **Workshop Lead** is a human authorized to submit camp events on behalf of their camp via `BarrioEventsController` (`/Barrios/{slug}/Events/*`), without inheriting general camp-management authority. Authority flows through a `CampRoleAssignment` against the `CampRoleDefinition` whose `SpecialRole = CampSpecialRole.Workshop`. Camp Leads automatically have Workshop authority because the event-management check is the OR of {Lead, Workshop} — no separate inheritance link.
- A **Camp Member** is a human's post-hoc, per-season affiliation with a camp. The app does **not** admit humans to a camp — each camp runs its own process. A CampMember row exists so the app knows who belongs to which camp for per-camp roles (e.g. LNT lead), Early Entry allocations, and notifications. Status: Pending → Active → Removed. `Removed` is a soft-delete tombstone so re-requesting creates a new row.
- The member-facing My Camps card and directory heading render their titles and active/pending membership badges through `CampsResource` in every supported culture. Join-request outcomes also carry internal resource keys resolved by the controller: request created, already active, already pending, or no open season.
- Registration and season-renewal guards return resource keys for reserved names, closed seasons, existing seasons and missing prior seasons. The controller formats the name/year in the member’s culture; failed guards create no camp or season and write no audit entry.
- Registration and editing validate required name, contact email, descriptions and languages on the server, plus email syntax and stored bounds for name, contact details, descriptions, languages and optional kids/performance text. Invalid forms redisplay with shared validation messages in every supported culture before any creation or update. The existing browser short-description limit remains stricter than its stored bound.
- Registration and edit link-removal buttons have accessible names from the shared Remove label in every supported culture, including dynamically added links.
- Contact-phone validation on registration and camp editing uses the localized field label and shared international-prefix message in every supported culture. The registration and contact pages also use the existing translated breadcrumb navigation label.
- The public/member camp roles card renders its role/lead heading and vacancy count through `CampsResource` in every supported culture.
- Public current- and past-season detail cards render community/culture labels and enum values through the existing Camps and shared resources in every supported culture.
- The detail page’s camp-lead action labels and season withdrawal/full confirmations use localized resources in every supported culture. CampAdmin-only controls remain operator-exempt.
- The camp-lead Members & Roles page localizes headings, role/member action tooltips and confirmations, search prompts, Early Entry controls, and the reminder that app approval records affiliation rather than admitting someone to the camp.
- Public season links, carousel controls, participation counts, and the signed-in membership card use localized resources, including pending/active messages and withdrawal/leave confirmations. The lead-facing placement card localizes its headings, container-management link, and sound-zone, space-size, and electrical-grid values through the same resources as the edit form.
- A **Camp Role Definition** is a CampAdmin-managed catalogue row describing a per-camp role with a slot count, compliance threshold (`MinimumRequired`), and sort order. `MinimumRequired = 0` means the role is optional and not tracked in the compliance report; `MinimumRequired ≥ 1` means the compliance report tracks it with that threshold. The catalogue ships empty — CampAdmin creates every definition. Soft-deleted via `DeactivatedAt` so historical assignments survive removal from the active catalogue.
- A **Camp Role Assignment** is a per-season binding of a `CampMember` to a `CampRoleDefinition`. "Camp Lead" and "Workshop Lead" **are** `CampRoleDefinition` rows (special, `SpecialRole != None`); lead authority is resolved entirely from `CampRoleAssignment`.
- **Camp Settings** is a singleton controlling which seasons accept new registrations. The public year is no longer stored here — it resolves from Settings' active event (falling back to the clock year when no event is active).

## Data Model

### Camp

Core entity: contact info, slug, flags.

**Table:** `camps`

### CampSeason

Per-year season data (name, blurbs, community info, placement). `EeSlotCount` (int, default 0) tracks the Early Entry slot cap for the season; managed by CampAdmin.

**Table:** `camp_seasons`

### CampImage

Image metadata; files are stored on disk via the shared `IFileStorage` abstraction (key `uploads/camps/{campId}/{guid}{.ext}`, served as static files at `/uploads/camps/...`). Display order is tracked per camp.

Uploaded display names are stored as a basename only and must fit the 256-character `CampImage.FileName` column; invalid names fail before a file or row is written. If an image metadata save fails, the new file is removed only after a fresh repository lookup confirms no image row exists. Committed files are preserved; verification/cleanup failures are logged as errors and the original save failure propagates. Member-facing image-upload validation outcomes use Camps resource keys for the count, MIME type, byte size, basename length and extension guards, translated in all six cultures at the controller boundary. The upload limits are unchanged.

**Table:** `camp_images`

### CampHistoricalName

Name history for tracking renames.

**Table:** `camp_historical_names`

### CampSettings

Singleton settings: open seasons and per-season name-lock dates (`NameLockDate`, `NameLockedAt`
live on `CampSeason`, not here). `PublicYear` and `EeStartDate` columns are **dead**
(nobodies-collective#1632, #1633): no longer read or written, kept mapped only so the EF model
matches the database until a Peter-approved follow-up drops them. The public year now resolves
from `ISettingsService.GetActiveEventSettingsAsync().Year`, falling back to the clock's current
year when no event is active; the EE start date resolves from
`EventSettings.EarlyEntryStartOffset` (`GateOpeningDate.PlusDays(offset)`), both surfaced via
`CampSettingsInfo` (a DTO, not the entity).

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
| HasEarlyEntry | bool | Default false; cleared on Removed transition unless the holder had already entered the event (consumed grant — retained so the slot-cap count stays accurate) |

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
| SpecialRole | CampSpecialRole | Default `None`. Marker for special, system-managed role definitions (`Lead`, `Workshop`) seeded by the CampAdmin "Seed system roles" action (issue nobodies-collective/Humans#753). `CampRoleService` rejects rename / slug change / sort-order change / min-required change / deactivation when `SpecialRole != None`; only `SlotCount` and `Description` are admin-mutable. Stored as string via `HasConversion<string>()`, no DB default — the `'None'` default that backfilled the AddColumn migration was dropped in issue nobodies-collective/Humans#787 because it shadowed the CLR default and produced a startup sentinel warning. |
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

Four controllers serve this section. The MVC URL surface is dual-routed under `/Camps/*` (English) and `/Barrios/*` (Spanish); the API surface is dual-routed under `/api/camps/*` and `/api/barrios/*`. The dual-route alias is governed by an invariant below — no other section may add aliases.

| Route | Controller | Purpose |
|-------|------------|---------|
| `/Camps` | `CampController` | Public directory |
| `/Camps/{slug}` | `CampController` | Camp detail (current season, leads, images, history; signed-in users also see the Events-owned hosted-events card) |
| `/Camps/{slug}/Season/{year}` | `CampController` | Past-season detail |
| `/Camps/{slug}/Contact` | `CampController` | Facilitated message to camp leads |
| `/Camps/{slug}/Edit` | `CampController` | Lead-only edit of season copy / images / leads (links through to Members for role/membership management) |
| `/Camps/{slug}/Edit/Members` | `CampController.Members` | Lead-only members + roles management (pending requests, active members, role assignments) |
| `/Camps/Register` | `CampController` | New camp registration |
| `/Camps/{slug}/OptIn/{year}`, `.../Withdraw/{seasonId}`, `.../MarkFull/{seasonId}` | `CampController` | Per-season participation toggles (reactivation is CampAdmin-only, under `/Camps/Admin`) |
| `/Camps/{slug}/Members/*` | `CampController` | Member request/approve/reject/remove/leave |
| `/Camps/{slug}/Roles/*` | `CampController` | Per-camp role assignment/unassignment |
| `/Camps/{slug}/Images/*` | `CampController` | Image upload/delete/reorder |
| `/Camps/{slug}/HistoricalNames/*` | `CampController` | Historical-name add/remove |
| `/Camps/Admin` | `CampAdminController` | CampAdmin-only directory + name-lock dates; open-season management moved to `/Settings#barrios` (peterdrier/Humans#1634) |
| `/Camps/Admin/Roles/*` | `CampAdminController` | `CampRoleDefinition` CRUD |
| `/Camps/Admin/Roles/{slug}` | `CampAdminController.RolesDrillDown` | Cross-camp roster for one role definition (issue nobodies-collective/Humans#740): per-camp-season assignees with name + Google email and a `mailto:` to the derived group email; year-picker drop-down. CampAdmin only. |
| `/Camps/Admin/Compliance` | `CampComplianceController` | Read-only role-staffing matrix: rows = active barrios (Active/Full) for the year, columns = active role definitions, cells = assignee avatars + a dashed placeholder per unfilled required slot. Gated by `CampComplianceAccess` (CampAdmin/Admin **or** any team/sub-team coordinator), broader than the CampAdmin-only management surface. |
| `/Camps/Admin/Export` | `CampAdminController` | CSV export |
| `/Camps/Admin/{Approve,Reject,OpenSeason,CloseSeason,SetNameLockDate,Reactivate,Delete}/...` | `CampAdminController` | Season lifecycle actions (`OpenSeason`/`CloseSeason` are posted to from `/Settings#barrios` and redirect back to that tab; the rest stay `/Camps/Admin`-only and redirect to `/Camps/Admin`) |
| `/api/camps/{year}` | `CampApiController` | Year directory JSON |
| `/api/camps/{year}/placement` | `CampApiController` | Placement-data JSON |
| `/Camps/{slug}/Members/{campMemberId}/EarlyEntry` | `CampController` | Grant / revoke EE on a camp member |
| `/Camps/Admin/SetCampSeasonEeSlotCount/{seasonId}` | `CampAdminController` | Set a season's EE slot cap |
| `/Camps/Admin/SeedSystemRoles` | `CampAdminController` | POST; idempotent seeding of missing `CampSpecialRole` definitions (button renders only while one is missing) |

Admin pages live under `/Camps/Admin/*` — never `/Admin/Camps/*` (per `docs/architecture/design-rules.md` § "Admin is not a section": `/Admin/*` is a nav holder for actions whose services live in their owning sections).

The shared camp-management and camp-event authorization preflights propagate browser cancellation through camp and current-user reads before authorizing or beginning a write. Camp edit and members GETs also forward cancellation to their edit-data reads; subsequent members and roles reads retain the same token.

The admin dashboard and CSV export propagate request cancellation through their settings, camp, role, and lead-user reads. A cancelled request is rethrown without an error toast or failure log.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | Browse the camps directory, view camp details and season details — for publicly visible (`Active`/`Full`) seasons only; other statuses 404 unless the viewer is a lead of that camp or CampAdmin (nobodies-collective/Humans#993) |
| Any authenticated human | Register a new camp (which creates a new season in Pending status). Request to join a camp for its open season; withdraw their own pending request; leave their own active membership. |
| Camp lead | Edit their camp's details, manage season registrations, manage co-leads, upload/manage images, manage historical names. Approve / reject pending membership requests for their camp. Remove active members. Add an active member directly to their camp (lead-driven shortcut). Assign / unassign per-camp role assignments for their camp. Mark their camp's Active season Full — an informational label only, shown to visitors, that does not block join requests. |
| CampAdmin, Admin | All camp lead capabilities on all camps. Approve/reject season registrations. Reactivate a Full or Withdrawn season. Manage camp settings (open seasons, name lock dates). Update registration info copy. View withdrawn seasons on the admin dashboard. Export camp data as CSV. Manage the role-definition catalogue (create, edit, deactivate, reactivate). View the role-staffing compliance matrix. |
| Team/sub-team coordinator | View the read-only role-staffing compliance matrix at `/Camps/Admin/Compliance` (via `CampComplianceAccess`) — no camp-management authority. |
| Admin | Delete camps |

## Invariants

- Global-search result rows pass browser cancellation through both the camp and public-year settings reads; season-name selection and missing-result behavior are unchanged.

- Admin pending/withdrawn Barrio description previews preserve whole UTF-16 surrogate pairs within their existing 100-unit limit; full descriptions and approval/reactivation behavior stay unchanged.

- The registration GET carries request cancellation through season settings and registration instructions; abandoning the page stops those loads. POST redisplay helpers keep their existing cancellation boundary.

- Each camp has a unique slug used for URL routing.
- Camp season status follows: Pending then Active, Full, Rejected, or Withdrawn. Only CampAdmin can approve or reject a season. A camp lead or CampAdmin can set an Active season's status to Full (`CampService.SetSeasonStatusAsync` → `CampSeason.SetStatus`, a plain field flip with no transition validation); only CampAdmin can reactivate a Full (or Withdrawn) season back to Active/Pending.
- **`Full` is informational only — it does not gate join requests.** It tells visitors the camp currently looks full; `RequestCampMembershipAsync` still matches `Active` **or** `Full` for the public year, because Humans doesn't yet know everyone who is actually in the camp (Peter, 2026-08-20). Don't reintroduce a block here — that reading of the issue was explicitly overridden.
- Only camp leads or CampAdmin can edit a camp.
- Registration, opt-in, scoped season/name/image changes and managed membership mutations return internal refusal results. Controllers localize their keys and log expected refusals as warnings without exceptions. Unexpected dependency exceptions propagate; camp updates never wrap diagnostic messages in member feedback. Guard refusals precede the writes and audit entries they protect; failure invalidation still refreshes partially committed updates.
- **Lead-facing mutations are camp-scoped.** Ids arriving from a form (seasonId, imageId, nameId) are proven to belong to the slug-resolved camp in `CampService` (`UpdateSeasonAsync`, `WithdrawSeasonAsync`, `ChangeSeasonNameAsync`, `DeleteImageAsync`, `RemoveHistoricalNameAsync`, `SetSeasonStatusAsync` all take a `scopedCampId` and return a refusal on mismatch) — a lead of camp A cannot mutate camp B by crafting an id.
- New camp images append after the highest remaining display position (zero for an empty gallery). Deletion leaves surviving positions intact, and later uploads preserve their relative order. Explicit reordering still assigns the requested positions.
- Camp images are stored on disk via the shared `IFileStorage` abstraction (key prefix `uploads/camps/{campId}/`); metadata and display order are tracked per camp. The upload route permits 11 MB for one 10 MB image plus multipart overhead; the service enforces the image cap.
- Camp and image deletions commit their metadata and audit before cleaning up image files. Cleanup ignores caller cancellation and logs storage failures without failing the committed deletion.
- **Name-lock + historical-name auto-log:** renaming a season (`ChangeSeasonNameAsync`) is rejected once the season's `NameLockDate` has passed (today ≥ `NameLockDate`). Before the lock date, a rename auto-records the *old* name as a `CampHistoricalName` with `Source = NameChange` and writes a `CampNameChanged` audit entry.
- Camp settings control which year is shown publicly and which seasons accept registrations.
- Resource-based authorization per design-rules §11: `CampAuthorizationHandler` + `CampOperationRequirement` gate all admin writes.
- Membership is **per-season**. One live (`Pending`/`Active`) row per `(CampSeasonId, UserId)` enforced by a partial unique index. `Removed` rows are kept for audit and do not block re-requests.
- Membership mutations (approve, reject, remove) are **scoped to the authorizing camp**. A lead or CampAdmin operating on camp A cannot mutate a member row whose season belongs to camp B even if they know the row id.
- Membership state is **never rendered on anonymous or public views**. It is only shown to the human themselves and to leads/CampAdmin of the camp.
- **A season outside `Active`/`Full` is non-public everywhere.** The directory, name search, and the JSON API already filter to public statuses (one deliberate exception: pasting a camp's GUID into search resolves that camp regardless of status — routing convenience for a caller who already holds the id, not authorization); `/Camps/{slug}` and `/Camps/{slug}/Season/{year}` enforce the same rule at the destination — a viewer without Manage on the camp gets a 404 (not a 403, so the slug leaks nothing). Leads and CampAdmin keep access to their own non-public camp (nobodies-collective/Humans#993).
- A `CampRoleAssignment` requires the linked `CampMember` to have `Status = Active` for the same `CampSeasonId`. Service rejects with `MemberNotActive` or `MemberSeasonMismatch` otherwise.
- A human cannot hold the same role twice in the same season — enforced by unique index on `(CampSeasonId, CampRoleDefinitionId, CampMemberId)`. Only `IX_camp_role_assignments_unique` collisions become the already-assigned outcome; unrelated persistence failures propagate.
- Filled role slots carry assignment, member and user IDs only; the views resolve names through the Human component. The role service does not fetch user names to build the panel.
- Camp-role slots have no per-slot identity — there is deliberately no `SlotIndex` column (rejected alternative from the original PR peterdrier/Humans#335 blueprint). Storage is the `(CampSeasonId, CampRoleDefinitionId, CampMemberId)` binding; slots are a display concern (the roles panel orders assignments and pads with empty rows up to `SlotCount`). When CampAdmin lowers `SlotCount` below current assignments, the panel renders an over-capacity indicator (`CampRolesPanelData.OverCapacity`) instead of blocking or evicting — a lead must unassign to return to capacity.
- All role-assignment *identity* data is private (no anonymous render). The public Camp Details page does not expose role assignments. The directory's opt-in "Show lead positions" toggle (`/Camps`, issue nobodies-collective/Humans#821) renders per-camp role-coverage **counts only** (filled/slots per active definition via `GetDirectoryRoleSummariesAsync`) — never assignee identities — so no private data leaks to anonymous viewers.
- Self-service leave and request withdrawal return localized missing-membership and invalid-status errors in all six cultures. Missing and another user’s memberships produce the same error; failed guards leave membership status unchanged.
- Leave/Withdraw/Remove cascades clear role assignments via `ICampRoleService.RemoveAllForMemberAsync` before the soft-delete. Hard-delete of a `CampMember` row cascades through the FK directly.
- `RejectCampMemberAsync` does **not** cascade role assignments (`cascadeRoleAssignments: false`). Reject targets `Pending` members only; a `Pending` member has never been `Active` and therefore holds no `CampRoleAssignment` rows. Cascading on Reject would be a no-op and is intentionally omitted.
- Camp Lead authz flows **solely** through `CampRoleAssignment` against the Camp Lead role definition (`SpecialRole = Lead`), surfaced on the read model as `CampSeasonInfo.LeadUserIds` and checked via `CampInfo.IsLead(userId)` (the `CampAuthorizationHandler` resolves the public-year `CampInfo` and calls `IsLead`). There is no other lead source.
- Camp-event submission authz (`BarrioEventsController` at `/Barrios/{slug}/Events/*`) flows through `CampInfo.IsEventManager(userId)` — true when the user holds a `CampRoleAssignment` whose `CampRoleDefinition.SpecialRole` is `Lead` OR `Workshop` (i.e. the user is in `LeadUserIds` ∪ `WorkshopLeadUserIds`; CampAdmin / Admin retain blanket authority). Camp Leads automatically satisfy the check; Workshop Leads do not gain general camp-management authority. Moderation of submitted events remains global GuideModerator / Admin.
- The `/Camps ↔ /Barrios` and `/api/camps ↔ /api/barrios` dual-route aliases are the **only sanctioned URL aliases in the codebase**. No other section may add URL aliases without explicit owner approval.
- Early Entry slot count is per-season (`CampSeason.EeSlotCount`, CampAdmin-managed). The EE start date is global per event, resolved from Settings' `EventSettings.EarlyEntryStartOffset` (`GateOpeningDate.PlusDays(offset)`) — edited on `/Settings`, not stored in Camps.
- A `CampMember.HasEarlyEntry` grant requires `Status = Active`. Granting beyond `EeSlotCount` is rejected; lowering `EeSlotCount` below current grants is allowed (no auto-revoke; overflow flagged in UI).
- Member-removal transitions (Remove / Leave / Withdraw / Reject) clear `HasEarlyEntry` in the same `SaveChangesAsync` as the status flip — **except** when the removed member has already entered the event (gate check-in detected via `IUserServiceRead`). In that case `HasEarlyEntry` is retained on the `Removed` row so the slot-cap count (`GetGrantedCountForSeasonAsync`, which no longer filters by `Status = Active`) still includes the consumed slot, preventing remove-and-regrant from yielding extra early entries.
- EE state is **never** rendered on anonymous or public views — only on `/Camps/Admin` and `/Camps/{slug}/Edit/Members` for CampAdmin/leads.
- Granting/revoking EE reuses the general camp-management gate (`ResolveCampManagementAsync` — camp lead of that camp, or CampAdmin/Admin). There is **no** dedicated `CampOperation.SetEarlyEntry` resource operation; don't go looking for one.
- The camp detail page carries two read-only cards with **different sources**, both built in `CampController.PopulateDetailCardsAsync`. **Roles** projects `CampRoleAssignment` via `ICampRoleService.BuildPanelAsync` (`canManage: false`) — the same data as `/Edit/Members`, so the two pages can never disagree about role assignments. **Roster** is a separate `CampMember` projection off `season.ActiveMembers`. They can legitimately disagree — an Active member holding no role appears on the Roster and not in Roles — so don't treat either as a view of the other.
- Card visibility keys off `CanSeeFullCamp` (an Active member of the **displayed** season — not the open-season membership VM, which would wrongly hide the roster on Pending/closed seasons — or CampAdmin/Admin). Anonymous viewers get neither card (`PopulateDetailCardsAsync` returns early with no signed-in user). A signed-in non-member gets the Roles card **retitled "Leads"** and filtered to the Lead role's filled assignees only (`_CampRolesCard.cshtml`), and no Roster. Full-camp viewers get every active role plus open slots, and the localized Roster card.

## Negative Access Rules

- Regular humans **cannot** edit camps they do not lead.
- Camp leads **cannot** approve or reject season registrations — that requires CampAdmin or Admin.
- CampAdmin **cannot** delete camps. Only Admin can delete a camp.
- Anonymous visitors **cannot** register camps or edit any camp data.
- Anonymous visitors **cannot** see role assignments — the public Camp Details page does not render the roles section.
- Camp leads **cannot** manage the role-definition catalogue (create, edit, deactivate). Only CampAdmin or Admin can.
- A camp lead **cannot** assign or unassign roles on a camp other than their own (controller verifies `assignment.CampSeasonId` is in the set of season IDs on the resolved `CampInfo` before delegating to the service).
- Anyone **cannot** assign a role to a human who is not an Active CampMember of the same season — service rejects with `MemberNotActive` / `MemberSeasonMismatch`.
- Camp leads **cannot** edit `EeSlotCount` (CampAdmin/Admin only).
- Anyone **cannot** grant EE to a non-Active member (service rejects with `MemberNotActive`).
- Anyone **cannot** revoke EE from a member who has already entered the event (service rejects with `MemberAlreadyEntered`). Once the grant is consumed at the gate the slot remains allocated to prevent extra early entries from remove-and-regrant.

## Triggers

- When a camp is registered, its initial season is created with Pending status.
- Registering a camp persists the camp, its initial season, the creator as an **Active `CampMember`**, and the creator's Camp Lead `CampRoleAssignment` in one `ICampRepository.CreateCampAsync` call — the creator is a lead through the role system from the first save, with nothing to backfill. When the Camp Lead `CampRoleDefinition` has not been seeded, registration still succeeds with the creator as an Active member only and logs a warning pointing at the `/Camps/Admin` "Seed system roles" action; an unseeded catalogue never blocks camp registration.
- When an existing camp opts into a newly-opened season (`OptInToSeasonAsync`, `/Camps/{slug}/OptIn/{year}`), the new season copies the previous season's details and is **auto-approved to `Active`** when the camp has any prior `Active`/`Full`/`Withdrawn` season (`HasApprovedSeasonAsync`). A camp with only `Pending`/`Rejected` history instead gets `Pending` and requires CampAdmin review.
- Historical-name removal and image deletion guard failures use translated Camps keys for missing records and cross-camp ownership mismatches. Expected failures log Warning with the reason and existing identifiers, without an exception. Failed scope checks preserve the target record; image audit and file-cleanup ordering are unchanged.
- Expected membership withdraw/approve/reject/remove guards log Warning with the reason and existing member/actor/camp identifiers, without an exception object. Missing-member and pending/active-status errors on lead-facing approve/reject/remove routes use Camps resource keys, translated in all six supported cultures, as do self-service withdrawal errors. Redirects and unexpected-failure propagation remain unchanged.
- Season approval or rejection is performed by CampAdmin. Admin approve/reject/reactivate/delete guards log expected missing-record or lifecycle rejections at Warning with the reason and existing identifiers, without an exception object; error feedback and redirects are preserved. Unexpected failures outside those guards propagate.
- Approving a membership request sends a `CampMembershipApproved` notification to the requester.
- Rejecting a membership request sends a `CampMembershipRejected` notification to the requester. Approval and rejection notices use the recipient’s supported saved language in all six cultures, with English fallback for missing/unsupported languages or a failed language lookup.
- When a season is rejected or withdrawn, pending requesters receive a `CampMembershipSeasonClosed` notification. Notices are grouped by the recipient’s supported saved language, with English fallback; a delivery failure in one language group does not prevent attempts for other groups. Their membership rows are **not** auto-mutated — the notification is the only side effect, so if the season is later reactivated the request is still live.
- Facilitated contact messages notify camp leads in each recipient’s supported saved language, across all six cultures. Missing/unsupported language or a failed lookup falls back to English; delivery failures in one language group do not prevent the others. Long titles fit the notification storage limit while preserving the full copy in the body.
- Camp leads do **not** receive a per-request stored notification when humans request to join. Instead a `NotificationMeter` ("N humans want to join your camp") shows the live pending count; it updates immediately on approve/reject/withdraw and drops to zero when the season is closed.
- Active leads appear in the camp's active-members list automatically, tagged with an `IsLead` flag. They do not need a `CampMember` row to be shown as part of the camp.
- When a CampMember is removed (Leave / Withdraw / Remove paths set `RemovedAt`), `ICampService` calls `ICampRoleService.RemoveAllForMemberAsync` before the soft-delete to clear any role assignments held by that member.
- When a lead uses the "add active member" shortcut at `/Camps/{slug}/Members/Add`, `ICampService.AddCampMemberToActiveSeasonAsync` creates `CampMember(Status=Active)` directly and writes a `CampMemberAddedByLead` audit entry.
- Assigning a per-camp role writes a `CampRoleAssigned` audit entry and sends a best-effort `CampRoleAssigned` notification to the assignee. The notice uses the assignee’s supported saved language across all six cultures, with English fallback for missing/unsupported language or a failed lookup. Unexpected lookup and delivery failures are logged as errors, while caller cancellation propagates. Unassign writes `CampRoleUnassigned` and does **not** notify.
- Role-definition create/edit refusals (duplicate names/slugs, reserved slug, protected system-role edits) log Warning with actor, role id where present and guard reason, without an exception, and retain form feedback. An unexpected edit result logs Error with its exception and uses the existing generic failure feedback.
- Definition CRUD (`CampRoleDefinitionCreated` / `Updated` / `Deactivated` / `Reactivated`) writes audit entries; ordering is `repo.Add` then `SaveChangesAsync` then `auditLog.LogAsync`.
- When an account merge accepts, `ICampService.ReassignAsync` folds the source's whole camp footprint onto the target: each source `CampMember` is re-pointed to the target (its `CampRoleAssignment` rows ride along on the unchanged `CampMemberId`); when the target already holds a live (`Pending`/`Active`) membership for the same season, the source member's roles are folded onto the target's member (target wins on `IX_camp_role_assignments_unique` collision, `HasEarlyEntry` is OR-ed in) and the now-empty source member is dropped. `Removed` source members always re-point. Because Camp Lead is now a `CampRoleAssignment`, leads move too. The per-user early-entry cache is evicted for both source and target after the fold. Called only by `IAccountMergeService.AcceptAsync` (Profiles section).
- GDPR export includes role assignments plus every membership across all years and statuses, including request/confirmation/removal dates and actors, camp/season affiliation and Early Entry state. Both collection slices remain present as empty lists when absent; erasure deletes both memberships and their role assignments.
- Membership merge and GDPR erasure evict pending-request badges for the current lead directory, captured before the write, plus the changed users. Deleting a pending request or folding duplicate requests cannot leave other leads displaying the pre-write count. Existing roster and early-entry invalidation also remain in place.
- Granting / revoking EE writes `CampEarlyEntryGranted` / `CampEarlyEntryRevoked` audit entries. Idempotent set writes no audit row.
- Changing `EeSlotCount` writes `CampSeasonEeSlotCountChanged`. The EE start date is edited on `/Settings` now (Settings' own audit trail), so `CampSettingsEeStartDateChanged` is no longer written; the enum member stays so existing history still reads (nobodies-collective/Humans#1633).

- Camp season names in notices may outgrow the 200-character title limit. These producers bound titles by Unicode character, preserving the full title and existing detail in the body; short notice copy is unchanged.

## Cross-Section Dependencies

- **Search (downstream consumer):** the global `/Search` page renders every camp hit through this section's own public `<vc:camps-search-result camp-id>`, which resolves the public-year season name and the slug it links to itself off `ICampServiceRead.GetCampByIdAsync`. Search passes the camp id and no display fields (nobodies-collective/Humans#1062); `CampSearchHit` is `(CampId, Name, Score)`, carrying the section's own `Score`. Camps does not depend on Search.
- **Users/Identity:** `IUserServiceRead.GetUserInfosAsync` — lead and assignee display names (stitched in memory; `CampRoleAssignment.AssignedByUserId` is scalar-only).
- **Admin:** Camp settings management is restricted to CampAdmin and Admin (resource-based auth handler).
- **City Planning:** CampSeason is the anchor for `camp_polygons`; City Planning reads camp data via `ICampService` but writes its own tables only.
- **Containers:** `Camp` is read by the Containers section via FK (`Container.CampId → camps.Id`) and via the cross-section read surface `ICampServiceRead` (`GetCampsForYearAsync`, `GetCampBySlugAsync`, etc.) for display; lead checks are answered in-memory off the returned `CampInfo` (`CampInfo.IsLead`). Containers are year-agnostic and have no `CampSeasonId`. Camps does not depend on Containers — this is a downstream dependency only.
- **Events:** the camp detail view renders `<vc:camp-parts camp-id>` (this section's own `CampPartsViewComponent`, `Humans.Camps.Contracts.ICampPart` seam, nobodies-collective/Humans#1815), which fans out over every active section's `ICampPart` contributor and invokes each declared component `Type` by `CampPartArgs(CampId)`. Events contributes its `EventsCardViewComponent` (reads `IEventServiceRead`) to list the camp's approved events with per-row favourite toggles — auth-gated at the call site, auto-hides when empty or when the Events feature is off. Web-layer view composition only; Camps services do not depend on Events, and this section references no contributor.
- **Camps internal — `CampRoleService` ↔ `CampService`:** `CampRoleService` calls the narrow camp-side port `ICampRoleCampAccess` (settings, member-status lookup, compliance season list, migration helper) for camp/season lookup and active-membership verification, signals cache invalidation through `ICampInfoInvalidator`, and is called back by `ICampService` from the Leave/Withdraw/Remove paths via `ICampRoleService.RemoveAllForMemberAsync`. It no longer injects `ICampService` directly. Both services live within the Camps section.
- **Audit Log:** `IAuditLogService` — definition CRUD, role assign/unassign, and `CampMemberAddedByLead` actions.
- **Notifications:** `INotificationService` — `CampRoleAssigned` notification on assign (best-effort, try/catch in controller).
- **Google Integration:** `CampRoleService` implements `IGoogleGroupMembershipSource` (issue nobodies-collective/Humans#740) — the single contract Camps exposes to the Google sync orchestrator. For every active `CampRoleDefinition` with a non-empty `Slug` × every in-scope season year (`CampSettingsInfo.PublicYear` ∪ `OpenSeasons`), `GetExpectedAsync` claims a Google Group keyed `barrios-{year}-{slug}@{GoogleWorkspaceOptions.Domain}` whose expected members are the assignees from `camp_role_assignments` (filtered to `CampMember.Status = Active`). **Barrio-lead fallback (issue nobodies-collective/Humans#859):** for **non-Lead** role definitions, any camp that has a Lead for that `(camp, year)` but no direct assignee for this role contributes its Lead(s) as stand-in members of the role's group — so an unfilled role's group still reaches the camp's barrio lead(s) rather than nobody. The Lead role's own group is never augmented this way. Definitions with empty `Slug` do not get a group and are not claimed — admins set the slug via the role-edit form when they want a group. Reconciliation is **pull-only**: the orchestrator (`GoogleGroupSyncService.ReconcileAllAsync`) enumerates membership sources on its own schedule; Camps does not push, does not call `IGoogleGroupSync.RequestSyncAsync`, and does not depend on `IGoogleGroupProvisioningClient`. Provisioning of missing groups happens inside `GoogleGroupSyncService.ReconcileClaimAsync` when a claim references a group that returns HTTP 404 from Cloud Identity — best-effort, per claim. No new email column is stored — the key is recomputed on demand from `(slug, year, domain)`.
- **Profiles:** Called by `IAccountMergeService` (Profiles section) — `ICampService.ReassignAsync` folds the source's whole camp footprint onto the target during account merge. `CampRepository.ReassignMembershipsToUserAsync` re-points each source `CampMember` to the target (its `CampRoleAssignment` rows ride along on the unchanged `CampMemberId`); when the target already holds a live (non-`Removed`) membership for the season, re-pointing would break `IX_camp_members_active_unique`, so the source member's roles are folded onto the target's member (target wins on `IX_camp_role_assignments_unique` collision) and the now-empty source member is dropped. `Removed` source members always re-point, carrying history forward. Because Camp Lead is a `CampRoleAssignment`, leads move too.
- **Early Entry contributor:** `CampService` implements `IEarlyEntryProvider`, with the registered `CachingCampService` delegating — emits one grant per Active `HasEarlyEntry` member (entry date = `EventSettings.EarlyEntryStartOffset` resolved against `GateOpeningDate`, read via `ISettingsService`; no grants emitted while the offset is unset, source = "Camp: {name}"). `SetEarlyEntryAsync` and the member-removal cascade evict the per-user EE cache via `IEarlyEntryInvalidator`.

## Architecture

`MyCampsViewComponent` owns the private profile-page membership list. Its settings and
per-year camp reads receive `HttpContext.RequestAborted`; operational failures still hide
the advisory component, while a disconnected request propagates cancellation. CampAdmin's
role-definition mutations and camp-management member mutations likewise propagate a
disconnected request rather than report it as a failed admin action.

**Owning services:** `CampService`, `CampContactService`, `CampRoleService`
**Owned tables:**
- `CampService` — `camps`, `camp_seasons`, `camp_members`, `camp_images`, `camp_historical_names`, `camp_settings`
- `CampRoleService` — `camp_role_definitions`, `camp_role_assignments`

**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#542, 2026-04-22).

- `CampService` lives in `Humans.Camps.Services.CampService` and goes through `ICampRepository` (`Humans.Camps.Data`) for all data access. T-06: the inner service is **cache-unaware**; every read goes to the repo on every call — except `SearchAsync`, which is cache-only: the inner method throws `NotSupportedException` (search runs against the cached `CampInfo` snapshot in `CachingCampService`; there is no repository search method).
- `CampRepository` lives in `Humans.Camps.Data`, uses `IDbContextFactory<CampsDbContext>` (nobodies-collective/Humans#858), and is registered as Singleton.
- **Live event reads.** The decorator resolves the active year and early-entry grants through its inner `ICampService`; only `CampService` reads Settings and assembles grants. The settings slot still caches camp-owned fields while `PublicYear` is read live.
- **Caching decorator (T-06).** `CachingCampService` (Singleton, `Humans.Camps.Services`) wraps `ICampService` per design-rules §15d. It owns a `ConcurrentDictionary<Guid, CampInfo>` keyed by camp id (the canonical per-camp projection) plus a single-slot `CampSettingsInfo`. Warmup reuses the settings read path and does not publish its initial settings snapshot after the camp loads; settings invalidated and reloaded during warmup remain current. Year-keyed sub-views (`GetCampsForYearAsync`, the public/placement summaries that `CampApiController` projects from it) are filtered **snapshots** of the canonical cache, not separate entries. Lead checks (`CampInfo.IsLead`, used by the auth handler) answer from the snapshot.
- **Invalidation** — decorator-only. Every mutating method on `CachingCampService` delegates to the inner service and then calls `InvalidateCampAsync(campId)` (or `InvalidateSettingsAsync()`) inline. Thrown failures or cancellation clear both the camp snapshot/warm-year index and settings slot before propagating the original exception; cache cleanup does not use the cancelled request token. `UpdateCampAsync` also invalidates after a returned failure because camp fields may already have committed; successful deletion still tombstones only its camp. Cross-table effects ride the same path because the mutating method already knows the affected camp id — `SetEarlyEntryAsync`, `RemoveCampMemberAsync`, `LeaveCampMembershipAsync`, and the membership confirm/withdraw methods all invalidate after writing to `camp_members`, which is what keeps `CampSeasonInfo.EeGrantedCount` honest. The no-bypass rule (only the inner `CampService` / `CampRoleService` may touch `ICampRepository`) replaced the earlier `CampInfoSaveChangesInterceptor` backstop. This is documentation, not a pinned test — see [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md).
- **Warmup.** `CachingCampService` warms through `TrackedCache` at startup and lazily on reads. It discovers camps through `CampSettingsInfo.PublicYear` (the active event year, sourced from Settings, falling back to the clock year), `OpenSeasons`, and the current year, then loads each camp's complete projection through `GetCampByIdAsync`. Cached slug/id reads retain earlier seasons and their lead assignments; year-list reads still filter to the requested season. A prior-season lead therefore retains access to a pending renewal without requiring a copied assignment.
- **Cache size budget.** The canonical projection retains full season history for discovered camps. Season blurbs and membership projections dominate its footprint; `/Debug/CacheStats` reports the retained size against the §15 budget.
- **Leads invariant (T-06).** Lead identities live on the read model as `CampSeasonInfo.LeadUserIds` / `WorkshopLeadUserIds` (non-null, empty when none), populated from `CampRoleAssignment` special roles. There is no separate `GetCampsWithLeadsForYearAsync` service method — callers use `GetCampsForYearAsync` and read leads off the returned `CampInfo` seasons.
- Filesystem I/O for camp images is abstracted behind the shared `IFileStorage` abstraction (`Humans.Base` interface + `FileSystemFileStorage` implementation in `Humans.Web`, rooted at `wwwroot/`); the service never touches `System.IO`.
- **Cross-domain navs stripped:** `Camp.CreatedByUser` and `CampSeason.ReviewedByUser` (nobodies-collective/Humans#934). The FKs are write-only provenance — `CampService` stamps `CreatedByUserId` on create and nothing reads either id back for display, so there is no stitching call to route. If a screen ever needs those names, resolve them via `IUserServiceRead.GetUserInfosAsync`; do not re-add the navs.
- `CampContactService` has no owned DB tables and does not inject `HumansDbContext`; it retains its `IMemoryCache` rate-limit usage since that's a request-acceleration cache, not canonical domain data. Its member contact form requires a nonblank message of at most 2000 characters and uses shared annotation translations in all six cultures.
- `CampRoleService` lives in `Humans.Camps.Services.CampRoleService` and goes through `ICampRepository` (`Humans.Camps.Data`) for all data access. The role-heavy methods are grouped in `CampRepository.Roles.cs`; `CampRepository` owns `camp_role_definitions` and `camp_role_assignments` alongside the rest of the Camps tables. Display-name stitching for `AssignedByUserId` routes through `IUserServiceRead.GetUserInfosAsync`. Plain pass-through (no caching decorator); add `IMemoryCache` later if list-of-definitions reads dominate.
- **Architecture test** — `tests/Humans.Camps.Tests/Architecture/CampsArchitectureTests.cs`.
- **Read/write interface split.** `ICampServiceRead` (7 methods: GetCampsForYearAsync, GetCampBySlugAsync, GetCampByIdAsync, GetCampSeasonByIdAsync, GetSettingsAsync, SearchAsync, GetCampUserInfoAsync) is the cross-section read surface — returns only CampInfo-family projections (CampInfo with computed `Active`, CampSeasonInfo, CampUserInfo), CampSettingsInfo, CampSearchHit; no EF entities. `GetCampUserInfoAsync(userId)` resolves a user's active-`PublicYear` camp membership (the attached `CampSeasonInfo` plus the named camp roles they hold, ordered by role sort order) from the cached projection — no DB hit; returns `CampUserInfo.None` when the user is not an Active member of any camp this year. It feeds the admin human card and the Shifts coordinator view; `CachingCampService` serves it from the warm projection (PublicYear is always warm). `ICampService : ICampServiceRead` adds writes, cache invalidation, per-user/lead/membership reads, and Camps-internal reads. External sections inject `ICampServiceRead`; the other public contracts are `ICampLeadDirectory` (year-agnostic lead reads consumed by `SystemTeamSyncJob`) and the dev-seeder-only `ICampSeeding` / `ICampRoleSeeding`. CampLookup/CampSeasonLookup were folded into CampInfo/CampSeasonInfo. See `memory/architecture/section-read-write-split.md`.

### Touch-and-clean guidance

- Lead authority is `CampRoleAssignment` only. The `AuditAction.CampLeadAdded` / `CampLeadRemoved` enum members stay because historical `audit_log` rows persist those strings.
- `CampMemberConfiguration.cs` lives in
  `src/Sections/Humans.Camps/Data/Configurations/` with the rest of the Camps entity configuration.

## Issue queue

Camps owns the `Camps` issue queue: it implements `IIssueQueueOwner` (Issues' `Contracts/`
folder) on its `Section` entry point, declaring the queue key and the roles that handle
issues filed against it — `CampAdmin`, plus `Admin`, which handles every queue. Issues
discovers the declaration through DI and holds no list of sections; dropping the seam
sends this section's stored issues to the Admin-only queue.

Whole-camp deletion evicts all Early Entry answers after its ambient transaction has disposed, including failed completion; later audit or image-cleanup failures cannot retain removed camp grants.
