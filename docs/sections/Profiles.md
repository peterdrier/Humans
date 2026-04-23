# Profiles — Section Invariants

## Concepts

- A **Profile** holds a human's personal information: name, city, country, birthday (month and day only — never year), profile picture, and admin notes.
- **Contact Fields** are per-field contact details (phone, Signal, Telegram, WhatsApp, Discord, custom) with per-field visibility controls.
- **Visibility Levels** determine who can see each contact field: BoardOnly (most restrictive), CoordinatorsAndBoard, MyTeams (shared team members), or AllActiveProfiles (least restrictive).
- **Membership Tier** is tracked on the profile: Volunteer (default), Colaborador, or Asociado.
- **Communication Preferences** control per-category email opt-in/opt-out (System, EventOperations, CommunityUpdates, Marketing).

## Routing

All profile-related functionality lives under `/Profile`:

| Route | Purpose |
|-------|---------|
| `/Profile/Me` | View own profile |
| `/Profile/Me/Edit` | Edit own profile |
| `/Profile/Me/Emails` | Email management |
| `/Profile/Me/ShiftInfo` | Shift preferences |
| `/Profile/Me/Notifications` | Communication preferences |
| `/Profile/Me/Privacy` | Privacy / deletion |
| `/Profile/Me/Outbox` | Own email outbox |
| `/Profile/{id}` | View another human's profile |
| `/Profile/{id}/Popover` | Quick profile popup |
| `/Profile/{id}/SendMessage` | Send facilitated message |
| `/Profile/{id}/Admin` | Admin detail view |
| `/Profile/{id}/Admin/Outbox` | Admin view of person's outbox |
| `/Profile/{id}/Admin/Suspend` | Suspend member |
| `/Profile/{id}/Admin/Approve` | Approve volunteer |
| `/Profile/{id}/Admin/Reject` | Reject signup |
| `/Profile/{id}/Admin/Roles/*` | Role management |
| `/Profile/Admin` | Admin list of all humans |
| `/Profile/Search` | People search |
| `/Profile/Picture` | Profile picture endpoint |
| `/api/profiles/search` | API search endpoint |

External contacts are managed separately at `/Contacts` (ContactsController).

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any authenticated human | View and edit own profile, manage own emails, manage own contact fields, upload profile picture, set notification and communication preferences, request data export (GDPR Article 15), request account deletion |
| Any active human | View other active humans' profiles (contact fields restricted by per-field visibility). Send facilitated messages to other humans. Search for humans |
| HumanAdmin, Board, Admin | View any profile with full detail. Manage humans via admin pages (suspend, unsuspend, update tier, view audit log, manage roles) |

## Invariants

- Every authenticated human can edit their own profile regardless of membership status (available during onboarding).
- Contact field visibility is enforced per-field: a human viewing their own profile sees everything. Board members see everything. Coordinators see CoordinatorsAndBoard-level and below. Shared-team members see MyTeams-level and below. Other active members see only AllActiveProfiles fields.
- Birthday stores month and day only — never year. UI text uses "birthday", not "date of birth".
- Membership tier (Volunteer, Colaborador, Asociado) is tracked on the profile, not as a role assignment.
- Consent check status on the profile gates Volunteer activation: unset until all consents are signed, then Pending, Cleared, or Flagged.
- Profile deletion request sets a flag. Memberships and team memberships are revoked immediately. Actual data purge is deferred to a background job.
- Data export returns all personal data as a JSON download (GDPR compliance).
- Profile pictures are stored on disk. Uploaded images are validated for allowed types and size.

## Negative Access Rules

- Regular humans **cannot** view suspended profiles.
- Regular humans **cannot** edit another human's profile.
- Regular humans **cannot** see contact fields above their access level on other humans' profiles.
- Non-active humans (still onboarding) **cannot** view other humans' profiles or send messages.

## Triggers

- When all required legal documents are consented to, consent check status transitions to Pending.
- When consent check status is Cleared, the human is auto-approved as a Volunteer and added to the Volunteers system team.
- When a human requests account deletion, team memberships are revoked immediately. The actual data purge runs in a background job.

## Cross-Section Dependencies

- **Legal & Consent**: Consent check status depends on all required document versions having consent records.
- **Teams**: Active membership equals membership in the Volunteers system team. Profile activation triggers addition.
- **Onboarding**: Profile completion is a prerequisite step in the onboarding pipeline.
- **Google Integration**: A human's Google service email determines which email is used for Google Groups and Drive sync.

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `ProfileService`, `ContactFieldService`, `ContactService`, `UserEmailService`, `CommunicationPreferenceService`, `VolunteerHistoryService`
**Owned tables:** `profiles`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries`

Services live in `Humans.Application/Services/Profile/`. Each has a repository interface in `Humans.Application/Interfaces/Repositories/` with EF implementation in `Humans.Infrastructure/Repositories/` (registered Singleton via `IDbContextFactory`). In-memory caching is provided by the `CachingProfileService` Singleton decorator, which owns a `ConcurrentDictionary<Guid, FullProfile>` keyed by userId. There is no separate store class or warmup hosted service. See `docs/architecture/design-rules.md §15` for the canonical pattern. (`VolunteerHistoryService` no longer exists as a separate service — CV entries are part of `FullProfile` and written through `IProfileService.SaveCVEntriesAsync`.)
