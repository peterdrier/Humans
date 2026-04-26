<!-- freshness:triggers
  src/Humans.Application/Services/Profile/ProfileService.cs
  src/Humans.Application/Services/Teams/TeamService.cs
  src/Humans.Application/Services/Teams/TeamPageService.cs
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Controllers/TeamController.cs
  src/Humans.Web/Views/Profile/Edit.cshtml
  src/Humans.Web/Views/Team/Birthdays.cshtml
  src/Humans.Domain/Entities/Profile.cs
  src/Humans.Infrastructure/Data/Configurations/Profiles/**
-->
<!-- freshness:flag-on-change
  Profile picture upload/serve route, birthday calendar view, and DOB privacy rules — review when Profile entity, ProfileController picture endpoint, or the team birthdays view change.
-->

# Profile Pictures & Birthday Calendar

## Business Context

Humans need a way to personalize their profiles. Custom profile pictures make team pages more personal and help humans recognize each other. The birthday calendar fosters community by letting humans see upcoming birthdays within the organization. Google OAuth avatars are no longer rendered anywhere in the UI — they are only used as a one-time seed when a human chooses to import their Google photo as a custom picture (see US-14.5).

## User Stories

### US-14.1: Upload Profile Picture
**As a** human
**I want to** upload a custom profile picture
**So that** I can personalize my profile

**Acceptance Criteria:**
- Can upload JPEG, PNG, WebP, or HEIC images (max 20MB; large images are auto-resized)
- The custom picture is rendered everywhere a profile picture is shown
- Can remove the custom picture; profile then falls back to the initial-letter placeholder
- Picture is shown on profile page, team detail pages, and birthday calendar
- Picture served via dedicated endpoint with 1-hour cache

### US-14.2: View Team Photo Gallery
**As a** human
**I want to** see profile pictures of all team members on the team detail page
**So that** I can put faces to names

**Acceptance Criteria:**
- Team detail page shows members in a grid layout with profile pictures
- Leads shown first with larger photos (80x80) and primary border
- Regular members shown with standard photos (64x64)
- Placeholder initials shown for members without a custom picture
- Only custom uploaded pictures are rendered (Google avatars are never shown directly)

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
**As a** human
**I want to** see upcoming birthdays by month
**So that** I can celebrate with my teammates

**Acceptance Criteria:**
- Monthly view with navigation between months
- Shows profile picture, display name, day of month, and team memberships
- Only shows humans who have set their birthday
- Accessible from Teams index page
- Privacy note explaining visibility rules
- System teams excluded from team name display

### US-14.5: Import Google Photo as Profile Picture
**As a** human signed in via Google
**I want to** copy my Google account photo as my profile picture with one click
**So that** I don't have to download and re-upload it manually

**Acceptance Criteria:**
- "Import my Google photo" button is shown on the profile edit page only when:
  - The human signed in with Google and an avatar URL was captured at sign-in
  - The human does not already have a custom profile picture
- Clicking the button server-side fetches the Google avatar, resizes it, and stores it as the custom profile picture
- The fetch is restricted to `https://*.googleusercontent.com` (SSRF guard); any other host is refused with an error message
- After import, the photo is treated like any uploaded picture and can be removed/replaced normally
- Errors (network, format, size, untrusted host) surface as friendly TempData error messages and never throw to the user

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
| `POST /Profile/Me/ImportGooglePhoto` | POST | Import the signed-in user's Google avatar as their custom picture (anti-forgery; SSRF-guarded to `*.googleusercontent.com`) |
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

Only the custom uploaded picture is rendered in the UI. Google OAuth avatar URLs are never displayed directly — humans without a custom picture get the initial-letter placeholder. The Google avatar URL is retained on the User entity solely as the source for the optional one-click import flow (US-14.5).

This is implemented via the `EffectiveProfilePictureUrl` computed property on both `ProfileViewModel` and `TeamMemberViewModel`:

```
EffectiveProfilePictureUrl = HasCustomProfilePicture
    ? CustomProfilePictureUrl   (→ /Profile/Picture/{id})
    : null                       (→ initial-letter placeholder)
```

## Privacy Model

| Data | Member | Other Members | Board |
|------|--------|---------------|-------|
| Profile picture | Yes | Yes (in teams) | Yes |
| Date of birth (full) | Yes | No | Yes |
| Birthday (month+day) | Yes | Yes (calendar) | Yes |

## Localization

All UI strings are localized in 6 languages: EN, ES, CA, DE, FR, IT. Keys include:
- `Profile_ProfilePicture`, `Profile_DateOfBirth`
- `Profile_PictureTooLarge`, `Profile_PictureInvalidFormat`
- `ProfileEdit_PictureHelp`, `ProfileEdit_RemovePicture`
- `ProfileEdit_DateOfBirthHelp`
- `TeamDetail_TeamLeads`
- `Birthdays_Title`, `Birthdays_Count`, `Birthdays_None`, `Birthdays_Privacy`

### Import-Google-photo flow (US-14.5)

| Key | Where | Purpose |
|-----|-------|---------|
| `ProfileEdit_ImportGooglePhoto` | Edit page | Button label |
| `ProfileEdit_ImportGooglePhotoHelp` | Edit page | Help text under the button |
| `Profile_ImportGooglePhoto_Success` | TempData | Import succeeded |
| `Profile_ImportGooglePhoto_Unavailable` | TempData | No Google login or no captured avatar |
| `Profile_ImportGooglePhoto_NoProfile` | TempData | Profile not yet provisioned |
| `Profile_ImportGooglePhoto_AlreadyHasCustom` | TempData | Already has a custom picture |
| `Profile_ImportGooglePhoto_FetchFailed` | TempData | Network / HTTP / timeout failure |
| `Profile_ImportGooglePhoto_InvalidFormat` | TempData | Unsupported content type or unprocessable bytes |
| `Profile_ImportGooglePhoto_NotGoogleUrl` | TempData | SSRF guard: stored URL is not on `*.googleusercontent.com` |

## Related Features

- [Profiles](02-profiles.md) - Profile entity and edit flow
- [Teams](06-teams.md) - Team detail page and membership
- [Contact Fields](10-contact-fields.md) - Other profile visibility controls
