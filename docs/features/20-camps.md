<!-- freshness:triggers
  src/Humans.Application/Services/Camps/**
  src/Humans.Web/Controllers/CampController.cs
  src/Humans.Web/Controllers/CampAdminController.cs
  src/Humans.Web/Controllers/CampApiController.cs
  src/Humans.Web/Controllers/HumansCampControllerBase.cs
  src/Humans.Web/Views/Camp/**
  src/Humans.Web/Views/CampAdmin/**
  src/Humans.Domain/Entities/Camp.cs
  src/Humans.Domain/Entities/CampSeason.cs
  src/Humans.Domain/Entities/CampLead.cs
  src/Humans.Domain/Entities/CampMember.cs
  src/Humans.Domain/Entities/CampImage.cs
  src/Humans.Domain/Entities/CampHistoricalName.cs
  src/Humans.Domain/Entities/CampSettings.cs
  src/Humans.Infrastructure/Data/Configurations/Camps/**
  src/Humans.Infrastructure/Data/Configurations/CampMemberConfiguration.cs
-->
<!-- freshness:flag-on-change
  Camp registration/season approval workflow, CampMember per-season affiliation, lead management, and route table — review when Camp services, controllers, or entities change.
-->

# Camps

## Business Context

Nobodies Collective organizes camping areas ("barrios") at Nowhere and related events. Each camp is a self-organizing community that registers annually, receives admin approval, and is listed publicly. Camps have leads who manage their profile, season data, and membership status. The system tracks camp history across years through seasonal opt-ins.

## User Stories

### US-20.1: Browse Camps
**As a** visitor or member
**I want to** see all camps for the current public year
**So that** I can discover communities I might want to join

**Acceptance Criteria:**
- Public page showing all active camps as cards
- Filter by vibe, sound zone, kids-friendly, accepting members
- Each card shows name, short description, image, vibes, and status badges
- Sorted alphabetically by name
- Clicking a card navigates to the camp detail page

### US-20.2: View Camp Details
**As a** visitor or member
**I want to** see detailed information about a camp
**So that** I can learn about its community and decide whether to join

**Acceptance Criteria:**
- Shows camp name, links (with platform icons), description, images
- Contact email is hidden — replaced with facilitated "Contact this camp" button (login required)
- Displays current season data (vibes, kids policy, performance space, etc.)
- Shows leads with display names (authenticated users only)
- Shows historical names if any (hidden when camp has `HideHistoricalNames` enabled)
- Leads and admins see edit link

### US-20.3: Register a New Camp
**As an** authenticated member
**I want to** register a new camp
**So that** my community can participate in the event

**Acceptance Criteria:**
- Only available when a season is open for registration
- Captures camp details: name, contact info, Swiss camp flag, times at Nowhere
- Captures season-specific data: description, vibes, kids policy, sound zone, etc.
- Optional historical names (comma-separated)
- Creates camp with Pending status
- Registering user becomes Primary Lead
- Redirects to detail page with success message

### US-20.4: Edit Camp
**As a** camp lead or CampAdmin
**I want to** update my camp's information
**So that** the listing stays current

**Acceptance Criteria:**
- Leads can edit their own camp; CampAdmin/Admin can edit any
- Can update contact info, season data, and camp-level fields
- Can toggle "Hide historical names" to suppress the "Also known as" section on the public detail page
- Name change blocked after name lock date
- Can upload, delete, and reorder images
- Can manage co-leads (add, remove, transfer primary)

### US-20.5: Opt-In to New Season
**As a** camp lead
**I want to** opt my camp into a new open season
**So that** we can participate again this year

**Acceptance Criteria:**
- Only available when target season is open
- Creates a new CampSeason with Pending status
- Copies camp identity but requires fresh season data review
- Redirects to edit page

### US-20.6: Approve/Reject Season Registration
**As a** CampAdmin or Admin
**I want to** review and approve or reject pending camp registrations
**So that** only legitimate camps appear in the public listing

**Acceptance Criteria:**
- Admin dashboard shows all pending seasons
- Approve transitions season to Active status
- Reject requires notes explaining the reason
- Records reviewer ID and timestamp

### US-20.7: Manage Seasons
**As a** CampAdmin or Admin
**I want to** open/close registration seasons, set the public year, and configure name lock dates
**So that** the camp registration lifecycle is controlled

**Acceptance Criteria:**
- Open a season by year (adds to OpenSeasons list)
- Close a season by year (removes from OpenSeasons list)
- Set public year (controls which year is shown on the public page)
- Set name lock date per year (prevents name changes after date)

### US-20.8: Delete Camp
**As an** Admin
**I want to** permanently delete a camp
**So that** invalid or test entries can be removed

**Acceptance Criteria:**
- Admin-only action (not CampAdmin)
- Deletes camp and all related data (seasons, leads, images, historical names)
- Requires confirmation

### US-20.9: View Season Details by Year
**As a** visitor or member
**I want to** view a camp's details for a specific season year
**So that** I can see historical or non-current season information

**Acceptance Criteria:**
- Accessible at `/Camps/{slug}/Season/{year}`
- Returns 404 if camp or season not found
- Reuses the detail view with the specified season's data

### US-20.10: API Access
**As a** website developer
**I want to** access camp data via JSON API
**So that** I can integrate camp listings into the main website

**Acceptance Criteria:**
- `GET /api/camps/{year}` returns all camps with season data for a year
- `GET /api/camps/{year}/placement` returns placement-relevant data (space, sound zone, containers, electrical)
- Both endpoints are public (no authentication required)

### US-20.11: Export Camps CSV
**As a** CampAdmin or Admin
**I want to** export camp data as CSV
**So that** I can analyze camp registrations in a spreadsheet

**Acceptance Criteria:**
- Export button on Barrios Admin page
- Exports all camps for the current public year
- Includes: name, slug, status, contact info, leads, season data (languages, member count, placement, vibes)
- CSV file named `barrios-{year}.csv`

### US-20.12: Per-camp roles
**As a** camp lead, CampAdmin, or Admin
**I want to** assign humans in my camp to per-camp roles (Consent Lead, LNT, Shit Ninja, Power, Build Lead, etc.)
**So that** the org knows who is responsible for the role each season and can flag camps that are missing required leads

**As a** CampAdmin
**I want to** manage the catalogue of role definitions (name, description, slot count, minimum required for compliance, sort order, required-for-compliance flag)
**So that** the role list can evolve year over year without code changes

**Acceptance Criteria:**
- CampAdmin can create, edit, deactivate, and reactivate role definitions at `/Camps/Admin/Roles`. Deactivated definitions are hidden from new-assignment UI but historical assignments stay intact.
- Per-camp role assignments live on the Camp Edit page (`/Camps/{slug}/Edit`), one card per active role definition, ordered by `SortOrder`. Each card renders one row per slot up to `SlotCount`; filled slots show the assigned human plus an unassign button, empty slots show a `_HumanSearchInput` member picker.
- Assigning a role requires the picked human to be an Active member of the **same camp-season**. Service rejects with `MemberNotActive` / `MemberSeasonMismatch` otherwise.
- Soft-cap: the service rejects an assignment that would exceed `SlotCount`. If a CampAdmin reduces `SlotCount` below current usage, the card renders an "over capacity" indicator and a lead must unassign one to drop back into capacity.
- A human cannot hold the same role twice in the same season (DB unique index + `AlreadyHoldsRole` outcome).
- Lead-add-active-member shortcut: if the human a lead wants to assign isn't yet a CampMember, the lead can add them directly at `/Camps/{slug}/Members/Add` (creates `CampMember(Active)` without going through the request/approve flow). Audited as `CampMemberAddedByLead`.
- Compliance report at `/Camps/Admin/Compliance` lists camps where any required role's filled-slot count is below `MinimumRequired` for the chosen year (defaults to current public year).
- All role visibility is **auth-gated**. The public Camp Details page does not render role assignments — only authenticated users see the roles section.
- Removing a CampMember (Leave / Withdraw / Remove) clears any role assignments held by that member via `ICampRoleService.RemoveAllForMemberAsync`.

### US-20.13: Tell Humans I'm in a camp this season
**As an** authenticated human
**I want to** tell Humans which camp I've joined this season
**So that** I can receive per-camp notifications, be assigned to per-camp roles (e.g. LNT lead), and claim Early Entry allocations — without the app claiming to manage the camp's real membership.

**Acceptance Criteria:**
- On a camp's detail page, an authenticated human with no existing membership sees a "Request to join for {year}" button (only when the camp has an Active or Full season for the public year).
- Copy on the request card explicitly states that this does NOT join you to the camp — do that through the camp's own process first.
- A pending request can be withdrawn by the requester; an active membership can be left by the member.
- Membership state (Pending / Active) is never rendered on anonymous views.
- Leads and CampAdmin see pending requests on the camp edit page with Approve / Reject buttons, and active members with a Remove button.
- Approve / Reject mutations are scoped to the authorizing camp — a lead of camp A cannot mutate camp B's memberships by submitting a crafted member id.
- Concurrent duplicate "request" submissions resolve idempotently — the second one returns the winning row instead of a 500.
- When a season is rejected or withdrawn, pending requesters receive a notification. Pending rows are left as-is (so if the season is later reactivated, the request is still live).
- A human's profile page shows a "My Barrios" panel grouping their memberships by year.

## Data Model

### Camp
```
Camp
├── Id: Guid
├── Slug: string [unique, URL-friendly]
├── ContactEmail: string
├── ContactPhone: string
├── WebOrSocialUrl: string? (legacy, read-only fallback — cleared when Links is populated)
├── Links: List<CampLink>? (jsonb — multiple URLs with auto-detected platform)
├── IsSwissCamp: bool
├── HideHistoricalNames: bool (default false — when true, "Also known as" is hidden on detail page)
├── TimesAtNowhere: int
├── CreatedByUserId: Guid (FK → User)
├── CreatedAt: Instant
├── UpdatedAt: Instant
└── Navigation: Seasons, Leads, HistoricalNames, Images
```

### CampSeason
```
CampSeason
├── Id: Guid
├── CampId: Guid (FK → Camp)
├── Year: int
├── Name: string
├── NameLockDate: LocalDate?
├── NameLockedAt: Instant?
├── Status: CampSeasonStatus [enum]
├── BlurbLong / BlurbShort: string
├── Languages: string
├── AcceptingMembers: YesNoMaybe
├── KidsWelcome: YesNoMaybe
├── KidsVisiting: KidsVisitingPolicy
├── KidsAreaDescription: string?
├── HasPerformanceSpace: PerformanceSpaceStatus
├── PerformanceTypes: string?
├── Vibes: List<CampVibe> [JSON]
├── AdultPlayspace: AdultPlayspacePolicy
├── MemberCount: int
├── SpaceRequirement: SpaceSize?
├── SoundZone: SoundZone?
├── ContainerCount: int
├── ContainerNotes: string?
├── ElectricalGrid: ElectricalGrid?
├── ReviewedByUserId: Guid?
├── ReviewNotes: string?
├── ResolvedAt: Instant?
├── CreatedAt: Instant
└── UpdatedAt: Instant
```

### CampLead
```
CampLead
├── Id: Guid
├── CampId: Guid (FK → Camp)
├── UserId: Guid (FK → User)
├── Role: CampLeadRole [Primary, CoLead]
├── JoinedAt: Instant
├── LeftAt: Instant? (null = active)
└── Computed: IsActive (LeftAt == null)
```

### CampSettings (singleton)
```
CampSettings
├── Id: Guid
├── PublicYear: int
└── OpenSeasons: List<int> [JSON]
```

### Supporting Entities
- **CampHistoricalName**: Id, CampId, Name, Year (int?), Source (CampNameSource), CreatedAt
- **CampImage**: Id, CampId, FileName, StoragePath, ContentType, SortOrder, UploadedAt

### CampMember (per-season affiliation, issue nobodies-collective#488)

Post-hoc record that a human is in a camp for a given season. The app does not
admit humans to a camp — each camp runs its own process (website, spreadsheet,
WhatsApp). This row exists so the app can attach per-camp roles (LNT lead, etc.),
Early Entry allocations, and notifications to the right humans.

```
CampMember
├── Id: Guid
├── CampSeasonId: Guid (FK → CampSeason, ON DELETE CASCADE)
├── UserId: Guid
├── Status: CampMemberStatus (Pending | Active | Removed)
├── RequestedAt: Instant
├── ConfirmedAt: Instant?
├── ConfirmedByUserId: Guid?
├── RemovedAt: Instant?
└── RemovedByUserId: Guid?
```

- Partial unique index `IX_camp_members_active_unique` on
  `(CampSeasonId, UserId) WHERE Status <> 'Removed'` — at most one live row per
  (season, user). Removed rows are tombstones kept for audit and allow
  re-requesting.
- The request flow is idempotent: a concurrent duplicate insert that races past
  the pre-check is caught (`23505`) and resolved to the winning row.
- Mutations by leads / CampAdmin (approve, reject, remove) are scoped by the
  authorizing camp id: a member id belonging to a different camp resolves to
  "not found" rather than being mutated cross-camp.
- When a season is rejected or withdrawn, pending requesters receive a
  `CampMembershipSeasonClosed` notification. Membership rows are not
  auto-mutated — if the season is reactivated the pending request is still live.

### CampRoleDefinition (catalogue of per-camp roles, issue nobodies-collective#489)

CampAdmin-managed catalogue. Each row defines a per-camp role (name, slot count,
compliance threshold). Soft-deleted definitions preserve historical assignments
but are hidden from new-assignment UI.

```
CampRoleDefinition
├── Id: Guid
├── Name: string [unique]
├── Description: string? [Markdown]
├── SlotCount: int (default 1) — how many slot rows render per camp-season; soft cap enforced in service
├── MinimumRequired: int (default 1) — slots required for compliance; 0 ≤ MinimumRequired ≤ SlotCount
├── SortOrder: int — display order on the Camp Edit roles panel
├── IsRequired: bool — true if the compliance report tracks this role
├── DeactivatedAt: Instant? (null = active)
├── CreatedAt: Instant
└── UpdatedAt: Instant
```

### CampRoleAssignment (per-season role assignment, issue nobodies-collective#489)

Assigns a `CampMember` to a `CampRoleDefinition` for a specific `CampSeason`.

```
CampRoleAssignment
├── Id: Guid
├── CampSeasonId: Guid (FK → CampSeason, ON DELETE CASCADE)
├── CampRoleDefinitionId: Guid (FK → CampRoleDefinition, ON DELETE RESTRICT)
├── CampMemberId: Guid (FK → CampMember, ON DELETE CASCADE)
├── AssignedAt: Instant
└── AssignedByUserId: Guid (scalar; no nav per design-rules §6)
```

- Unique index on `(CampSeasonId, CampRoleDefinitionId, CampMemberId)` — same human cannot hold the same role twice in the same season.
- Service precondition: the linked `CampMember` must have `Status = Active` for the same `CampSeasonId`.
- Cascades: removing a `CampMember` (Leave / Withdraw / Remove paths) calls `ICampRoleService.RemoveAllForMemberAsync` to clear assignments before the soft-delete.

#### Seed (5 spec roles)

| Name | IsRequired | MinimumRequired | SlotCount | SortOrder |
|---|---|---|---|---|
| Consent Lead | true | 1 | 2 | 10 |
| LNT | true | 1 | 1 | 20 |
| Shit Ninja | true | 1 | 1 | 30 |
| Power | false | 0 | 1 | 40 |
| Build Lead | true | 1 | 2 | 50 |

CampAdmin can edit the seed rows or add new definitions post-deploy.

### Enums
```
CampSeasonStatus: Pending(0), Active(1), Full(2), Rejected(4), Withdrawn(5)
CampMemberStatus: Pending(0), Active(1), Removed(2)
CampLeadRole: Primary(0), CoLead(1)
CampVibe: Adult(0), ChillOut(1), ElectronicMusic(2), Games(3), Queer(4), Sober(5), Lecture(6), LiveMusic(7), Wellness(8), Workshop(9)
CampNameSource: Manual(0), NameChange(1)
YesNoMaybe: Yes(0), No(1), Maybe(2)
SoundZone: Blue(0), Green(1), Yellow(2), Orange(3), Red(4), Surprise(5)
SpaceSize: Sqm150(0), Sqm300(1), Sqm450(2), Sqm600(3), Sqm800(4), Sqm1000(5), Sqm1200(6), Sqm1500(7), Sqm1800(8), Sqm2200(9), Sqm2800(10)
KidsVisitingPolicy: Yes(0), DaytimeOnly(1), No(2)
PerformanceSpaceStatus: Yes(0), No(1), WorkingOnIt(2)
AdultPlayspacePolicy: Yes(0), No(1), NightOnly(2)
ElectricalGrid: Yellow(0), Red(1), Norg(2), OwnSupply(3), Unknown(4)
```

## Registration Workflow

```
Authenticated User
        │
        ▼
┌───────────────────┐     Season
│ Check open season │──── closed ──→ Redirect with error
└─────────┬─────────┘
          │ open
          ▼
┌───────────────────┐
│ Fill registration │
│ form              │
└─────────┬─────────┘
          │
          ▼
┌───────────────────┐
│ Create Camp       │
│ + CampSeason      │
│ (Status=Pending)  │
│ + CampLead        │
│ (Role=Primary)    │
└─────────┬─────────┘
          │
          ▼
┌───────────────────┐
│ Redirect to       │
│ Detail page       │
└───────────────────┘
```

## Season Approval Workflow

```
                ┌─────────┐
                │ Pending │
                └────┬────┘
                     │
       ┌─────────────┼──────────────┐
       │             │              │
  ┌────▼────┐  ┌─────▼─────┐  ┌────▼─────┐
  │ Active  │  │ Rejected  │  │Withdrawn │
  └────┬────┘  └───────────┘  └────┬─────┘
       │                           │
  ┌────┼─────┐                     │
  │          │                     │
┌─▼──┐ ┌────▼─────┐               │
│Full│ │Withdrawn │               │
└─┬──┘ └────┬─────┘               │
  │          │                     │
  └────┬─────┴─────────────────────┘
       │
  Reactivate (CampAdmin only)
       │
  ┌────▼────┐
  │ Active  │
  └─────────┘
```

Transitions:
- Pending → Active (admin approves)
- Pending → Rejected (admin rejects)
- Pending → Withdrawn (lead withdraws)
- Active → Full (lead marks full)
- Active → Withdrawn (lead withdraws)
- Full → Active (CampAdmin reactivates)
- Withdrawn → Active (CampAdmin reactivates)

## Authorization

| Action | Required Role |
|--------|---------------|
| Browse camps | Public (AllowAnonymous) |
| View camp details | Public (AllowAnonymous) |
| Register camp | Authenticated |
| Edit camp | Camp Lead, CampAdmin, or Admin |
| Opt-in to season | Camp Lead, CampAdmin, or Admin |
| Manage leads | Camp Lead, CampAdmin, or Admin |
| Upload/delete images | Camp Lead, CampAdmin, or Admin |
| Approve/reject season | CampAdmin or Admin |
| Open/close season | CampAdmin or Admin |
| Set public year | CampAdmin or Admin |
| Set name lock date | CampAdmin or Admin |
| Delete camp | Admin only |
| Manage role definitions (create/edit/deactivate/reactivate) | CampAdmin or Admin |
| Assign / unassign per-camp role | Camp Lead, CampAdmin, or Admin |
| Add active member to camp (lead-driven shortcut) | Camp Lead, CampAdmin, or Admin |
| View role assignments | Authenticated users only (no anonymous render) |
| Compliance report | CampAdmin or Admin |
| JSON API | Public (AllowAnonymous) |

## URL Structure

| Route | Description |
|-------|-------------|
| `GET /Camps` | Public camp listing |
| `GET /Camps/{slug}` | Camp detail page |
| `GET /Camps/{slug}/Season/{year}` | Camp detail for specific season |
| `GET /Camps/Register` | Registration form |
| `POST /Camps/Register` | Submit registration |
| `GET /Camps/{slug}/Edit` | Edit form |
| `POST /Camps/{slug}/Edit` | Submit edits |
| `POST /Camps/{slug}/OptIn/{year}` | Opt-in to season |
| `POST /Camps/{slug}/Leads/Add` | Add co-lead |
| `POST /Camps/{slug}/Leads/Remove/{leadId}` | Remove lead |
| `POST /Camps/{slug}/Leads/TransferPrimary` | Transfer primary lead |
| `POST /Camps/{slug}/Images/Upload` | Upload image |
| `POST /Camps/{slug}/Images/Delete/{imageId}` | Delete image |
| `POST /Camps/{slug}/Images/Reorder` | Reorder images |
| `GET /Camps/Admin` | Admin dashboard |
| `POST /Camps/Admin/Approve/{seasonId}` | Approve season |
| `POST /Camps/Admin/Reject/{seasonId}` | Reject season |
| `POST /Camps/Admin/OpenSeason/{year}` | Open season |
| `POST /Camps/Admin/CloseSeason/{year}` | Close season |
| `POST /Camps/Admin/SetPublicYear` | Set public year |
| `POST /Camps/Admin/SetNameLockDate` | Set name lock date |
| `POST /Camps/Admin/Delete/{campId}` | Delete camp |
| `GET /Camps/Admin/Roles` | List role definitions |
| `POST /Camps/Admin/Roles/Create` | Create role definition |
| `POST /Camps/Admin/Roles/{id}/Edit` | Edit role definition |
| `POST /Camps/Admin/Roles/{id}/Deactivate` | Soft-delete role definition |
| `POST /Camps/Admin/Roles/{id}/Reactivate` | Reactivate a soft-deleted role definition |
| `GET /Camps/Admin/Compliance` | Required-role compliance report (defaults to current public year) |
| `POST /Camps/{slug}/Members/Add` | Lead-adds-active-member shortcut |
| `POST /Camps/{slug}/Roles/Assign` | Assign role for the current season |
| `POST /Camps/{slug}/Roles/{assignmentId}/Unassign` | Unassign role (controller verifies cross-camp ownership) |
| `GET /api/camps/{year}` | JSON API: camps for year |
| `GET /api/camps/{year}/placement` | JSON API: placement data |

## Related Features

- [Authentication](01-authentication.md) - User identity for camp registration and lead management
- [Teams](06-teams.md) - Similar self-organizing group concept; camps are event-specific
- [Administration](09-administration.md) - Admin role provides full camp management access
