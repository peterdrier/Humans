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
- **Communication Preferences** control per-category email opt-in/opt-out (System, EventOperations, CommunityUpdates, Marketing).
- **UserEmail** is a per-user email address record. A user has one "login" email plus zero-or-more verified additional addresses; one of them may be flagged as the notification target.
- **CV Entries** (sub-aggregate of Profile) record volunteer involvement history.
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
| UserId | Guid | — | FK → User (Users/Identity) — **FK only**, no nav |
| MembershipTier | MembershipTier | Volunteer | Current tier — tracked on Profile, not as RoleAssignment |
| ConsentCheckStatus | ConsentCheckStatus? | null | Consent check gate status (null until all consents signed) |
| ConsentCheckAt | Instant? | null | When consent check was performed |
| ConsentCheckedByUserId | Guid? | null | Consent Coordinator who performed the check |
| ConsentCheckNotes | string? | null | Notes from the Consent Coordinator |
| RejectionReason | string? | null | Reason for rejection (when Admin rejects a flagged check) |
| RejectedAt | Instant? | null | When the profile was rejected |
| RejectedByUserId | Guid? | null | Admin who rejected the profile |

Cross-domain nav `Profile.User` is **stripped** per design-rules §15i. Consumers resolve User data via `IUserService.GetByIdsAsync`.

### ContactField

**Table:** `contact_fields`

Contact fields allow humans to share different types of contact information with per-field visibility controls.

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

### CommunicationPreference

**Table:** `communication_preferences`

Per-user, per-category email opt-in/opt-out preferences. One row per user per category. Used for CAN-SPAM/GDPR compliance.

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → User (Cascade) — **FK only**, no nav |
| Category | MessageCategory | Enum stored as string |
| OptedOut | bool | true = user opted out |
| UpdatedAt | Instant | Last change |
| UpdateSource | string (100) | "Profile", "MagicLink", "OneClick", "Default", "DataMigration" |

**Unique constraint:** `(UserId, Category)`. **Indexes:** `UserId`.

Defaults are created lazily: System=on, EventOperations=on, CommunityUpdates=off, Marketing=off.

### VolunteerHistoryEntry (CV Entry)

**Table:** `volunteer_history_entries`

Sub-aggregate of Profile — no separate service. Written through `IProfileService.SaveCVEntriesAsync`; read via `FullProfile.CVEntries`.

### AccountMergeRequest

Tracks pending and resolved merges between duplicate accounts. `AccountMergeService` orchestrates the merge; `DuplicateAccountService` is the stateless detector that flags candidates.

**Table:** `account_merge_requests`

Cross-domain navs `TargetUser`, `SourceUser`, `ResolvedByUser` → FK-only (`TargetUserId`, `SourceUserId`, `ResolvedByUserId`). User data resolves via `IUserService.GetByIdsAsync`.

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
| EventOperations | 1 | Shift changes, schedule updates, team additions. Default: on. |
| CommunityUpdates | 2 | Community news, facilitated messages. Default: off. |
| Marketing | 3 | Campaign emails, promotions. Default: off. |

Stored as string via `HasConversion<string>()`.

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
|-------|--------------|
| Any authenticated human | View and edit own profile, manage own emails, manage own contact fields, upload profile picture, set notification and communication preferences, request data export (GDPR Article 15), request account deletion |
| Any active human | View other active humans' profiles (contact fields restricted by per-field visibility). Send facilitated messages to other humans. Search for humans |
| HumanAdmin, Board, Admin | View any profile with full detail. Manage humans via admin pages (suspend, unsuspend, update tier, view audit log, manage roles). Review duplicate-account candidates. Approve/resolve `AccountMergeRequest`s |
| Admin (non-production only) | Purge a human and all associated data |

## Invariants

- Every authenticated human can edit their own profile regardless of membership status (available during onboarding).
- Contact field visibility is enforced per-field: a human viewing their own profile sees everything. Board members see everything. Coordinators see CoordinatorsAndBoard-level and below. Shared-team members see MyTeams-level and below. Other active members see only AllActiveProfiles fields.
- Birthday stores month and day only — never year. UI text uses "birthday", not "date of birth".
- Membership tier (Volunteer, Colaborador, Asociado) is tracked on the profile, not as a role assignment.
- Consent check status on the profile gates Volunteer activation: unset until all consents are signed, then Pending, Cleared, or Flagged.
- Profile deletion request sets a flag. Memberships and team memberships are revoked immediately. Actual data purge is deferred to a background job.
- Data export returns all personal data as a JSON download (GDPR compliance). The service implements `IUserDataContributor` per design-rules §8a.
- Profile pictures are stored on disk. Uploaded images are validated for allowed types and size.
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
- When a human requests account deletion, team memberships are revoked immediately. The actual data purge runs in a background job.
- When an `AccountMergeRequest` is accepted, all rows from the source user (emails, contact fields, CV entries, role assignments, memberships) are reassigned to the target user via the Profile section's owning services; the source user is archived.
- When `DuplicateAccountService` flags a candidate, an audit entry is written via `IAuditLogService`.

