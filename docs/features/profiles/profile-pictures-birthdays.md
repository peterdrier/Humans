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

Humans need a way to personalize their profiles. Custom profile pictures make team pages more personal and help humans recognize each other. The birthday calendar fosters community by letting humans see upcoming birthdays within the organization. Google OAuth avatars are no longer rendered anywhere in the UI, and the former one-click Google-photo import (US-14.5) was removed in peterdrier/Humans#745.

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

### US-14.5: Import Google Photo as Profile Picture — REMOVED

The one-click Google-photo import (`POST /Profile/Me/ImportGooglePhoto`) was removed in peterdrier/Humans#745. Humans upload pictures via US-14.1 only. The orphaned `ProfileEdit_ImportGooglePhoto*` / `Profile_ImportGooglePhoto_*` resx keys remain in the resource files pending cleanup.

## Data Model

### Profile Entity (additions)
```
Profile
├── DateOfBirth: LocalDate? [PersonalData]
├── ProfilePictureData: byte[]? [PersonalData] [Obsolete — unused; pictures live on the file share. Column retained for prod-soak drop, see nobodies-collective#702]
└── ProfilePictureContentType: string? (100)
```

### Computed Properties
```
Profile.HasCustomProfilePicture: bool (computed, not mapped)
  → ProfilePictureContentType is not null
```

### Storage Approach
Profile pictures are stored on the application's filesystem via the shared `IFileStorage` abstraction (rooted at `wwwroot/`, the same Coolify-mounted volume that serves camp images and any future uploads). The key format is `uploads/profile-pictures/{profileId}{.ext}` where `.ext` is derived from the content type (`.jpg`, `.png`, `.webp`, or empty for unknown). The original `ProfilePictureData` bytea column on `Profile` is `[Obsolete]` and unused — it is never written or served; it stays only until a post-prod-soak drop PR per [nobodies-collective#702](https://github.com/nobodies-collective/Humans/issues/702) and `memory/architecture/no-drops-until-prod-verified.md`. Writes go to a temporary sibling and rename into place so readers never see a partial file.

Profile pictures live under `uploads/` but are NOT publicly served — `Program.cs` registers middleware that 404s `/uploads/profile-pictures/*` before `UseStaticFiles` sees it, so reads must go through the controller (and therefore through the GDPR gate below).

The read path lives in `IProfileService.GetProfilePictureAsync` and is the only entry point the `Profile/Picture` endpoint calls — the controller does not touch `IFileStorage` directly. The service:

1. Reads the DB `ProfilePictureContentType` column via a cheap scalar projection. If null (no picture, or the row was anonymized), it returns `null` and the endpoint responds with 404 — even if a stale file still exists on disk.
2. Otherwise reads the filesystem store. A hit is returned immediately.
3. On a filesystem miss it returns `null`; the obsolete DB bytes column is never a serving fallback.

Saves and removals are filesystem-only: `SaveProfileAsync` writes the bytes through `IFileStorage` (deleting the old-extension file first when the content type changed) and sets only the `ProfilePictureContentType` column; removal/anonymization clears the content-type column AND best-effort deletes the filesystem file. If the filesystem delete fails an error is logged so an operator can clean up the stale file out-of-band, but the read-path content-type gate ensures a stale file is never served to clients (GDPR-compliant). The obsolete `ProfilePictureData` column drops in a follow-up PR after prod soak (#702).

## Routes

| Route | Method | Description |
|-------|--------|-------------|
| `GET /Profile/Picture?id={id}` | GET | Serve profile picture (anonymous, cached 1hr) |
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
│  - Size ≤ 20MB  │
│  - JPEG/PNG/WebP│
└──────┬──────────┘
       │
┌──────▼──────────────┐
│  Read into byte[]   │
│  Write to IFile-    │
│  Storage (FS only); │
│  set content-type   │
│  column             │
└──────┬──────────────┘
       │
┌──────▼──────────────┐
│  Served via         │
│  /Profile/Picture   │
│  (content-type gate │
│   → FS read,        │
│   1hr client cache) │
└─────────────────────┘
```

## Picture Priority

Only the custom uploaded picture is rendered in the UI. Google OAuth avatar URLs are never displayed directly — humans without a custom picture get the initial-letter placeholder. (The former one-click import that consumed the captured Google avatar URL was removed in peterdrier/Humans#745.)

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

The `ProfileEdit_ImportGooglePhoto*` / `Profile_ImportGooglePhoto_*` keys still present in the resx files belong to the removed US-14.5 import flow and are orphaned (no code references them).

## Related Features

- [Profiles](../profiles/profiles.md) - Profile entity and edit flow
- [Teams](../teams/teams.md) - Team detail page and membership
- [Contact Fields](../profiles/contact-fields.md) - Other profile visibility controls
