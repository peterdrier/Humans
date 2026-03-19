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
- Shows historical names if any
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

### Enums
```
CampSeasonStatus: Pending(0), Active(1), Full(2), Rejected(4), Withdrawn(5)
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
| `GET /api/camps/{year}` | JSON API: camps for year |
| `GET /api/camps/{year}/placement` | JSON API: placement data |

## Related Features

- [Authentication](01-authentication.md) - User identity for camp registration and lead management
- [Teams](06-teams.md) - Similar self-organizing group concept; camps are event-specific
- [Administration](09-administration.md) - Admin role provides full camp management access
