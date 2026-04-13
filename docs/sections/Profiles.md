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

Old `/Human/*` routes redirect permanently to their `/Profile/*` equivalents.

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

See `.claude/DESIGN_RULES.md` for the full rules.

**Owning services:** `ProfileService`, `ContactFieldService`, `UserEmailService`, `CommunicationPreferenceService`, `VolunteerHistoryService`
**Owned tables:** `profiles`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries`

### Current Violations

**ProfileController — injects HumansDbContext (Rule 1):**
- Directly queries/modifies `ProfileLanguages` in Edit()
- Queries `UserEmails` in SetGoogleServiceEmail()
- Queries `EmailOutboxMessages` in MyOutbox() and AdminDetail()
- Queries `TeamMembers` in Popover()
- Queries `CampaignGrants` in AdminDetail()
- Queries and modifies `GoogleSyncOutboxEvents` in EnqueueResyncForUserTeamsAsync()

**ProfileService — queries non-owned tables (Rule 2c):**
- Queries `Applications` table (owned by Governance) in multiple methods
- Queries `CampaignGrants` table (owned by Campaigns)
- Queries `TeamMembers` table (owned by Teams) in GetAdminHumanDetailAsync()
- Queries `RoleAssignments` table (owned by Auth)
- Queries `CommunicationPreferences` table (owned by CommunicationPreferenceService)
- Queries `ShiftSignups` table (owned by Shifts)
- Queries `CampLeads` table (owned by Camps)
- Creates `Application` entities in SaveProfileAsync()

**UserEmailService — queries non-owned tables (Rule 2c):**
- Queries `AccountMergeRequests` in multiple methods
- Queries `Users` table directly

### Target State

- ProfileController delegates all data access to ProfileService and other section services
- ProfileService only queries its owned tables; calls `IApplicationDecisionService` (Governance), `ICampaignService`, `ITeamService`, `IRoleAssignmentService` (Auth), `ICommunicationPreferenceService`, `IShiftManagementService`, `ICampService` for cross-section data
- UserEmailService calls `IAccountMergeService` and `IUserService` instead of querying their tables
