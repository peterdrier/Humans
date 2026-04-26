<!-- freshness:triggers
  src/Humans.Application/Services/Profile/**
  src/Humans.Domain/Entities/Profile.cs
  src/Humans.Domain/Entities/ContactField.cs
  src/Humans.Domain/Entities/UserEmail.cs
  src/Humans.Domain/Entities/CommunicationPreference.cs
  src/Humans.Domain/Entities/VolunteerHistoryEntry.cs
  src/Humans.Domain/Entities/AccountMergeRequest.cs
  src/Humans.Infrastructure/Data/Configurations/Profiles/**
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Controllers/ProfileApiController.cs
  src/Humans.Web/Controllers/ContactsController.cs
  src/Humans.Web/Controllers/AdminDuplicateAccountsController.cs
  src/Humans.Web/Controllers/AdminMergeController.cs
  src/Humans.Web/Views/Profile/**
-->
<!-- freshness:flag-on-change
  Profile data model, contact-field visibility tiers, FullProfile caching/invalidation, GDPR contributor wiring, and merge/duplicate flows — review when Profile services/entities/controllers/views change.
-->

# Profiles — Section Invariants

Per-human personal data: profile, contact fields, emails, communication preferences. The reference implementation for the §15 caching architecture.

## Concepts

- A **Profile** holds a human's personal information: name, city, country, birthday (month and day only — never year), profile picture, and admin notes.
- **Contact Fields** are per-field contact details (phone, Signal, Telegram, WhatsApp, Discord, custom) with per-field visibility controls.
- **Visibility Levels** determine who can see each contact field: BoardOnly (most restrictive), CoordinatorsAndBoard, MyTeams (shared team members), or AllActiveProfiles (least restrictive).
- **Membership Tier** is tracked on the profile: Volunteer (default), Colaborador, or Asociado.
- **Communication Preferences** control per-category email opt-in/opt-out and per-category in-app inbox visibility. The active categories are System, CampaignCodes, FacilitatedMessages, Ticketing, VolunteerUpdates, TeamUpdates, Governance, and Marketing (see the `MessageCategory` table for defaults). System and CampaignCodes are always on.
- **UserEmail** is a per-user email address record. A user has one "login" email plus zero-or-more verified additional addresses; one of them may be flagged as the notification target.
- **CV Entries** (sub-aggregate of Profile, table `volunteer_history_entries`) record volunteer involvement history.
- **Profile Languages** (sub-aggregate of Profile, table `profile_languages`) record self-assessed proficiency in ISO 639-1 language codes.
- **Duplicate Account Detection** scans for email addresses appearing on multiple accounts (across `User.Email` and `UserEmail.Email`, with gmail/googlemail equivalence). Admin can resolve by archiving the duplicate and re-linking its logins to the real account.
- **Account Merge** consolidates two accounts into one, transferring all associated data (emails, contact fields, CV entries, role assignments, memberships) to the surviving account.

## Data Model

### User (Identity extension)

User is owned by the **Users/Identity** section; the properties below are the profile-adjacent extensions that Profile consumers read most often. Field-level ownership still belongs here because Profile's `CachingProfileService` stitches them into `FullProfile`.

#### Google email preference

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| GoogleEmail | string? (256) | null | Preferred email for Google services (Groups, Drive). Auto-set to @nobodies.team when provisioned/linked. Falls back to OAuth email when null. |

Methods:
- `GetGoogleServiceEmail()` → `GoogleEmail ?? Email` (for Google resource sync)
- `GetEffectiveEmail()` → notification target email or OAuth email (for system notifications)

#### Contact-import properties

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| ContactSource | ContactSource? | null | Where imported from (Manual, MailerLite, TicketTailor); null for self-registered users |
| ExternalSourceId | string?(256) | null | ID in the external source system |

A contact is identified by `ContactSource != null && LastLoginAt == null`. When a contact authenticates, `LastLoginAt` is set and they become a regular user.

#### Campaign-related properties

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| UnsubscribedFromCampaigns | bool | false | Set via `/Unsubscribe/{token}`; excludes user from future campaign sends |

### Profile

**Table:** `profiles`

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| Id | Guid | new | PK |
| UserId | Guid | — | FK → User (Users/Identity) — **FK only**, no nav. Unique. |
| BurnerName | string (256) | "" | Required. Primary display name visible to everyone (burner name / nickname). |
| FirstName | string (256) | "" | Required. Legal first name (private — self + Board). |
| LastName | string (256) | "" | Required. Legal last name (private — self + Board). |
| City | string? (256) | null | Member's city. |
| CountryCode | string? (2) | null | ISO 3166-1 alpha-2. |
| Latitude | double? | null | Place coordinate. |
| Longitude | double? | null | Place coordinate. |
| PlaceId | string? (512) | null | Google Places ID. |
| Bio | string? (4000) | null | Optional biography. |
| Pronouns | string? (100) | null | e.g., "they/them". |
| DateOfBirth | LocalDate? | null | Stored as `LocalDate` but only month + day are meaningful — the year component is hard-coded to `4` by `ProfileService` so the entire field can use Postgres `date` storage without leaking a year. UI labels it "birthday". |
| EmergencyContactName | string? (256) | null | Private — self + Board. |
| EmergencyContactPhone | string? (50) | null | Private — self + Board. |
| EmergencyContactRelationship | string? (100) | null | Private — self + Board. |
| ProfilePictureData | byte[]? | null | Custom uploaded picture, resized to long-side 1000px JPEG by `ProfilePictureProcessor`. |
| ProfilePictureContentType | string? (100) | null | MIME type of the stored picture. |
| ContributionInterests | string? | null | Skills / availability statement (publicly visible on profile). |
| BoardNotes | string? | null | Notes from the human intended for the Board (self + Board only). |
| AdminNotes | string? (4000) | null | Admin-only notes (not visible to the human). |
| IsSuspended | bool | false | Manually suspended; hides profile from regular humans. |
| IsApproved | bool | false | Set automatically when consent check is cleared. |
| MembershipTier | MembershipTier | Volunteer | Current tier — tracked on Profile, not as RoleAssignment. |
| ConsentCheckStatus | ConsentCheckStatus? | null | Consent check gate status (null until all consents signed). |
| ConsentCheckAt | Instant? | null | When consent check was performed. |
| ConsentCheckedByUserId | Guid? | null | Consent Coordinator who performed the check. |
| ConsentCheckNotes | string? (4000) | null | Notes from the Consent Coordinator. |
| RejectionReason | string? (4000) | null | Reason for rejection (when Admin rejects a flagged check). |
| RejectedAt | Instant? | null | When the profile was rejected. |
| RejectedByUserId | Guid? | null | Admin who rejected the profile. |
| NoPriorBurnExperience | bool | false | When true, CV entries are not required during onboarding. |
| CreatedAt | Instant | — | Set on insert. |
| UpdatedAt | Instant | — | Maintained by services. |

**Indexes:** unique on `UserId`; non-unique on `ConsentCheckStatus`.

Cross-domain nav `Profile.User` is **stripped** per design-rules §15i. Consumers resolve User data via `IUserService.GetByIdsAsync`. Aggregate-local navs `ContactFields`, `VolunteerHistory`, and `Languages` are kept.

### ContactField

**Table:** `contact_fields`

Contact fields allow humans to share different types of contact information with per-field visibility controls.

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ProfileId | Guid | FK → Profile (Cascade) |
| FieldType | ContactFieldType | Stored as string (max 50) |
| CustomLabel | string? (100) | Required when `FieldType == Other` |
| Value | string (500) | Required |
| Visibility | ContactFieldVisibility | Stored as string (max 50) |
| DisplayOrder | int | Sort order |
| CreatedAt / UpdatedAt | Instant | Maintained by `ContactFieldService` |

**Indexes:** `ProfileId`; composite `(ProfileId, Visibility)`.

#### Field types (`ContactFieldType`)

| Value | Description |
|-------|-------------|
| ~~Email~~ | **Deprecated** — use `UserEmail` instead. Kept for backward compatibility. |
| Phone | Phone number |
| Signal | Signal messenger |
| Telegram | Telegram messenger |
| WhatsApp | WhatsApp messenger |
| Discord | Discord username |
| Other | Custom type (requires `CustomLabel`) |

#### Visibility levels (`ContactFieldVisibility`)

Lower values are more restrictive. A viewer with access level X can see fields with visibility >= X.

| Value | Level | Who Can See |
|-------|-------|-------------|
| BoardOnly | 0 | Board members only |
| CoordinatorsAndBoard | 1 | Team coordinators and Board |
| MyTeams | 2 | Members who share a team with the owner |
| AllActiveProfiles | 3 | All active members |

#### Access-level resolution

1. **Self** → BoardOnly (sees everything)
2. **Board member** → BoardOnly (sees everything)
3. **Any coordinator** → CoordinatorsAndBoard
4. **Shares team with owner** → MyTeams
5. **Other active member** → AllActiveProfiles only

### UserEmail

**Table:** `user_emails`

Per-user email addresses (login, verified, notifications). Cross-domain nav `UserEmail.User` is **stripped** per §15i.

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → User (Cascade) — **FK only**, no nav |
| Email | string (256) | Required |
| IsVerified | bool | Required |
| IsOAuth | bool | True for the OAuth login email; cannot be deleted |
| IsNotificationTarget | bool | Exactly one verified email per user is the system-notification target |
| Visibility | ContactFieldVisibility? | Stored as string (max 50); null hides the email from profile view |
| VerificationSentAt | Instant? | Last time a verification email was sent (rate limiting) |
| DisplayOrder | int | Sort order in the UI |
| CreatedAt / UpdatedAt | Instant | Maintained by `UserEmailService` |

**Indexes:** `UserId`; **unique partial index** on `Email` filtered to `IsVerified = true` (Postgres `"IsVerified" = true`) — prevents email squatting across accounts.

### CommunicationPreference

**Table:** `communication_preferences`

Per-user, per-category email opt-in/opt-out preferences. One row per user per category. Used for CAN-SPAM/GDPR compliance.

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → User (Cascade) — **FK only**, no nav |
| Category | MessageCategory | Enum stored as string (max 50) |
| OptedOut | bool | true = user opted out of email for this category |
| InboxEnabled | bool | Default true; when false, informational in-app notifications for this category are suppressed (actionable notifications always show) |
| UpdatedAt | Instant | Last change |
| UpdateSource | string (100) | "Profile", "MagicLink", "OneClick", "Default", "DataMigration" |

**Unique constraint:** `(UserId, Category)`. **Indexes:** `UserId`.

Defaults are created lazily by `CommunicationPreferenceService` on first read. All active categories default to opted-in (`OptedOut = false`) except Marketing, which defaults to opted-out. System and CampaignCodes are always on (cannot be opted out).

### VolunteerHistoryEntry (CV Entry)

**Table:** `volunteer_history_entries`

Sub-aggregate of Profile — no separate service. Written through `IProfileService.SaveCVEntriesAsync`; read via `FullProfile.CVEntries`.

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ProfileId | Guid | FK → Profile (Cascade) |
| Date | LocalDate | Required; users may enter a full date or first-of-month. Displayed as e.g. `Mar'25`. |
| EventName | string (256) | Required |
| Description | string? (2000) | Optional |
| CreatedAt / UpdatedAt | Instant | Maintained by `ProfileService` |

**Indexes:** `ProfileId`.

### ProfileLanguage

**Table:** `profile_languages`

Sub-aggregate of Profile — no separate service. Records languages spoken with self-assessed proficiency.

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ProfileId | Guid | FK → Profile (Cascade) |
| LanguageCode | string (10) | ISO 639-1 two-letter code (e.g., `en`, `es`, `de`) |
| Proficiency | LanguageProficiency | Stored as string (max 50) |

**Indexes:** `ProfileId`.

### AccountMergeRequest

Tracks pending and resolved merges between duplicate accounts. `AccountMergeService` orchestrates the merge; `DuplicateAccountService` is the stateless detector that flags candidates.

**Table:** `account_merge_requests`

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK |
| TargetUserId | Guid | FK → User (Cascade) — receives the merged data |
| SourceUserId | Guid | FK → User (Cascade) — gets archived |
| Email | string (256) | The address that triggered the request |
| PendingEmailId | Guid | The unverified `UserEmail` row on the target account |
| Status | AccountMergeRequestStatus | Stored as string (max 50) |
| CreatedAt | Instant | When created |
| ResolvedAt | Instant? | When accepted or rejected |
| ResolvedByUserId | Guid? | FK → User (SetNull) — admin who resolved |
| AdminNotes | string? (4000) | Admin notes |

**Indexes:** `Status`, `TargetUserId`, `SourceUserId`.

The entity still carries `TargetUser`, `SourceUser`, and `ResolvedByUser` navigation properties (configured with `HasOne(...).WithMany().HasForeignKey(...)`). They predate the §15i nav-strip work; the merge admin views read them directly today. Strip and route through `IUserService.GetByIdsAsync` when this pattern is generalised across the section.

### MembershipTier

| Value | Int | Description |
|-------|-----|-------------|
| Volunteer | 0 | Default tier, no application needed |
| Colaborador | 1 | Active contributor, requires application + Board vote, 2-year term |
| Asociado | 2 | Voting member with governance rights, requires application + Board vote, 2-year term |

Stored as string via `HasConversion<string>()`.

### ConsentCheckStatus

| Value | Int | Description |
|-------|-----|-------------|
| Pending | 0 | All required consents signed, awaiting Coordinator review |
| Cleared | 1 | Cleared — triggers auto-approve as Volunteer |
| Flagged | 2 | Safety concern flagged — blocks Volunteer access |

Stored as string via `HasConversion<string>()`. Nullable on Profile (null until all consents signed).

### MessageCategory

| Value | Int | Description |
|-------|-----|-------------|
| System | 0 | Critical account/consent/security notifications. Always on. |
| ~~EventOperations~~ | 1 | **Deprecated** — split into VolunteerUpdates + TeamUpdates. Kept for DB string compatibility. |
| ~~CommunityUpdates~~ | 2 | **Deprecated** — replaced by FacilitatedMessages. Kept for DB string compatibility. |
| Marketing | 3 | Mailing list, promotions. Default: off. |
| Governance | 4 | Board voting, tier applications, role assignments, onboarding reviews. Default: on. |
| CampaignCodes | 5 | Discount codes, grants, campaign redemption codes. Always on. |
| FacilitatedMessages | 6 | User-to-user emails relayed via Humans. Default: on. |
| Ticketing | 7 | Purchase confirmations, event info. Default: on. Locked on when user has a matched ticket order. |
| VolunteerUpdates | 8 | Shift changes, schedule updates, volunteer notifications. Default: on. |
| TeamUpdates | 9 | Drive permissions, team member adds/removes. Default: on. |

Stored as string via `HasConversion<string>()`. `IsAlwaysOn()` covers System and CampaignCodes; `MessageCategoryExtensions.ActiveCategories` is the display-order list shown in the UI (deprecated values omitted).

## Routing

All profile-related functionality lives under `/Profile`:

| Route | Purpose |
|-------|---------|
| `/Profile/Me` | View own profile |
| `/Profile/Me/Edit` | Edit own profile |
| `/Profile/Me/Emails` | Email management |
| `/Profile/Me/ShiftInfo` | Shift preferences |
| `/Profile/Me/CommunicationPreferences` | Per-category email/in-app communication preferences |
| `/Profile/Me/Notifications` | Permanent redirect to `/Profile/Me/CommunicationPreferences` |
| `/Profile/Me/Privacy` | Privacy / deletion |
| `/Profile/Me/DownloadData` | GDPR Article 15 JSON export download |
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

External contacts (pre-provisioned Identity users imported from MailerLite/TicketTailor/etc.) are managed separately at `/Contacts` (`ContactsController`, `PolicyNames.HumanAdminBoardOrAdmin`).

Admin-only flows for the section's cross-account hygiene:

| Route | Purpose |
|-------|---------|
| `/Admin/MergeRequests` | List pending `AccountMergeRequest`s (`AdminMergeController`) |
| `/Admin/MergeRequests/{id}` | Detail view of a single merge request |
| `/Admin/MergeRequests/{id}/Accept` | Accept and execute the merge |
| `/Admin/MergeRequests/{id}/Reject` | Reject the merge |
| `/Admin/DuplicateAccounts` | List detected duplicate-account groups (`AdminDuplicateAccountsController`) |
| `/Admin/DuplicateAccounts/Detail` | Side-by-side comparison of two candidate accounts |
| `/Admin/DuplicateAccounts/Resolve` | Archive the duplicate and re-link logins to the survivor |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View and edit own profile, manage own emails, manage own contact fields, upload profile picture, set notification and communication preferences, request data export (GDPR Article 15), request account deletion |
| Any active human | View other active humans' profiles (contact fields restricted by per-field visibility). Send facilitated messages to other humans. Search for humans |
| HumanAdmin, Board, Admin | View any profile with full detail. Manage humans via admin pages (suspend, unsuspend, approve volunteer, reject signup, view audit log, add or end role assignments). (Membership tier changes go through tier applications in Governance, not the profile admin page.) |
| Admin | Review duplicate-account candidates at `/Admin/DuplicateAccounts` and approve/reject `AccountMergeRequest`s at `/Admin/MergeRequests` (both `PolicyNames.AdminOnly`). |
| Admin (non-production only) | Purge a human and all associated data |

## Invariants

- Every authenticated human can edit their own profile regardless of membership status (available during onboarding).
- Contact field visibility is enforced per-field: a human viewing their own profile sees everything. Board members see everything. Coordinators see CoordinatorsAndBoard-level and below. Shared-team members see MyTeams-level and below. Other active members see only AllActiveProfiles fields.
- Birthday stores month and day only — never year. UI text uses "birthday", not "date of birth".
- Membership tier (Volunteer, Colaborador, Asociado) is tracked on the profile, not as a role assignment.
- Consent check status on the profile gates Volunteer activation: unset until all consents are signed, then Pending, Cleared, or Flagged.
- Profile deletion request sets `User.DeletionRequestedAt` and `User.DeletionScheduledFor = now + 30 days` on the User record. Team memberships and governance role assignments are revoked immediately. Actual data purge is deferred to a background job.
- Data export returns all personal data as a JSON download (GDPR Article 15). `ProfileService` and `AccountMergeService` are this section's `IUserDataContributor` implementations per design-rules §8a; the orchestration lives in `GdprExportService`.
- Profile pictures are stored in the database on `Profile.ProfilePictureData` (~500-user scale). Uploaded images are validated against an allowed-content-type set (JPEG, PNG, WebP, HEIC/HEIF, AVIF) and a 20 MB upload cap, then resized by `ProfilePictureProcessor` to a long-side of 1000 px and re-encoded as JPEG before persistence.
- `CachingProfileService` (Singleton) and `IFullProfileInvalidator` must resolve to the **same** instance — both registrations point to the single decorator. Two instances would split the `ConcurrentDictionary<Guid, FullProfile>` cache and silently lose invalidations.
- Purging a human permanently deletes the account and all associated data, including severing the OAuth link so the next Google login creates a fresh account. Purge is disabled in production environments. No one can purge their own account.
- Duplicate account detection applies gmail/googlemail equivalence when scanning for address collisions.
- `AccountMergeService` writes and `DuplicateAccountService` reads go through the Profile section's repositories and `IUserService` — never through cross-section `DbSet` reads.

## Negative Access Rules

- Regular humans **cannot** view suspended profiles.
- Regular humans **cannot** edit another human's profile.
- Regular humans **cannot** see contact fields above their access level on other humans' profiles.
- Non-active humans (still onboarding) **cannot** view other humans' profiles or send messages.
- Any Admin **cannot** purge their own account.
- Purge **cannot** run in production environments (gate on `IWebHostEnvironment`).

## Triggers

- When all required legal documents are consented to, consent check status transitions to Pending.
- When consent check status is Cleared, the human is auto-approved as a Volunteer and added to the Volunteers system team.
- When a human requests account deletion, team memberships and governance roles are revoked immediately and `User.DeletionScheduledFor` is set to `now + 30 days`. `ProcessAccountDeletionsJob` runs the actual anonymisation via `IUserService.AnonymizeExpiredAccountAsync` after the scheduled date passes.
- When a human verifies a pending email that already exists as a verified address on another account, `UserEmailService` creates an `AccountMergeRequest` (status `Pending`) for admin review.
- When an `AccountMergeRequest` is accepted, all rows from the source user (emails, contact fields, CV entries, role assignments, memberships) are reassigned to the target user via the Profile section's owning services; the source user is archived.
- When `DuplicateAccountService` flags a candidate, an audit entry is written via `IAuditLogService`.
- When a profile field changes through any owning service, `CachingProfileService` reloads the affected `FullProfile` dict entry from the section's repositories.

## Cross-Section Dependencies

- **Legal & Consent:** `IConsentService` — consent-check status gating depends on all required document versions having active consent records.
- **Teams:** `ITeamService` — active membership equals membership in the Volunteers system team. Profile activation triggers addition.
- **Onboarding:** `IOnboardingEligibilityQuery.SetConsentCheckPendingIfEligibleAsync` — Profile calls back into Onboarding to trigger the consent-check gate, using a narrow interface to avoid a DI cycle with `IOnboardingService`.
- **Google Integration:** `IGoogleWorkspaceUserService` / `IGoogleSyncService` — a human's Google service email determines which email is used for Google Groups and Drive sync.
- **Users/Identity:** `IUserService.GetByIdsAsync` — display data for cross-domain nav stitching.

## Architecture

**Owning services:** `ProfileService`, `ContactFieldService`, `ContactService`, `UserEmailService`, `CommunicationPreferenceService`, `AccountMergeService`, `DuplicateAccountService`
**Owned tables:** `profiles`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries`, `profile_languages`, `account_merge_requests`
**Status:** (A) Migrated — canonical §15 reference implementation (peterdrier/Humans PR #235, 2026-04-20). `AccountMergeService` / `DuplicateAccountService` moved into `Humans.Application/Services/Profile/` after the original migration (they now live alongside the other Profile-section services in the code tree; design-rules §8 ownership updated accordingly).

- Services live in `Humans.Application.Services.Profile/` and never import `Microsoft.EntityFrameworkCore`.
- `IProfileRepository`, `IUserEmailRepository`, `IContactFieldRepository`, `ICommunicationPreferenceRepository` (impls in `Humans.Infrastructure/Repositories/`) are the only code paths that touch this section's tables via `DbContext`. Repositories are Singleton, using `IDbContextFactory<HumansDbContext>` and short-lived contexts per method.
- **Decorator decision — caching decorator.** `CachingProfileService` is a Singleton owning `ConcurrentDictionary<Guid, FullProfile> _byUserId`. Warmup via `FullProfileWarmupHostedService`. See design-rules §15d.
- **Inner service** is `Humans.Application.Services.Profile.ProfileService`, registered as `AddKeyedScoped` under `CachingProfileService.InnerServiceKey` (`"profile-inner"`). The decorator resolves it per-call via `IServiceScopeFactory`.
- **`IFullProfileInvalidator`** is aliased to the same Singleton `CachingProfileService` instance so external sections' writes (Auth, Onboarding, Teams, Google) can invalidate the cache without touching the dict.
- **Cross-domain navs stripped:** `Profile.User`, `UserEmail.User`, `CommunicationPreference.User`. Display stitching routes through `IUserService.GetByIdsAsync`.
- **GDPR:** `ProfileService` and `AccountMergeService` both implement `IUserDataContributor` (design-rules §8a). `ProfileService` emits the `Profile`, `ContactFields`, `UserEmails`, `VolunteerHistory`, `Languages`, and `CommunicationPreferences` slices; `AccountMergeService` emits the `AccountMergeRequests` slice. Section keys are constants on `GdprExportSections`. The `ExpectedContributorTypes` in `GdprExportDependencyInjectionTests` enforces registration.
- **Account merge & duplicates** — `AccountMergeService` and `DuplicateAccountService` live in `Humans.Application.Services.Profile/`. `AccountMergeService` is backed by `IAccountMergeRepository` (Singleton) for `account_merge_requests` and orchestrates the actual merge via `IUserEmailService`, `IContactFieldService`, `IProfileService`, and `IUserService`. `DuplicateAccountService` is stateless — no repository, just cross-section reads via those same interfaces. Neither service reads `DbContext` directly.
- **Architecture tests** — `tests/Humans.Application.Tests/Architecture/ProfileArchitectureTests.cs` + `GdprExportDependencyInjectionTests.cs`.

### Account deletion cascade

Account deletion cascades (user-requested / admin-initiated / expiry-triggered) are orchestrated by `IAccountDeletionService` (lives in the Users section, `src/Humans.Application/Services/Users/AccountLifecycle/`). `ProfileService` keeps only own-data mutations (`AnonymizeExpiredProfileAsync`, delegation from `RequestDeletionAsync` to the orchestrator) — the cascade that revokes team memberships, role assignments, and cancels shift signups lives in the orchestrator (peterdrier/Humans#314, nobodies-collective/Humans#582). This preserves the rule that foundational services (`UserService`, `ProfileService`) have no outbound edges to higher-level sections (Teams, RoleAssignments, Shifts).

### Touch-and-clean guidance

- `SetConsentCheckPendingIfEligibleAsync` does not currently invalidate the `FullProfile` dict (§15i). Pre-existing behavior; to be addressed when Shifts migrates (§15 NEW-B).
- Cross-section reads for `Profile.User` / `UserEmail.User` / `CommunicationPreference.User` must go through `IUserService.GetByIdsAsync` — do not re-add nav properties to the entities.