## Cross-Section Dependencies

- **Legal & Consent:** `IConsentService` — consent-check status gating depends on all required document versions having active consent records.
- **Teams:** `ITeamService` — active membership equals membership in the Volunteers system team. Profile activation triggers addition.
- **Onboarding:** `IOnboardingEligibilityQuery.SetConsentCheckPendingIfEligibleAsync` — Profile calls back into Onboarding to trigger the consent-check gate, using a narrow interface to avoid a DI cycle with `IOnboardingService`.
- **Google Integration:** `IGoogleWorkspaceUserService` / `IGoogleSyncService` — a human's Google service email determines which email is used for Google Groups and Drive sync.
- **Users/Identity:** `IUserService.GetByIdsAsync` — display data for cross-domain nav stitching.

## Architecture

**Owning services:** `ProfileService`, `ContactFieldService`, `ContactService`, `UserEmailService`, `CommunicationPreferenceService`, `AccountMergeService`, `DuplicateAccountService`
**Owned tables:** `profiles`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries`, `account_merge_requests`
**Status:** (A) Migrated — canonical §15 reference implementation (peterdrier/Humans PR #235, 2026-04-20). `AccountMergeService` / `DuplicateAccountService` moved into `Humans.Application/Services/Profile/` after the original migration (they now live alongside the other Profile-section services in the code tree; design-rules §8 ownership updated accordingly).

- Services live in `Humans.Application.Services.Profile/` and never import `Microsoft.EntityFrameworkCore`.
- `IProfileRepository`, `IUserEmailRepository`, `IContactFieldRepository`, `ICommunicationPreferenceRepository` (impls in `Humans.Infrastructure/Repositories/`) are the only code paths that touch this section's tables via `DbContext`. Repositories are Singleton, using `IDbContextFactory<HumansDbContext>` and short-lived contexts per method.
- **Decorator decision — caching decorator.** `CachingProfileService` is a Singleton owning `ConcurrentDictionary<Guid, FullProfile> _byUserId`. Warmup via `FullProfileWarmupHostedService`. See design-rules §15d.
- **Inner service** is `Humans.Application.Services.Profile.ProfileService`, registered as `AddKeyedScoped` under `CachingProfileService.InnerServiceKey` (`"profile-inner"`). The decorator resolves it per-call via `IServiceScopeFactory`.
- **`IFullProfileInvalidator`** is aliased to the same Singleton `CachingProfileService` instance so external sections' writes (Auth, Onboarding, Teams, Google) can invalidate the cache without touching the dict.
- **Cross-domain navs stripped:** `Profile.User`, `UserEmail.User`, `CommunicationPreference.User`. Display stitching routes through `IUserService.GetByIdsAsync`.
- **GDPR:** `ProfileService` implements `IUserDataContributor` (design-rules §8a). The `ExpectedContributorTypes` in `GdprExportDependencyInjectionTests` enforces registration.
- **Account merge & duplicates** — `AccountMergeService` and `DuplicateAccountService` live in `Humans.Application.Services.Profile/`. `AccountMergeService` is backed by `IAccountMergeRepository` (Singleton) for `account_merge_requests` and orchestrates the actual merge via `IUserEmailService`, `IContactFieldService`, `IProfileService`, and `IUserService`. `DuplicateAccountService` is stateless — no repository, just cross-section reads via those same interfaces. Neither service reads `DbContext` directly.
- **Architecture tests** — `tests/Humans.Application.Tests/Architecture/ProfileArchitectureTests.cs` + `GdprExportDependencyInjectionTests.cs`.

### Touch-and-clean guidance

- `OnboardingService.PurgeHumanAsync` / `SetConsentCheckPendingIfEligibleAsync` do not currently invalidate the `FullProfile` dict (§15g, §15i). Pre-existing behavior; to be addressed when Shifts migrates (§15 NEW-B).
- Cross-section reads for `Profile.User` / `UserEmail.User` / `CommunicationPreference.User` must go through `IUserService.GetByIdsAsync` — do not re-add nav properties to the entities.
