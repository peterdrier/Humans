<!-- freshness:triggers
  src/Humans.Application/Services/Profile/**
  src/Humans.Application/Services/Gdpr/**
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Controllers/ProfileApiController.cs
  src/Humans.Web/Views/Profile/**
  src/Humans.Domain/Entities/Profile.cs
  src/Humans.Infrastructure/Data/Configurations/Profiles/**
  src/Humans.Infrastructure/Jobs/ProcessAccountDeletionsJob.cs
-->
<!-- freshness:flag-on-change
  Profile fields, edit page sections, visibility matrix, and GDPR export/deletion workflow — review when Profile entity, ProfileService, or ProfileController change.
-->

# Profiles

## Business Context

Members need to maintain personal information for organizational records while protecting privacy. The system distinguishes between legal names (restricted access) and public "burner names" used within the community. Location data supports event planning and regional coordination.

## User Stories

### US-2.1: View My Profile
**As a** member
**I want to** view my complete profile information
**So that** I can verify what the organization has on record

**Acceptance Criteria:**
- Profile displays all personal information
- Shows membership status badge
- Displays list of team memberships
- Shows consent status for legal documents
- Shows tier application status banner (Submitted/Approved/Rejected) linking to Governance page, if the user has a non-withdrawn tier application

### US-2.2: Edit Personal Information
**As a** member
**I want to** update my personal details
**So that** the organization has accurate contact information

**Acceptance Criteria:**
- Can edit burner name, legal name, phone, bio
- Can update location with Google Places autocomplete
- Changes are timestamped (UpdatedAt)
- Validation prevents invalid data

### US-2.3: Set Burner Name
**As a** member
**I want to** set a public "burner name" separate from my legal name
**So that** my real identity is protected in public contexts

**Acceptance Criteria:**
- Burner name displayed in team listings and public views
- Legal name only visible to member and board members
- User's display name syncs with burner name

### US-2.4: Location Autocomplete
**As a** member
**I want to** enter my location using autocomplete
**So that** I can easily specify my city without manual entry

**Acceptance Criteria:**
- Google Places autocomplete suggestions as user types
- Selecting a place captures: city, country, coordinates
- Stored coordinates enable future map features
- PlaceId stored for reference

## Data Model

### Profile Entity
```
Profile
├── Id: Guid
├── UserId: Guid (FK → User, 1:1)
├── BurnerName: string? (256)
├── FirstName: string (256) [legal]
├── LastName: string (256) [legal]
├── Pronouns: string? (50)
├── DateOfBirth: LocalDate?
├── City: string? (256)
├── CountryCode: string? (2)
├── Latitude: double?
├── Longitude: double?
├── PlaceId: string? (256) [Google Places ID]
├── Bio: string? (4000)
├── ContributionInterests: string? [public]
├── PersonalBoundaries: string? [public]
├── EmergencyContactName: string? (256) [board only]
├── EmergencyContactPhone: string? (50) [board only]
├── EmergencyContactRelationship: string? (100) [board only]
├── AdminNotes: string? (4000) [admin only]
├── NoPriorBurnExperience: bool (default false)
├── IsSuspended: bool
├── CreatedAt: Instant
└── UpdatedAt: Instant
```

## Emergency Contact

Members can optionally provide emergency contact information for safety at events.

### Fields
- **EmergencyContactName** (string, max 256) — Name of the emergency contact person
- **EmergencyContactPhone** (string, max 50) — Phone number
- **EmergencyContactRelationship** (string, max 100) — Relationship (e.g., "Partner", "Parent")

### Visibility Rules
- **Profile owner**: Can view and edit their own emergency contact on their profile
- **Board/Admin**: Can view emergency contact on the Admin Member Detail page
- **Other members**: Cannot see emergency contact information (not shown on public profile views)

### GDPR
Emergency contact fields are marked `[PersonalData]` and included in the data export (`ExportData`).

## Membership Status

Profile includes a computed `MembershipStatus` property:

| Status | Description | Visual |
|--------|-------------|--------|
| **Active** | Has roles + all consents signed | Green badge |
| **Inactive** | Has roles but missing consents | Yellow badge |
| **Suspended** | Admin-suspended | Red badge |
| **None** | No active roles | Gray badge |

## Privacy Model

### Data Visibility Matrix

| Field | Member | Other Members | Lead | Board | Admin |
|-------|--------|---------------|----------|-------|-------|
| Burner Name | Yes | Yes | Yes | Yes | Yes |
| Legal Name | Yes | No | No | Yes | Yes |
| Emails (UserEmail) | Per-field visibility | Per-field visibility | Per-field visibility | Yes | Yes |
| City/Country | Yes | Yes | Yes | Yes | Yes |
| Coordinates | No | No | No | Yes | Yes |
| Bio | Yes | Yes | Yes | Yes | Yes |
| Emergency Contact | Yes | No | No | Yes | Yes |
| Admin Notes | No | No | No | No | Yes |

## Location Capture Flow

```
┌──────────────┐
│  User types  │
│  location    │
└──────┬───────┘
       │
┌──────▼───────────────┐
│  Google Places API   │
│  Autocomplete        │
└──────┬───────────────┘
       │
┌──────▼───────────────┐
│  User selects        │
│  suggestion          │
└──────┬───────────────┘
       │
┌──────▼───────────────┐
│  Fetch place details │
│  (gmp-select event)  │
└──────┬───────────────┘
       │
┌──────▼───────────────┐
│  Extract & store:    │
│  - City              │
│  - CountryCode       │
│  - Latitude          │
│  - Longitude         │
│  - PlaceId           │
└──────────────────────┘
```

## Profile Edit Sections

The Profile Edit page (`/Profile/Edit`) is organized into four card sections within a single form:

### Section 1: General Information (always visible)
Profile picture, burner name, pronouns, location (Google Places), birthday, contact information, bio.

### Section 2: Contributor Information (always visible)
Burner CV entries with "No prior burn experience" checkbox, contribution interests. Includes a note that this info may be considered for tier applications.

### Section 3: Application (initial setup only)
Membership tier selection, motivation, additional info, Asociado-specific questions. Only shown when `IsInitialSetup` is true. After onboarding, users apply via `/Application/Create`.

### Section 4: Private Information (always visible)
Legal first name(s), last name, emergency contact, board notes. Prefixed with lock icon and "only visible to you and the board" note.

### Section Ordering
- **Normal order:** General → Contributor → Application (if initial) → Private
- **ShowPrivateFirst:** Private → General → Contributor → Application (if initial)
- `ShowPrivateFirst` is true when FirstName, LastName, and EmergencyContactName are all empty (new user hasn't filled them in yet)

### Burner CV Validation
Users must provide at least one Burner CV entry **or** check the "No prior burn experience" checkbox. Validated both client-side (on submit) and server-side.

## Validation Rules

| Field | Validation |
|-------|------------|
| FirstName | Required, max 256 chars |
| LastName | Required, max 256 chars |
| BurnerName | Optional, max 256 chars |
| Bio | Optional, max 4000 chars |
| CountryCode | Optional, ISO 3166-1 alpha-2 |
| Burner CV | At least one entry OR NoPriorBurnExperience checked |

## Admin Capabilities

1. **View Any Profile**: Full access to all profile fields
2. **Suspend Member**: Set IsSuspended = true with AdminNotes
3. **Unsuspend Member**: Clear suspension status
4. **Edit Admin Notes**: Internal notes not visible to member

## GDPR Data Rights

### Data Export (Right of Access)

Members can download all their personal data as JSON from `/Profile/DownloadData`. The export includes profile fields, contact fields, volunteer history, consent records, team memberships, applications, and role assignments. Response headers disable caching (`no-store`).

**Route:** `GET /Profile/DownloadData`

### Account Deletion (Right to Erasure)

Members can request account deletion from the Privacy page (`/Profile/Privacy`). The process uses a 30-day grace period with anonymization rather than hard delete (per decision R-02).

#### Deletion Workflow

```
User requests deletion (/Profile/RequestDeletion)
    │
    ├── DeletionRequestedAt = now
    ├── DeletionScheduledFor = now + 30 days
    ├── End memberships (immediate)
    │   • TeamMemberships: LeftAt = now
    │   • RoleAssignments: ValidTo = now
    │   • Audit log: MembershipsRevokedOnDeletionRequest
    ├── Confirmation email sent
    │
    │   ┌─────────────────────────────────────┐
    │   │  30-day grace period                │
    │   │  • User can still log in            │
    │   │  • User can cancel at any time      │
    │   │    via /Profile/CancelDeletion      │
    │   │  • Memberships already revoked      │
    │   │  • Cancellation does NOT restore    │
    │   │    memberships (must rejoin teams)   │
    │   │  • System sync auto-re-enrolls in   │
    │   │    Volunteers if user is still       │
    │   │    approved with valid consents      │
    │   └─────────────────────────────────────┘
    │
    ▼ Grace period expires
ProcessAccountDeletionsJob (daily)
    │
    ├── Anonymize user record
    │   • DisplayName → "Deleted User"
    │   • Email → "deleted-{id}@deleted.local"
    │   • Phone, pronouns, DOB, profile picture → null
    │   • Emergency contact fields → null
    │
    ├── Remove related data
    │   • UserEmails (all removed)
    │   • ContactFields (all removed)
    │   • VolunteerHistoryEntries (all removed)
    │
    ├── End memberships (safety net, idempotent)
    │   • TeamMemberships: LeftAt = now (only if still null)
    │   • RoleAssignments: ValidTo = now (only if still null)
    │
    ├── Disable login
    │   • LockoutEnd = DateTimeOffset.MaxValue
    │   • SecurityStamp rotated
    │
    ├── Audit log: AccountAnonymized
    ├── Confirmation email to original address
    │
    └── Preserved for audit trail:
        • ConsentRecords (immutable, anonymized via user FK)
        • Applications (anonymized via user FK)
```

#### Google Workspace Deprovisioning

Google permissions (Shared Drive access, Group memberships) are **not** revoked by the deletion job directly. Instead, deprovisioning happens through the normal membership lifecycle:

1. The anonymization job ends all team memberships (`LeftAt = now`)
2. The overnight sync job (`SystemTeamSyncJob` / `GoogleResourceReconciliationJob`) detects the ended memberships and removes the corresponding Google permissions

This two-step approach ensures Google deprovisioning uses the same tested code path as any other team departure, rather than a separate deletion-specific implementation.

> **Note:** The automated sync jobs are currently disabled during initial rollout. Google permissions are managed manually via the "Sync Now" button at `/Admin/GoogleSync` until automated sync is validated. Sync jobs must be able to add members reliably before removal logic is enabled.

#### Routes

| Route | Method | Purpose |
|-------|--------|---------|
| `/Profile/Privacy` | GET | View deletion status, data export link |
| `/Profile/RequestDeletion` | POST | Start 30-day deletion countdown |
| `/Profile/CancelDeletion` | POST | Cancel pending deletion |
| `/Profile/DownloadData` | GET | Download personal data as JSON |

## Related Features

- [Authentication](01-authentication.md) - Profile created after first login
- [Volunteer Status](05-volunteer-status.md) - Computed from profile approval + consents
- [Contact Fields](10-contact-fields.md) - Granular contact info visibility
- [Administration](09-administration.md) - Admin member management
- [Profile Pictures & Birthdays](14-profile-pictures-birthdays.md) - Custom profile pictures and birthday calendar
