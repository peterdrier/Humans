# Contact Fields with Granular Visibility

## Business Context

Members need to share contact information (Signal, Telegram, WhatsApp, Discord, phone) with other members, but different contexts require different privacy levels. A member might want their Signal handle visible to all active members, but their phone number only visible to their team coordinators or board members.

**Note:** Email addresses are managed separately via the `UserEmail` entity (see below). The `Email` contact field type is deprecated.

## User Stories

### US-10.1: Add Contact Fields
**As a** member
**I want to** add multiple contact methods to my profile
**So that** other members can reach me through my preferred channels

**Acceptance Criteria:**
- Can add unlimited contact fields
- Choose from predefined types: Phone, Signal, Telegram, WhatsApp, Discord
- Can add "Other" type with custom label (e.g., Matrix, IRC)
- Each field has a value (the actual contact info)
- Email addresses are managed separately via the Manage Emails page (`/Profile/Emails`)
- Fields can be reordered

### US-10.2: Set Per-Field Visibility
**As a** member
**I want to** control who can see each contact field
**So that** I can share different information with different groups

**Acceptance Criteria:**
- Each field has independent visibility setting
- Visibility levels from most to least restrictive:
  - Board only
  - Coordinators + Board (coordinators and board)
  - My teams (members sharing a team with me)
  - All active members
- Default visibility is "All active members"

### US-10.3: View Contact Fields on Profile
**As a** member viewing another member's profile
**I want to** see their contact fields appropriate to my access level
**So that** I can reach them through their shared channels

**Acceptance Criteria:**
- Only shows fields I'm authorized to see
- Displays visibility icon indicating the restriction level
- Shows field type icon for quick recognition
- Fields ordered by member's preference

### US-10.4: Edit Contact Fields
**As a** member
**I want to** update or remove my contact fields
**So that** my contact information stays current

**Acceptance Criteria:**
- Can edit value, type, and visibility of existing fields
- Can delete fields
- Can reorder fields
- Empty fields are automatically removed on save

## Data Model

### ContactField Entity
```
ContactField
├── Id: Guid
├── ProfileId: Guid (FK → Profile)
├── FieldType: ContactFieldType [enum]
├── CustomLabel: string? (100) [for "Other" type]
├── Value: string (500)
├── Visibility: ContactFieldVisibility [enum]
├── DisplayOrder: int
├── CreatedAt: Instant
├── UpdatedAt: Instant
└── Computed: DisplayLabel
```

### Enums
```
ContactFieldType:
  Email = 0     [Obsolete - use UserEmail entity]
  Phone = 1
  Signal = 2
  Telegram = 3
  WhatsApp = 4
  Discord = 5
  Other = 99

ContactFieldVisibility:
  BoardOnly = 0        // Most restrictive
  LeadsAndBoard = 1
  MyTeams = 2
  AllActiveProfiles = 3 // Least restrictive
```

Note: Lower enum values are more restrictive. This enables `>=` comparisons for filtering.

## Visibility Access Logic

### Access Level Determination

When viewer looks at owner's profile:

```
GetViewerAccessLevel(ownerUserId, viewerUserId):

  1. Self → BoardOnly (sees everything)

  2. Board member → BoardOnly (sees everything)

  3. Any coordinator (of any team) → LeadsAndBoard

  4. Shares team with owner → MyTeams

  5. Active member → AllActiveProfiles only
```

### Filtering Logic

A viewer with access level X can see fields where `Visibility >= X`.

| Viewer Access | Sees BoardOnly | Sees LeadsAndBoard | Sees MyTeams | Sees AllActive |
|---------------|----------------|-------------------|--------------|----------------|
| BoardOnly (0) | Yes | Yes | Yes | Yes |
| LeadsAndBoard (1) | No | Yes | Yes | Yes |
| MyTeams (2) | No | No | Yes | Yes |
| AllActiveProfiles (3) | No | No | No | Yes |

## Example Scenarios

### Scenario 1: Board Member Viewing
Alice (board member) views Bob's profile:
- Access level: BoardOnly (0)
- Sees: All contact fields regardless of visibility

### Scenario 2: Team Coordinator Viewing
Carol (coordinator of Art team) views Bob's profile:
- Access level: LeadsAndBoard (1)
- Sees: Fields with visibility LeadsAndBoard, MyTeams, or AllActiveProfiles
- Cannot see: Fields with BoardOnly visibility

### Scenario 3: Teammate Viewing
Dave (member of same team as Bob) views Bob's profile:
- Access level: MyTeams (2)
- Sees: Fields with visibility MyTeams or AllActiveProfiles
- Cannot see: Fields with BoardOnly or LeadsAndBoard visibility

