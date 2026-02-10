# Profile Pictures & Birthday Calendar

## Business Context

Members need a way to personalize their profiles beyond the Google OAuth avatar. Custom profile pictures make team pages more personal and help members recognize each other. The birthday calendar fosters community by letting members see upcoming birthdays within the organization.

## User Stories

### US-14.1: Upload Profile Picture
**As a** member
**I want to** upload a custom profile picture
**So that** I can personalize my profile beyond my Google avatar

**Acceptance Criteria:**
- Can upload JPEG, PNG, or WebP images (max 2MB)
- Custom picture takes precedence over Google OAuth avatar
- Can remove custom picture to revert to Google avatar
- Picture is shown on profile page, team detail pages, and birthday calendar
- Picture served via dedicated endpoint with 1-hour cache

### US-14.2: View Team Photo Gallery
**As a** member
**I want to** see profile pictures of all team members on the team detail page
**So that** I can put faces to names

**Acceptance Criteria:**
- Team detail page shows members in a grid layout with profile pictures
- Metaleads shown first with larger photos (80x80) and primary border
- Regular members shown with standard photos (64x64)
- Placeholder initials shown for members without pictures
- Both custom and Google avatar pictures are displayed

### US-14.3: Set Date of Birth
**As a** member
**I want to** set my date of birth on my profile
**So that** my birthday appears in the team birthday calendar

**Acceptance Criteria:**
- Date of birth field on profile edit page
- Only month and day shown in the birthday calendar (privacy)
- DOB only visible to the member themselves and board members
- DOB included in GDPR data export
- Field is optional

### US-14.4: View Birthday Calendar
**As a** member
**I want to** see upcoming birthdays by month
**So that** I can celebrate with my teammates

**Acceptance Criteria:**
- Monthly view with navigation between months
- Shows profile picture, display name, day of month, and team memberships
- Only shows members who have set their date of birth
- Accessible from Teams index page
- Privacy note explaining visibility rules
- System teams excluded from team name display

## Data Model

### Profile Entity (additions)
```
Profile
├── DateOfBirth: LocalDate? [PersonalData]
├── ProfilePictureData: byte[]? [PersonalData]
└── ProfilePictureContentType: string? (100)
```

### Computed Properties
```
Profile.HasCustomProfilePicture: bool (computed, not mapped)
  → ProfilePictureData != null && ProfilePictureData.Length > 0
```

### Storage Approach
Profile pictures are stored as `bytea` in PostgreSQL. This is appropriate for the ~500 member scale of this organization. The dedicated `Picture` endpoint uses EF projection to load only the picture data columns, avoiding loading the full Profile entity.

## Routes

| Route | Method | Description |
|-------|--------|-------------|
| `GET /Profile/Picture/{id}` | GET | Serve profile picture (anonymous, cached 1hr) |
| `GET /Teams/Birthdays` | GET | Birthday calendar (current month) |
| `GET /Teams/Birthdays?month=N` | GET | Birthday calendar for specific month |

## Picture Upload Flow

```
┌─────────────────┐
│  User selects   │
│  file on Edit   │
└──────┬──────────┘
       │
┌──────▼──────────┐
│  Validate:      │
│  - Size ≤ 2MB   │
│  - JPEG/PNG/WebP│
└──────┬──────────┘
       │
┌──────▼──────────┐
│  Read into byte[]│
│  Store in DB     │
└──────┬──────────┘
       │
┌──────▼──────────┐
│  Served via      │
│  /Profile/Picture│
│  with 1hr cache  │
└──────────────────┘
```

## Picture Priority

Custom uploaded picture always takes precedence over Google OAuth avatar. This is implemented via the `EffectiveProfilePictureUrl` computed property on both `ProfileViewModel` and `TeamMemberViewModel`:

```
EffectiveProfilePictureUrl = HasCustomProfilePicture
    ? CustomProfilePictureUrl   (→ /Profile/Picture/{id})
    : ProfilePictureUrl         (→ Google avatar URL)
```

## Privacy Model

| Data | Member | Other Members | Board |
|------|--------|---------------|-------|
| Profile picture | Yes | Yes (in teams) | Yes |
| Date of birth (full) | Yes | No | Yes |
| Birthday (month+day) | Yes | Yes (calendar) | Yes |

## Localization

All UI strings are localized in 5 languages: EN, ES, DE, FR, IT. Keys include:
- `Profile_ProfilePicture`, `Profile_DateOfBirth`
- `Profile_PictureTooLarge`, `Profile_PictureInvalidFormat`
- `ProfileEdit_GoogleAvatar`, `ProfileEdit_PictureHelp`, `ProfileEdit_RemovePicture`
- `ProfileEdit_DateOfBirthHelp`
- `TeamDetail_TeamLeads`
- `Birthdays_Title`, `Birthdays_Count`, `Birthdays_None`, `Birthdays_Privacy`

## Related Features

- [Profiles](02-profiles.md) - Profile entity and edit flow
- [Teams](06-teams.md) - Team detail page and membership
- [Contact Fields](10-contact-fields.md) - Other profile visibility controls
