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

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `ProfileService`, `ContactFieldService`, `UserEmailService`, `CommunicationPreferenceService`, `VolunteerHistoryService`
**Owned tables:** `profiles`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries`

## Target Architecture Direction

> **Status:** This section currently follows the "services in Infrastructure, direct DbContext" model. It will be migrated to the repository/store/decorator pattern per [`../architecture/design-rules.md`](../architecture/design-rules.md). **Delete this block once the migration lands and this section's services live in `Humans.Application` with `*Repository.cs` impls in `Humans.Infrastructure/Repositories/`.**

### Target repositories

- **`IProfileRepository`** — owns `profiles`
  - Aggregate-local navs kept: `Profile.ContactFields`, `Profile.VolunteerHistory`, `Profile.Languages`
  - Cross-domain navs stripped: `Profile.User → Profile.UserId`
- **`IContactFieldRepository`** — owns `contact_fields`
  - Aggregate-local navs kept: `ContactField.Profile` (back-ref within Profiles section)
  - Cross-domain navs stripped: none
- **`IUserEmailRepository`** — owns `user_emails`
  - Aggregate-local navs kept: none
  - Cross-domain navs stripped: `UserEmail.User → UserEmail.UserId`
- **`ICommunicationPreferenceRepository`** — owns `communication_preferences`
  - Aggregate-local navs kept: none
  - Cross-domain navs stripped: `CommunicationPreference.User → CommunicationPreference.UserId`
- **`IVolunteerHistoryRepository`** — owns `volunteer_history_entries`
  - Aggregate-local navs kept: `VolunteerHistoryEntry.Profile` (back-ref within Profiles section)
  - Cross-domain navs stripped: none

### Current violations

Observed in this section's service code as of 2026-04-15 (reforge `dbset-usage` sweep of `ProfileService`, `ContactFieldService`, `UserEmailService`, `CommunicationPreferenceService`, `VolunteerHistoryService`, `ContactService`, plus grep for `.Include(` and `_cache.`). The 15 cross-section reads and 13 cross-domain `Include` chains that used to live in `ProfileService.ExportDataAsync` were removed in #502 — those reads now come from each owning section through `IUserDataContributor` and `IGdprExportService` (`Humans.Application.Services.Gdpr`). See [`docs/features/gdpr-export.md`](../features/gdpr-export.md).

- **Cross-domain `.Include()` calls:**
  - `ProfileService.cs:67` — `.Include(p => p.User)` in `GetProfileAsync` (navigates to `Users` domain)
  - `ProfileService.cs:100` — `.Include(g => g.Campaign)` in `GetActiveOrCompletedCampaignGrantsAsync` (navigates to `Campaigns` domain)
  - `ProfileService.cs:101` — `.Include(g => g.Code)` in `GetActiveOrCompletedCampaignGrantsAsync` (navigates to `Campaigns` domain)
  - `ProfileService.cs:966` — `.Include(u => u.Profile)` in `GetAdminHumanDetailAsync` (query root is `Users` — the entire query lives in the wrong domain)
  - `ProfileService.cs:967` — `.Include(u => u.Applications)` in `GetAdminHumanDetailAsync` (navigates to `Governance` domain)
  - `ProfileService.cs:968` — `.Include(u => u.UserEmails)` in `GetAdminHumanDetailAsync` (query root is `Users`)
  - `ProfileService.cs:978` — `.Include(ra => ra.CreatedByUser)` in `GetAdminHumanDetailAsync` (query root is `RoleAssignments`/`Auth`)
  - `ProfileService.cs:1070` — `.Include(p => p.User)` in `GetCachedProfilesAsync` (navigates to `Users` domain)
  - `UserEmailService.cs:90` — `.Include(ue => ue.User)` in `AddEmailAsync` (navigates to `Users` domain)
  - `UserEmailService.cs:97` — `.Include(ue => ue.User)` in `AddEmailAsync` (navigates to `Users` domain)
  - `UserEmailService.cs:217` — `.Include(u => u.UserEmails)` in `VerifyEmailAsync` (query root is `Users` domain)
  - `VolunteerHistoryService.cs:97` — `.Include(p => p.User)` in `SaveAsync` (navigates to `Users` domain)
- **Cross-section direct DbContext reads** (reading tables owned by OTHER sections):
  - `ProfileService.cs:87` — `_dbContext.Applications` in `GetProfileAsync` (owned by `Governance`)
  - `ProfileService.cs:114` — `_dbContext.Applications` in `SaveProfileAsync` (owned by `Governance`)
  - `ProfileService.cs:123` — `_dbContext.Applications` in `SaveProfileAsync` (owned by `Governance`)
  - `ProfileService.cs:221` — `_dbContext.Applications` in `SaveProfileAsync` (owned by `Governance`)
  - `ProfileService.cs:232` — `_dbContext.Applications` in `SaveProfileAsync` (owned by `Governance`)
  - `ProfileService.cs:265` — `_dbContext.Applications.Add()` in `SaveProfileAsync` (owned by `Governance`)
  - `ProfileService.cs:271` — `_dbContext.Users.FindAsync()` in `SaveProfileAsync` (owned by `Users`)
  - `ProfileService.cs:301` — `_dbContext.Users.FindAsync()` in `RequestDeletionAsync` (owned by `Users`)
  - `ProfileService.cs:376` — `_dbContext.Users.FindAsync()` in `CancelDeletionAsync` (owned by `Users`)
  - `ProfileService.cs:405` — `_dbContext.EventSettings` in `GetEventHoldDateAsync` (owned by `Shifts`)
  - `ProfileService.cs:98` — `_dbContext.CampaignGrants` in `GetActiveOrCompletedCampaignGrantsAsync` (owned by `Campaigns`)
  - `ProfileService.cs:892` — `_dbContext.Users` in `GetFilteredHumansAsync` (owned by `Users`)
  - `ProfileService.cs:965` — `_dbContext.Users` in `GetAdminHumanDetailAsync` (owned by `Users`)
  - `ProfileService.cs:976` — `_dbContext.RoleAssignments` in `GetAdminHumanDetailAsync` (owned by `Auth`)
  - `ProfileService.cs:986` — `_dbContext.Users` in `GetAdminHumanDetailAsync` (owned by `Users`)
  - `ProfileService.cs:1026` — `_dbContext.Users` in `SearchApprovedUsersAsync` (owned by `Users`)
  - `UserEmailService.cs:48` — `_dbContext.AccountMergeRequests` in `GetUserEmailsAsync` (owned by `Admin`)
  - `UserEmailService.cs:128` — `_dbContext.AccountMergeRequests` in `AddEmailAsync` (owned by `Admin`)
  - `UserEmailService.cs:133` — `_dbContext.AccountMergeRequests` in `AddEmailAsync` (owned by `Admin`)
  - `UserEmailService.cs:244` — `_dbContext.AccountMergeRequests` in `VerifyEmailAsync` (owned by `Admin`)
  - `UserEmailService.cs:262` — `_dbContext.AccountMergeRequests.Add()` in `VerifyEmailAsync` (owned by `Admin`)
  - `UserEmailService.cs:444` — `_dbContext.Users.FindAsync()` in `AddOAuthEmailAsync` (owned by `Users`)
  - `UserEmailService.cs:459` — `_dbContext.Users.FindAsync()` in `AddVerifiedEmailAsync` (owned by `Users`)
  - `ContactService.cs:54` — `_dbContext.Users` in `CreateContactAsync` (owned by `Users`)
  - `ContactService.cs:62` — `_dbContext.Users` in `CreateContactAsync` (owned by `Users`)
  - `ContactService.cs:175` — `_dbContext.Users` in `GetFilteredContactsAsync` (owned by `Users`)
  - `ContactService.cs:216` — `_dbContext.Users` in `GetContactDetailAsync` (owned by `Users`)
- **Within-section cross-service direct DbContext reads** (per §2c each SERVICE owns tables, not each SECTION — these are real violations, though less severe than cross-section):
  - `ProfileService.cs:907` — `_dbContext.UserEmails` in `GetFilteredHumansAsync` (owned by sibling `UserEmailService`)
  - `ProfileService.cs:1006` — `_dbContext.UserEmails` in `GetAdminHumanDetailAsync` (owned by sibling `UserEmailService`)
  - `ProfileService.ContributeForUserAsync` — `_dbContext.{ContactFields,UserEmails,VolunteerHistoryEntries,CommunicationPreferences}` (owned by sibling services within Profiles section). This is scope-deferred per #502: the GDPR fan-out pulls the WHOLE Profiles-section slice through `ProfileService` for now; splitting it into per-service contributors happens when the section migrates to repositories.
  - `ContactFieldService.cs:44` — `_dbContext.Profiles` in `GetContactFieldsAsync` (owned by sibling `ProfileService`)
  - `VolunteerHistoryService.cs:95` — `_dbContext.Profiles` in `SaveAsync` (owned by sibling `ProfileService`)
  - `ContactService.cs:89` — `_dbContext.UserEmails` in `CreateContactAsync` (owned by sibling `UserEmailService` — assuming `ContactService` belongs in Profiles section)
  - `ContactService.cs:96` — `_dbContext.UserEmails` in `CreateContactAsync` (owned by sibling `UserEmailService`)
  - `ContactService.cs:148` — `_dbContext.UserEmails.Add()` in `CreateContactAsync` (owned by sibling `UserEmailService`)
- **Inline `IMemoryCache` usage in service methods:**
  - `ProfileService.cs:62` — `_cache.TryGetExistingValue<Profile>(cacheKey, out var cached)` in `GetProfileAsync`
  - `ProfileService.cs:72` — `_cache.Set(cacheKey, profile, ProfileCacheTtl)` in `GetProfileAsync`
  - `ProfileService.cs:278` — `_cache.InvalidateNavBadgeCounts()` in `SaveProfileAsync`
  - `ProfileService.cs:279` — `_cache.InvalidateNotificationMeters()` in `SaveProfileAsync`
  - `ProfileService.cs:280` — `_cache.InvalidateActiveTeams()` in `SaveProfileAsync`
  - `ProfileService.cs:281` — `_cache.InvalidateUserProfile(userId)` in `SaveProfileAsync`
  - `ProfileService.cs:282` — `_cache.InvalidateRoleAssignmentClaims(userId)` in `SaveProfileAsync`
  - `ProfileService.cs:350` — `_cache.InvalidateUserAccess(userId)` in `RequestDeletionAsync`
  - `ProfileService.cs:351` — `_cache.InvalidateUserProfile(userId)` in `RequestDeletionAsync`
  - `ProfileService.cs:387` — `_cache.InvalidateUserProfile(userId)` in `CancelDeletionAsync`
  - `ProfileService.cs:1064` — `_cache.GetOrCreateAsync(CacheKeys.Profiles, ...)` in `GetCachedProfilesAsync`
  - `ProfileService.cs:1083` — `_cache.TryGetExistingValue<ConcurrentDictionary<Guid, CachedProfile>>(...)` in `GetCachedProfile`
  - `ProfileService.cs:1100` — `_cache.UpdateProfile(userId, newValue)` in `UpdateProfileCache`
- **Cross-domain nav properties on this section's entities:**
  - `Profile.User → Profile.UserId` (`Users` is a separate domain)
  - `UserEmail.User → UserEmail.UserId` (`Users` is a separate domain)
  - `CommunicationPreference.User → CommunicationPreference.UserId` (`Users` is a separate domain)

### Touch-and-clean guidance

Until this section is migrated end-to-end, when touching its code:

- When adding a new user-scoped section (or new user-scoped table in an existing section), make the owning service implement `IUserDataContributor` in `Humans.Application.Interfaces.Gdpr` and wire it through `InfrastructureServiceCollectionExtensions` using the forwarding pattern. Do NOT add new `_dbContext.X` reads to `ProfileService.ContributeForUserAsync` for other sections — the orchestrator picks them up automatically once registered. See `docs/features/gdpr-export.md`.
- When touching `ProfileService.GetAdminHumanDetailAsync` (lines 963–1020) or `GetFilteredHumansAsync` (lines 889+), do not add new `.Include(u => u.Profile / u.Applications / u.UserEmails)` chains on a `_dbContext.Users` root. The query root belongs in `Users` (or should be flipped to a `Profiles`-rooted query that resolves user data via `IUserService.GetByIdsAsync`). Lines 965–968 and 976 are the exact pattern to NOT copy.
- When touching `ProfileService.GetProfileAsync` (line 59+) or `GetCachedProfilesAsync` (line 1062+), resist adding fresh `_cache.*` calls (see the 13 hits at 62, 72, 278–282, 350, 351, 387, 1064, 1083, 1100). All cache logic is slated to move into a `CachingProfileService` decorator per §4–§5 — any new cache needs there become rework.
- When touching `UserEmailService` methods that currently hit `AccountMergeRequests` (lines 48, 128, 133, 244, 262) or `Users` (lines 444, 459), call `IAccountMergeService` / `IUserService` instead. Same rule applies to `ContactService` (lines 54, 62, 89, 96, 148, 175, 216) — route `Users` reads through `IUserService` and `UserEmails` writes through `IUserEmailService`.