### Scenario 4: Regular Member Viewing
Eve (active member, no shared teams) views Bob's profile:
- Access level: AllActiveProfiles (3)
- Sees: Only fields with AllActiveProfiles visibility
- Cannot see: Any restricted fields

## UI Components

### Profile View (Index)
- Contact fields displayed in definition list format
- Icon for field type (envelope, phone, etc.)
- Visibility icon with tooltip
- "Add" button if no fields exist

### Profile Edit
- Dynamic form with add/remove capability
- Dropdown for field type (shows/hides custom label for "Other")
- Text input for value
- Dropdown for visibility level
- Delete button per row
- JavaScript handles dynamic row management

### Visibility Icons

| Visibility | Icon | Tooltip |
|------------|------|---------|
| BoardOnly | Lock | "Visible to board members only" |
| LeadsAndBoard | User Shield | "Visible to team coordinators and board" |
| MyTeams | Users | "Visible to members of your teams" |
| AllActiveProfiles | Globe | "Visible to all active members" |

## Service Interface

```csharp
IContactFieldService
├── GetVisibleContactFieldsAsync(profileId, viewerUserId)
│   → Returns fields filtered by viewer's access level
├── GetAllContactFieldsAsync(profileId)
│   → Returns all fields for editing (owner only)
├── SaveContactFieldsAsync(profileId, fields)
│   → Upsert/delete contact fields
└── GetViewerAccessLevelAsync(ownerUserId, viewerUserId)
    → Determines what visibility level viewer has
```

## Database Schema

```sql
CREATE TABLE contact_fields (
    "Id" uuid PRIMARY KEY,
    "ProfileId" uuid NOT NULL REFERENCES profiles("Id") ON DELETE CASCADE,
    "FieldType" varchar(50) NOT NULL,
    "CustomLabel" varchar(100),
    "Value" varchar(500) NOT NULL,
    "Visibility" varchar(50) NOT NULL,
    "DisplayOrder" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL
);

CREATE INDEX IX_contact_fields_ProfileId ON contact_fields("ProfileId");
CREATE INDEX IX_contact_fields_ProfileId_Visibility ON contact_fields("ProfileId", "Visibility");
```

## UserEmail — Unified Email Management

Email addresses are managed separately from contact fields via a dedicated `UserEmail` entity owned by `User` (not `Profile`). This ensures email verification, notification routing, and OAuth identity are all handled in one place.

### UserEmail Entity
```
UserEmail
├── Id: Guid
├── UserId: Guid (FK → User)
├── Email: string (256)
├── IsVerified: bool
├── IsOAuth: bool               ← true for login email, not deletable
├── IsNotificationTarget: bool  ← exactly one per user
├── Visibility: ContactFieldVisibility? ← null = hidden from profile
├── VerificationSentAt: Instant?
├── DisplayOrder: int
├── CreatedAt: Instant
└── UpdatedAt: Instant
```

### Constraints
- Unique index on `Email` where `IsVerified = true` (prevents email squatting)
- Exactly one `IsNotificationTarget = true` per user (app-level enforcement)
- OAuth emails cannot be deleted

### Manage Emails Page (`/Profile/Emails`)
- Lists all emails: OAuth email first (non-deletable, always verified), then additional
- Each row: email address, verified badge, notification target control, visibility dropdown, delete button
- "Add email" form sends verification, shows pending state
- Verification uses `UserManager.GenerateUserTokenAsync` / `VerifyUserTokenAsync`
- 5-minute cooldown between verification requests

### Service Interface
```csharp
IUserEmailService
├── GetUserEmailsAsync(userId)
│   → All emails for the user (for manage emails page)
├── GetVisibleEmailsAsync(userId, accessLevel)
│   → Emails visible on profile based on viewer access
├── AddEmailAsync(userId, email)
│   → Adds new email and returns verification token
├── VerifyEmailAsync(userId, token)
│   → Verifies email, returns verified address
├── SetNotificationTargetAsync(userId, emailId)
│   → Sets which verified email receives notifications
├── SetVisibilityAsync(userId, emailId, visibility)
│   → Updates profile visibility for an email
├── DeleteEmailAsync(userId, emailId)
│   → Removes a non-OAuth email
└── RemoveAllEmailsAsync(userId)
    → Removes all emails (account deletion)
```

## Related Features

- [Profiles](02-profiles.md) - Contact fields and emails extend the profile
- [Teams](06-teams.md) - Team membership affects visibility access
- [Administration](09-administration.md) - Board members have full visibility
