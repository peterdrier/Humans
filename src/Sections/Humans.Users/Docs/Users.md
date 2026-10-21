<!-- freshness:triggers
  src/Sections/Humans.Users/**
  src/Sections/Humans.Users.Contracts/**
  src/Humans.Web/Controllers/AccountController.cs
  src/Sections/Humans.Shifts/Controllers/ShiftProfileController.cs
  src/Humans.Analyzers/UserEmailLegacyFieldAnalyzer.cs
  src/Humans.Analyzers/IdentityFindByEmailAnalyzer.cs
  tests/Humans.Analyzers.Tests/UserEmailLegacyFieldAnalyzerTests.cs
  tests/Humans.Analyzers.Tests/IdentityFindByEmailAnalyzerTests.cs
  tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs
  tests/Humans.Users.Tests/Architecture/UserArchitectureTests.cs
-->
<!-- freshness:flag-on-change
  User entity surface, OAuth-vs-magic-link provisioning, the Identity-framework §2a exception, the Profile data model, contact-field visibility tiers, UserInfo caching/invalidation, GDPR contributor wiring, and merge/duplicate flows — review when Humans.Users services/entities/controllers/views change.
-->

# Users — Section Invariants

`src/Sections/Humans.Users` (+ `.Contracts`) covers the **User/Identity**
aggregate and **Profiles**. They share a project because they share the `UsersDbContext` and
because `CachingUserService` stitches User-owned columns into the unified `UserInfo` read model; the invariants below
are stated separately, because they are separate invariants.

The agent's `fetch_section_guide` tool reaches this file under the canonical key `Users`, with
`Profiles` and `Profile` as aliases (`AgentSectionKeys`).

---

# Part 1 — Users / Identity


The User aggregate and its identity surface. Profile-adjacent User properties (Google email preference, contact import) are documented under [Part 2 — Profiles](#part-2--profiles) because `CachingUserService` stitches them into `UserInfo`; this section owns the entity itself, the identity framework extensions, and cross-event participation state.

## Concepts

- A **User** is the ASP.NET Core Identity aggregate for every human in the system. Authenticates via Google OAuth or magic link. The entity extends `IdentityUser<Guid>`.
- **Account Provisioning** creates new `User` rows from an OAuth login (`ExternalLoginService.CompleteExternalLoginAsync`, dispatched from `AccountController.ExternalLoginCallback`), a magic-link signup (`AccountController.CompleteSignup`), or an import (`AccountProvisioningService.FindOrCreateUserByEmailAsync` for ticket / MailerLite contacts). All three paths look up an existing user across `UserEmail` records and `User.Email` (with gmail/googlemail equivalence) before creating a new row. Magic-link completion validates trimmed burner, first and last names against their 256-character limit before any user/email/profile mutation, including interrupted-signup recovery. The Shell validates before redeeming the token and redisplays field errors with all entered values; the form carries matching maxlength limits.
- **Communication preferences and unsubscribe** moved to Email (`email_communication_preferences`, Users decomposition commit 14): `ICommunicationPreferenceService`, the unsubscribe tokens, `/Email/Unsubscribe/*`, `/Email/Guest/CommunicationPreferences*` and `/Email/CommunicationPreferences*` are Email's. Users keeps the frozen legacy `communication_preferences` table — no longer written — for Email's read fallback and the `email.communication-preferences` copy.
- **Event Participation** moved to Tickets (`tickets_event_participations`, Users decomposition commit 13). Users keeps the frozen legacy `event_participations` table — no longer written — for Tickets' read fallback and the `tickets.event-participations` copy.
- **Account deletion** is a 30-day grace period: `User.DeletionRequestedAt` + `DeletionScheduledFor` are stamped when a user requests deletion (with optional `DeletionEligibleAfter` for ticket-holder event holds). `ProcessAccountDeletionsJob` runs daily and calls `IAccountDeletionService.AnonymizeExpiredAccountAsync` for each due user. That job and `SuspendNonCompliantMembersJob` live in `Humans.Users/Jobs/` and are registered and scheduled by concrete type from Users' own `Section.cs` / `SectionJobs.cs` through the Shell's Hangfire scheduler. Those call sites are inside the section, so `ProcessAccountDeletionsJob` is `internal sealed`; `SuspendNonCompliantMembersJob` is still `public` and has no caller outside Users either.
- Identity sub-tables (renamed to `users`, `user_claims`, `user_logins`, `user_tokens`, `roles`, `user_roles` per Postgres convention in `UsersDbContext.OnModelCreating`) are managed by ASP.NET Identity's `UserManager<User>` / `SignInManager<User>`. Controllers may inject those framework services directly (design-rules §2a exception).

## Data Model

### User

**Table:** `users` (ASP.NET Identity table, renamed from `AspNetUsers` in `UsersDbContext.OnModelCreating`).

Extends `IdentityUser<Guid>` with project-specific columns.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Email | string | Computed from `UserEmails` (first verified, primary-preferred); falls back to `base.Email` when collection is not loaded. Override — not a plain column. |
| DisplayName | string | `[Obsolete]` legacy Identity column (max 256, required). Nothing writes it on create or profile save (new rows hold `""`); left: merge/GDPR tombstone scrubs, the GDPR export, and the legacy tombstone arm below. Column drop is nobodies-collective/Humans#1102. |
| BurnerName | string? | The name we render (max 256), and the sole source for `UserInfo.BurnerName` (nobodies-collective/Humans#1098). Dual-written from `Profile.BurnerName` on every profile save; null on rows not yet backfilled. The only fallback left is narrow legacy-tombstone recognition (blank `BurnerName` + the GDPR sentinel in `DisplayName`), which retires with the #1102 column drop. |
| FirstName | string? | Legal given name (max 256). Dual-written from `Profile.FirstName`; `Profile` is still the read source. |
| LastName | string? | Legal family name (max 256). Dual-written from `Profile.LastName`; `Profile` is still the read source. |
| PreferredLanguage | string | UI / email locale, default `"en"` (max 10) |
| ProfilePictureUrl | string? | Google profile picture URL (max 2048) |
| CreatedAt | Instant | Set on insert; immutable (`init`) |
| LastLoginAt | Instant? | Most recent login timestamp — distinguishes imported contacts (`null`) from active users |
| LastConsentReminderSentAt | Instant? | **Legacy, no longer written** (GDPR erasure clears it). The re-consent reminder cooldown stamp moved to Consent's `consent_reminders`. Carried on `UserInfo.LastConsentReminderSentAt` as `[Obsolete]` (`HUM_USER_CONSENT_REMINDER`) for Consent's read fallback and the `consent.reminder-stamps` copy only; column kept until its drop PR (`Docs/debt.yml` USERS-22). |
| SuppressScheduleChangeEmails | bool | **Dead.** No reader or writer (readers removed in Users decomposition commit 16, except the Art. 15 export); `[Obsolete]`, column kept until its drop PR (`Docs/debt.yml` USERS-13). |
| UnsubscribedFromCampaigns | bool | **Dead.** Email's per-category preferences are the source of truth; no reader or writer (readers removed in commit 16, except the Art. 15 export); `[Obsolete]`, column kept until its drop PR (`Docs/debt.yml` USERS-12). |
| GoogleEmailStatus | LegacyGoogleEmailStatus | **Dead.** Google sync status lives on GoogleIntegration's `google_workspace_identities` (commit 15); no reader or writer; shadow column with no CLR property, kept until its drop PR (`Docs/debt.yml` USERS-14). |
| ContactSource | ContactSource? | Where imported from (`Manual`, `MailerLite`, `TicketTailor`); null for self-registered users. |
| ExternalSourceId | string? (256) | ID in the external source system (e.g., MailerLite subscriber ID). |
| DeletionRequestedAt | Instant? | When the user requested account deletion |
| DeletionScheduledFor | Instant? | `DeletionRequestedAt + 30 days`; the earliest the job will anonymize |
| DeletionEligibleAfter | Instant? | Optional event-hold floor for ticket holders; the job waits past this date too |
| MergedToUserId | Guid? | Self-referential FK → User. Set on this row when `AccountMergeService.AcceptAsync` folds it into the target. Source rows are tombstones — they outlive their target's lifecycle (`OnDelete: Restrict`) so append-only history (audit log, consent records, budget audit log) stays attributable. Filtered index `IX_users_MergedToUserId` (WHERE NOT NULL) backs the chain-follow lookup. |
| MergedAt | Instant? | When the merge tombstone was applied. Null while live. |
| State | UserState | Lifecycle state and the single source of truth for access, suspension included (`Bare`, `Active`, `DeletePending`, `Suspended`, `Rejected`, `Deleted`, `Merged`, `AdminSuspended`). Required string column (`HasConversion<string>()`, physical default `Bare`); written at each transition and never derived on read. The full app is reachable only when `State == Active`. |

**Shadow columns (no C# property):** `users.GoogleEmail` is kept on disk as an EF shadow property (`UserConfiguration.cs`); no production code reads or writes it any more (nobodies-collective/Humans#1102 retired the last reader) and the column drop is deferred to a later PR per `memory/architecture/no-drops-until-prod-verified.md`. The canonical read path is `UserInfo.GoogleEmail` (derived from `UserEmail.IsGoogle`). `user_emails` carries no shadow columns; display sorting is alphabetical on `Email`. Analyzer `HUM0001` (`UserEmailLegacyFieldAnalyzer`) rejects any reference to the legacy properties (`User.GoogleEmail`, `UserEmail.IsOAuth`, `UserEmail.DisplayOrder`).

**Profile-adjacent fields** — `ContactSource`, `ExternalSourceId` — are also described under [Part 2 — Profiles](#user-identity-extension) because `CachingUserService` stitches them into `UserInfo`. The canonical field table is here; Part 2 describes `UserInfo` projection semantics.

Computed: `IsDeletionPending => DeletionRequestedAt.HasValue`.

User-suspension state lives on `User.State` (`UserState.Suspended` / `AdminSuspended`); `UserInfo.IsSuspended` is the canonical predicate. Single-user writes go through the internal `IUserManagement.SetSuspensionAsync` (called by `HumanLifecycleService`), which delegates to `IUserRepository.SetSuspensionAsync` — that call writes `users.State` and nothing else. The suspension **reason** is not stored on the row: `HumanLifecycleService.SuspendAsync` puts it in the audit log (`AuditAction.MemberSuspended`) and in the `AccessSuspended` notification body. `Profile.AdminNotes` is a general-purpose admin field and is never written by the suspension path. The nightly consent-expiry sweep prepares user details and one team-membership snapshot before changing state, so lookup failures leave suspension retryable; downstream cleanup and audits process only the IDs the write actually changes. The bulk path is `IUserManagement.SuspendProfilesForMissingConsentAsync` → `IUserRepository.SuspendManyAsync`, which returns only the users whose state actually moved — a row already `Rejected`/`Merged`/`Deleted` outranks `Suspended` and is left alone. Unsuspending re-classifies from the remaining fields, so a rejected or deletion-pending row does not come back as `Active`. Rejection is written by `IUserWrite.SetRejectedAsync` (Onboarding's reject, after Consent records the rejection in `consent_reviews`; Users decomposition commit 12) and carried forward from the stored `UserState.Rejected` by every later classification, alongside the frozen legacy `profiles.RejectedAt`. `UserInfo.IsRejected` reads both. The User entity has no `IsArchived` / `SuspendedAt` / `SuspensionReason` columns; "archive" / "lockout" semantics are achieved by anonymizing identity fields and removing OAuth logins through `IAccountDeletionService.PurgeAsync` / `AnonymizeExpiredAccountAsync`.

`UserStateEvaluator.Classify`'s `isGdprDeleted` (→ `UserState.Deleted`) is keyed on the minted tombstone email (`deleted-<id>@deleted.local`, written by `ApplyExpiredDeletionAnonymizationAsync`), never on a user-editable name field — `DisplayName`/`BurnerName` are things a member can type, and a burner name of "Deleted User" must not read as erasure (nobodies-collective/Humans#1742). A narrower legacy fallback recognises the complete pre-email tombstone shape — `DisplayName`, `FirstName` and `LastName` all set to their sentinel values together — for rows anonymized before the email scrub existed. The `users.legacy-gdpr-tombstones` data migration re-erases those rows so they carry the minted email; once production reports it done, the fallback retires with the #1102 `DisplayName` column drop.

`User`'s only navs are `User.UserEmails` (required by the `User.Email` override) and `User.EventParticipations` (the frozen legacy table). Every other relationship is FK-only, configured inverse-side on the owning entity (e.g., `ProfileConfiguration.HasOne<User>().WithOne().HasForeignKey<Profile>(p => p.UserId)`); readers route through `IUserService` / the owning section's service. This is documented, not test-pinned — a test asserting an entity lacks a nav property is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md).

`User.Email` is overridden on the entity to compute from `UserEmails` (first verified, primary-preferred) — application code reads `user.Email` and gets the canonical address without touching the underlying Identity column (or `UserInfo.PrimaryEmail` for canonical reads on a singly-loaded User without `UserEmails` hydrated). `User.NormalizedEmail` is `[Obsolete]` (diagnostic id `HUM_USER_NORMALIZEDEMAIL`); applications must use `user.Email` or `IUserEmailRepository` for canonical lookups. `User.EmailConfirmed` is also overridden (true when any `UserEmail` is verified). Identity's email-lookup APIs (`UserManager.FindByEmailAsync` / `FindByNameAsync`) are forbidden by analyzer `HUM0003` — application code routes through `IUserEmailService.FindByAddressAsync` instead. Identity itself does not call these internally, so no custom `UserStore` is needed.

Every newly created User has a corresponding Profile row materialized inline at the User-creation call site (see [Stub Profile invariant](#part-2--profiles)); until the names are filled the user is `UserState.Bare`. Legacy profile-less users are reconciled by the `users.stub-profiles` data migration on `/Debug/DataMigrations`.

### EventParticipation (legacy, frozen)

**Legacy, no longer written.** Event participation moved to Tickets' `tickets_event_participations` (Users decomposition commit 13; see `Humans.Tickets/Docs/Tickets.md`). This table stays until the `tickets.event-participations` copy reports done in production and a drop PR removes it.

**Table:** `event_participations` (EF configuration: `Data/Configurations/EventParticipationConfiguration.cs`.)

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK (`init`) |
| UserId | Guid | FK → User. The inverse `User.EventParticipations` collection is declared on `User`; there is no forward `EventParticipation.User` nav. |
| Year | int | Year of the event |
| Status | LegacyParticipationStatus | `NotAttending` (0), `Ticketed` (1), `Attended` (2), `NoShow` (3). Stored as string. |
| Source | LegacyParticipationSource | `UserDeclared` (0), `TicketSync` (1), `AdminBackfill` (2). Stored as string. |
| DeclaredAt | Instant? | Set when the user self-declared (Source = `UserDeclared`); null otherwise. |
| CheckedInAt | Instant? | First recorded gate arrival. |

**Indexes:** unique on `(UserId, Year)`.

The rows still load into `UserInfo.EventParticipations` (`[Obsolete(HUM_USER_PARTICIPATIONS)]`), read only by Tickets' `EventParticipationService.LegacyRows`; the warm-cache user-row refresh carries the slice across unchanged. Users no longer folds the table on account merge — Tickets folds the effective records.


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

The entity carries `TargetUser`, `SourceUser`, and `ResolvedByUser` navigation properties (configured with `HasOne(...).WithMany().HasForeignKey(...)`); the merge admin views read them directly. Strip and route through `IUserService.GetUserInfosAsync` when this pattern is generalised across the section — see `Docs/health.md`.

### Identity framework tables

`UsersDbContext.OnModelCreating` renames every Identity table to a lowercase `snake_case` Postgres-friendly name:

- `user_claims` (was `AspNetUserClaims`)
- `user_logins` (was `AspNetUserLogins`)
- `user_tokens` (was `AspNetUserTokens`)
- `roles` (was `AspNetRoles`) — ASP.NET Identity creates the table because `IdentityDbContext<User, IdentityRole<Guid>, Guid>` is used. Authorization itself does **not** read this table — role membership is computed from `role_assignments` by `RoleAssignmentClaimsTransformation` (see [Auth.md](../../Humans.Auth/Docs/Auth.md)).
- `user_roles` (was `AspNetUserRoles`) — same rationale; not used by the runtime authorization path.
- `role_claims` (was `AspNetRoleClaims`) — same rationale.

These are managed by `UserManager<User>` / `SignInManager<User>` / `RoleManager<IdentityRole<Guid>>` from `Microsoft.AspNetCore.Identity`. Do not write a custom repository over them.

## Routing

The section's controllers (`Controllers/`): `ProfileController` (`/Profile/Me/*`, the member's own profile), `ProfileEmailsController` (`/Profile/Me/Emails/*` and `/Profile/{id}/Admin/Emails/*`), `ProfileViewController` (`/Profile/{id}`, `/Profile/Picture`, popovers, `/Profile/Search`), `ProfileApiController` (`/api/profiles/*`), `UsersAdminController` (`/Users/Admin/*`), `UsersAdminAccountMergesController`, `ProfileAdminController` (`/Profile/Admin/EmailProblems/*`). The section's one-off copies are data migrations (`SectionDataMigrations`, below), run from Debug's `/Debug/DataMigrations`. The Profile, User, Guest, API and admin routes are tabled under [Part 2 — Routing](#routing-1); Part 1 covers identity.

Authentication routes are served by `AccountController`, which lives in `Humans.Web/Controllers/` (the Shell) but dispatches into this section's services (`ExternalLoginService`, `IMagicLinkService`, `AccountProvisioningService`):

**`AccountController`** (`/Account/*`, `Humans.Web/Controllers/AccountController.cs`) — authentication and user creation:
- `GET /Account/Login` — login page
- `POST /Account/ExternalLogin` — initiates Google OAuth
- `GET /Account/ExternalLoginCallback` — OAuth callback; creates/links/signs-in user
- `POST /Account/MagicLinkRequest` — sends magic link to email
- `GET /Account/MagicLinkConfirm` — landing page (prevents scanner token consumption)
- `POST /Account/MagicLink` — verifies token and signs in
- `GET /Account/MagicLinkSignup` — displays signup form after token verification
- `POST /Account/CompleteSignup` — creates new user via magic link
- `GET /Account/GateLogin` — gate-terminal username/password form (shared kiosk)
- `POST /Account/GateLogin` — authenticates the shared gate account, `isPersistent: true`; throttled by source IP via `GateLoginThrottle`
- `POST /Account/Logout`
- `GET /Account/AccessDenied`

**Served by Gdpr, not this section:** `/Gdpr/Status`, `/Gdpr/Deletion`, `/Gdpr/Deletion/Cancel` (`UserController`), `POST /Gdpr/RequestDeletion` and `GET /Gdpr/DownloadData` (`MemberDataController`) — the moved pages take Gdpr's URLs. They inject `IAccountDeletionService` from `Humans.Users.Contracts`; the deletion work (grace period, ticket hold, audit, email, the timestamps on `users` behind `IUserWrite`, the anonymize job) stays here.

**Served by Email and Gdpr since Users decomposition commit 14:** `/Email/Unsubscribe/*`, `/Email/Guest/CommunicationPreferences*` and `/Email/CommunicationPreferences*` (Email); `POST /Gdpr/Guest/RequestDeletion` and `/Gdpr/Guest/CancelDeletion` (Gdpr's `GuestDataController`, still calling this section's `IAccountDeletionService`). Each took its new owner's URLs.
- Member profile, email, search and messaging pages localize their breadcrumb navigation landmark in all six cultures. The profile’s application contribution prompt uses the shared six-culture Elsewhere wording.
- The `/Consent/MyData` page (Consent's) is not served by this section; its `ProfilePrivacy_Title` key stays here only for the profile-hub link.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone with a valid email | Sign up via OAuth (`/Account/ExternalLogin`) or magic link (`/Account/MagicLinkRequest`). Use `/Email/Unsubscribe/{token}` (no login required). |
| Import jobs (Tickets, MailerLite) | Call `IAccountProvisioningService.FindOrCreateUserByEmailAsync` to materialize contact-only User rows (no `LastLoginAt`); they get back the user id and whether it was created, never the `User` entity. |
| Authenticated human | Read own User row. Request account deletion. (Self-declaring `NotAttending` is Tickets' `TicketParticipationController`.) |
| HumanAdmin, Board, Admin | Read any User's deletion / login state. Suspension itself is a Profile concern (see [Part 2 — Profiles](#part-2--profiles). |
| Admin | Trigger account merge flows on the unified **Account Merges** page (`/Users/Admin/AccountMerges`, `UsersAdminAccountMergesController`); `AccountMergeService` + `DuplicateAccountService` are Users-section services. Outside Production only: purge a human via `UsersAdminController.PurgeHuman` (`POST /Users/Admin/{id}/Purge`). |

## Invariants

- GDPR-erased accounts cannot use current or legacy unsubscribe tokens, or a lingering Guest session, to read or recreate communication preferences. Live merge survivors remain eligible.

- Guest preference tokens minted before an account merge read and update the surviving account, matching the one-click unsubscribe flow.

- Communication preference writes share one channel-update path. Signed-in profile and Guest changes audit their authenticated actor; token/one-click and imported changes retain service attribution. Email-only writes preserve the inbox channel; no-ops preserve timestamps, source and audit history.

- Self-service email visibility accepts only defined visibility values. Unsupported text and out-of-range numeric input use the existing hidden fallback rather than persisting an undefined audience; valid names, case-insensitive names and defined numeric values retain their meaning.

- The profile's onsite chip is Tickets' `OnsiteChipViewComponent` on the `UserPartSlots.Profile` slot (Users decomposition commit 13). Member email-grid and unsubscribe GETs retain browser cancellation for their token-aware service reads.

- Profile-card volunteer-history descriptions retain their 80-character excerpt limit without splitting UTF-16 surrogate pairs; the complete history description remains stored and exported.

- Member, guest and read-only admin communication-preference checkboxes expose localized channel/category names to assistive technology, including locked and unchecked controls.

- Guest preference saves disable both editable channels and the matching one-click unsubscribe action until completion. Writes to one category cannot overlap in the page; other categories remain available, and failures restore the edited value before unlocking.

- Member email-preference changes preserve the row’s existing inbox preference when the alert control is not shown; changing email cannot silently re-enable inbox notifications.

- Guest communication-preference GETs carry request cancellation through session/token user lookup, preference reads and ticket holdings. Cancellation propagates without an error flash; paired POST helpers retain their default mutation token.
- Member and guest communication-preference POSTs require both channel flags explicitly. Missing flags and binding errors return HTTP 400 and a Warning after session/token resolution, before writing either channel. Unresolved callers remain HTTP 401, and explicit false channel flags remain valid.

- Member and guest communication-preference category names, descriptions, ticket-year heading and ticket-lock note use Users resources in all six cultures. Guest one-click banners and legacy marketing unsubscribe pages use the same translated category names; stored category identifiers and preference rules are unchanged.

- Account-status, pending-deletion and communication-preferences GETs retain request cancellation during viewer resolution; cancellation propagates without an error flash. Deletion and preference POSTs keep their existing mutation boundaries.

- The login page’s locked-account error and dismissal label render in all six supported cultures. Authentication and lockout behavior are unchanged.

- OAuth login (`ExternalLoginService.CompleteExternalLoginAsync`, dispatched from `AccountController.ExternalLoginCallback`) matches existing accounts through verified `UserEmails` before creating a new account. Selecting an account by email also requires the provider's `email_verified` claim; false, missing or malformed claims cannot link to or sign in that account. The locked-out branch may re-link a stale OAuth login from a merged source to the email-matched target only with the same provider-verification proof, before removing any login. Known provider-key sign-ins and intentional links from an authenticated account retain their existing paths.
- `AccountController`, the Development section's `DevLoginController` / `DevPersonaSeeder` / `DevelopmentDashboardSeeder`, and the ASP.NET Identity framework surface may inject `UserManager<User>` and `SignInManager<User>` directly — this is the explicit §2a exception because Identity is a framework concern, not a domain service. Application-layer code (`AccountProvisioningService`, `ExternalLoginService`) may also inject `UserManager<User>` for user creation; everything else routes through `IUserService`.
- Users never writes `event_participations` (frozen since Users decomposition commit 13); the participation lifecycle rules live in Tickets.
- **The Google Workspace identity is GoogleIntegration's** (`google_workspace_identities`, Users decomposition commit 15). `UserEmail.IsGoogle` and `UserEmail.GoogleEmailStatus` are frozen: Users never writes them (new rows keep the defaults; the merge re-FK still clears `IsGoogle` on rows it moves), and their only reader is GoogleIntegration's legacy fallback and `google.workspace-identities` copy through the `[Obsolete(HUM_USER_GOOGLE_IDENTITY)]` `UserEmailInfo` properties. `EnsureGoogleInvariantAsync`, `SetGoogleAsync`/`ClearGoogleAsync` and `TrySetGoogleEmailStatusFromSyncAsync` are gone. Every path that removes or renames an address away from an account — `DeleteEmailAsync`, `DeleteByIdAsync`, `UnlinkAsync`, an OAuth tag-move or rename, and a cross-user displacement — calls `IGoogleIntegrationWrite.ClearIfAddressAsync` after it commits.
- Users never writes `communication_preferences` (frozen since Users decomposition commit 14); the preference and unsubscribe rules live in Email.
- `UserService` implements `IUserDataContributor` (design-rules §8) and contributes the User-account slice (Id, Email, DisplayName, PreferredLanguage, UnsubscribedFromCampaigns, SuppressScheduleChangeEmails (the two dead legacy flags, until their drop), ContactSource, deletion / created / last-login timestamps) under `UserService.Account` plus an array of `{ UserId, Year, Status, Source, DeclaredAt, CheckedInAt }` rows under `UserService.LegacyEventParticipations` (every frozen legacy row of the user and the accounts merged into them; the live record is Tickets' `EventParticipations` slice) to the GDPR export. The legacy slice is retained on erasure, as before. It also emits the Profiles-half slices `Profile`, `ContactFields`, `UserEmails`, `VolunteerHistory`, `Languages` and `LegacyCommunicationPreferences` (the frozen rows; the live record is Email's `CommunicationPreferences` slice) — see [Part 2 — Profiles](#part-2--profiles).
- **The cross-section reads resolve merge chains forward, and Users is the only thing that walks one.** `GetUserInfoAsync`, `GetUserInfosAsync` and `GetAllUserInfosAsync` answer a merge-tombstone id with the surviving row (transitively, so A→B→C resolves to C), and drop tombstones from the load-all read entirely, one entry per living human. Every row they return is stamped with `MergedUserIds`, and `UserInfo.AllUserIds` is that row's own id followed by them: the list a chain-following read queries over, seeded from the record returned and never from the id the caller asked with. `SearchUsersAsync` omits tombstones, including exact-id queries, and answers a merged-away id with its live survivor. A chain ending at an erased or unresolved merged row returns no search result. A cycle or a chain pointing at a row that is no longer there is logged at Warning and resolves to the last row reached, so a bad graph degrades rather than spins.
- **A resolving read hands the caller a different id, and the caller acts on that one.** Whatever a caller does after `GetUserInfoAsync` / `GetUserInfosAsync`, write, look up by id, notify, audit, redirect, it does with `UserInfo.Id` (or `AllUserIds`), not with the id it asked for. A path that must not act for anyone but the id it was handed (the Gate claim, an Asociado ballot stored under an id) compares `info.Id` to the requested id and fails closed. Raw per-id row reads that stamp the owner id (`GetEntitiesByUserIdAsync`, `GetEntitiesByUserIdsAsync`, the email-problems page) stay on the raw reads so rows and the id labelling them come from one row.
- **The raw reads are the escape hatch, and there are exactly two.** `IUserService.GetRawUserInfoAsync` and `GetAllRawUserInfosAsync` return the row as stored, tombstones included, still stamped with `MergedUserIds`. They exist for this section's own merge machinery (`AccountMergeService`, `AccountDeletionService`, the admin screens) and for `RoleAssignmentClaimsTransformation`, which must stamp a tombstone principal `Merged` rather than inherit the survivor's Active state. No other section calls them.
- **The merge index is derived state, rebuilt on demand.** `CachingUserService` builds a `Dictionary<Guid, Guid[]>` over its warmed snapshot the first time a read needs it, and drops it on every mutation of the dict (`TrackedCache.OnMutated`). It is never persisted and never invalidated by hand; a merge, an erasure or a cache refresh all mark it dirty through the same hook. Build and drop share one lock, so a mutation that lands mid-rebuild is not overwritten by the stale index.
- A `User` row whose `MergedToUserId` is non-null is a **merge tombstone**: its identity fields are anonymized, its OAuth logins have been re-FK'd to the target, `LockoutEnd` is far-future, but the row itself persists indefinitely so append-only history written under the source id stays resolvable. The self-referential FK is `OnDelete(Restrict)` — deleting a target cannot cascade-delete its source tombstones.
- `UserInfo.IsActive` excludes tombstones (`IsTombstone`: merged, GDPR-anonymized, or legacy `.local`-email rows) — a tombstone keeps its anonymized Profile row, so "has a profile" alone doesn't exclude it (peterdrier/Humans#1707).
- **A merge is atomic by ordering, not by transaction.** `AccountMergeService.MergeAsync` (reached from `AcceptAsync`) uses no `TransactionScope` — crossing section DB boundaries in one transaction is disallowed. Instead the steps run in a fixed order and each commits independently: move every section's rows via the `IUserMerge` fan-out, settle the pending email, **tombstone the archived account last**. The tombstone is the single observable commit point and the source of truth; a failure before it leaves the archived account untouched and the request still actionable. Every `IUserMerge.ReassignAsync` must therefore stay idempotent — "move all of archived's X to survivor" is a no-op on re-run — so a partial merge is safely retryable. The pending-email settle is deliberately **non-fatal**: a missing or already-consumed pending email is the desired end state, not an error. The only `TransactionScope` in the service is in `RejectAsync`, wrapping the pending-email delete and the request close so a dangling `PendingEmailId` can never block a later accept.
- **Merge-request state is derived from the tombstone.** A `Pending` `AccountMergeRequest` whose two accounts are already merged into each other (`source.MergedToUserId == request.TargetUserId` or the inverse) is moot and is closed on sight — this is the cleanup mechanism for half-done rows, not a data migration. A side merged into some unrelated third account is a different conflict and is refused rather than silently closed.
- **No merge status beyond `Pending` / `Accepted` / `Rejected`.** Dismiss reuses `Rejected`; a request auto-closed by a completed merge becomes `Accepted` with a note (`CloseRequestsForPairAsync`). Request rows are never deleted, so GDPR export and audit history stay intact.

- User-row interceptor snapshots carry the stored `State`. Warm-cache user-field refreshes reuse the canonical `UserInfo` factory, including that state, while preserving profile, email, participation, login, preference, and merge-ID slices.
- A failed or cancelled `UserInfo` cache reload evicts the affected user and marks the cache cold before propagating the failure; the next read reloads from source rather than retaining stale fields or omitting a newly created human.
- Contact-field and profile-language saves resolve the owner before committing, so a failed lookup cannot strand committed changes outside cache refresh.
- Profile fields, a supplied CV and the legacy identity name share one database save and the per-user save lock; failures or overlapping submissions cannot separate the experience declaration from its history. A null CV payload leaves history unchanged.

The operator-only user directory renders its sort-control label directly, without a missing resource key.

## Negative Access Rules

- Controllers (other than `AccountController` / the Development section's `DevLoginController` / the ASP.NET Identity framework surface) **cannot** inject `UserManager<User>` or `SignInManager<User>`. They go through `IUserService`.
- Services in `Humans.Users/Services/` **cannot** inject a DbContext directly — they go through `IUserRepository` / `IUserEmailRepository`.
- Other Application-layer services **cannot** read or write the `users` / Identity tables directly — they go through `IUserService` (or, inside this section, `IUserManagement`).
- Regular humans **cannot** purge any account.
- An Admin **cannot** purge their own account — `IAccountDeletionService.PurgeAsync` refuses when the actor is the target, and `UsersAdminController.PurgeHuman` returns the user to the admin-detail page with an error.
- Purge **cannot** run in Production — `IAccountDeletionService.PurgeAsync` refuses when `IHostEnvironment.IsProduction()`, and `UsersAdminController.PurgeHuman` returns `NotFound`. Account anonymization (the GDPR-deletion path through `AnonymizeExpiredAccountAsync`) runs in every environment via `ProcessAccountDeletionsJob`.
- A merge-tombstoned `User` (`MergedToUserId` non-null) **cannot** sign in — `LockoutEnd` is bumped far-future during fold. Application code outside `IAccountMergeService` **cannot** clear `MergedToUserId` / `MergedAt` or revive a tombstone.

## Triggers

- **On first OAuth login (no matching account):** `ExternalLoginService.CompleteExternalLoginAsync` (dispatched from `AccountController.ExternalLoginCallback`) creates the `User` via `UserManager.CreateAsync`, attaches the external login, and persists a provider-tagged `UserEmail` row via `IUserEmailService.ReconcileOAuthIdentityAsync` (HUM0005 pins `ExternalLoginService` as its sole caller); `AccountController` then signs the user in via `SignInManager.SignInAsync`. Profile creation happens lazily in the Profile section (see [Part 2 — Profiles](#part-2--profiles).
- **On import (Tickets / MailerLite contact upsert):** `AccountProvisioningService.FindOrCreateUserByEmailAsync` looks up an existing user by `UserEmail` and `User.Email` (with gmail/googlemail equivalence), creates a contact-only `User` + `UserEmail` if no match, layers `User.ContactSource` onto an existing self-registered user when null, and writes an `AuditAction.ContactCreated` audit entry on creation.
- Unverified-address replacement is one Users-owned `ReplaceUnverifiedEmailAndProvisionAsync` operation. It rechecks the planned row, rejects verified/provider-linked/stale rows, and retains ordinary deletion guards. A serializable transaction contains deletion, account/email/stub-profile creation and audits; any provisioning failure restores the original state. The deletion audit identifies the old user and new account. Affected user caches refresh after commit or rollback.
- **On unsubscribe click / RFC 8058 one-click:** Email's `UnsubscribeService` opts the user out of the message category (Email since Users decomposition commit 14). The dead `User.UnsubscribedFromCampaigns` column is never touched — Email's per-category preferences are the source of truth for opt-out.
- **On admin participation backfill (Tickets since decomposition commit 13):** `/Tickets/Participation/Backfill` previews the parsed valid CSV rows before an explicit confirm POST; preview performs no writes. Confirmation requires an admin actor ID. Tickets' `EventParticipationService.BackfillParticipationsAsync` writes records with `Source = AdminBackfill` and audits each changed member’s final status/year after the repository save, before cache refresh. Counts describe unique changed records: protected Attended rows and identical re-imports produce neither changes nor success audits; duplicate input rows retain the existing sequential status rules. CSV parsing skips undefined numeric statuses and unparseable rows; no valid entries means no write. The default-year GET observes request cancellation.
- Email-verification, account-deletion request and erasure-confirmation emails use the recipient's supported saved language, falling back to English for blank, invalid or unsupported preferences. Erasure confirmations retain their direct, non-persisted delivery.
- **On account-deletion request (user-initiated):** `IAccountDeletionService.RequestDeletionAsync` orchestrates the user-initiated path. Internally calls `IUserManagement.SetDeletionPendingAsync` to stamp `DeletionRequestedAt` + `DeletionScheduledFor` on the User row, revokes team memberships and governance roles immediately so the user loses access during the 30-day grace period, and sends a confirmation email. Its cancellation link opens the existing `/User/Deletion` status page, which only shows the signed-in user’s pending request.
- **On scheduled deletion expiry:** `ProcessAccountDeletionsJob` calls `IAccountDeletionService.AnonymizeExpiredAccountAsync` per due user. It captures the identity slice the caller needs for the confirmation email, resolves the merge chain, and calls `IGdprService.EraseForUserAsync` per id — the erasure fan-out lives in Gdpr beside the export one, so the loop over `IUserDataContributor`s and the `UserService.Account`-last ordering are Gdpr's concern. A contributor that throws aborts the run with the deletion markers still set, so the whole cascade retries the next day. Each archived id's `UserInfo` cache entry is dropped as its erasure completes (interleaved, so a mid-chain failure still invalidates the ids already erased); then the cross-cutting caches (Teams active-teams + member, role-assignment claims, shift authorization, shift view) — the survivor's `UserInfo` entry refreshes automatically as part of the `IUserService` write path. The job writes the `AuditAction.AccountAnonymized` audit entry and sends the confirmation email.
- **On admin purge (non-Production only):** `UsersAdminController.PurgeHuman` (`POST /Users/Admin/{id}/Purge`) calls `IAccountDeletionService.PurgeAsync`, which runs the same Article 17 fan-out as the scheduled expiry path — `IGdprService.EraseForUserAsync` per merge-chain id, survivor last — so an admin purge never erases less than the scheduled job does; the Account contributor inside that fan-out owns the identity collapse (tombstone name/address, external logins, permanent lockout). The orchestrator then drops the Teams active-teams cache, refreshes the cached `UserInfo` entry, and invalidates per-user role-assignment / shift-authorization / shift-view caches.
- **On account merge accept:** `IAccountMergeService.AcceptAsync` (Users section) resolves the request and delegates to the one merge engine, `MergeAsync(survivor, archived, …)` — the admin picks the survivor, so the request's stored direction can be flipped. `MergeAsync` fans out its `IUserMerge` implementations to re-FK source → target (Users' own `IUserMerge.ReassignAsync` calls `IUserRepository.ReassignLoginsToUserAsync` for `AspNetUserLogins`; event participation is Tickets' `IUserMerge`, and the frozen `event_participations` rows stay under their ids), then `AnonymizeForMergeAsync` to tombstone the source row by setting `MergedToUserId` + `MergedAt` and bumping `LockoutEnd` far-future so the source can no longer sign in. The source `User` row stays in place as a redirect — it is NOT deleted — so append-only history written under the source id (audit log, consent records, budget audit log) remains attributable.
- **On per-user reads of a target after merge:** every `UserInfo` the cross-section reads hand out carries `MergedUserIds`, every id whose `MergedToUserId` chain reaches this row, transitively, sorted. Append-only sections (`IAuditLogService`, `IConsentManagement`, `IBudgetManagement.ContributeForUserAsync`, `IExpenseService.ContributeForUserAsync`) query over `AllUserIds` of the record they got back, so source-tombstoned rows surface for the target and a read through a source id sees the target's own rows. Nobody outside Users walks `MergedToUserId`.

## Cross-Section Dependencies

Outbound (Users → other sections). `Humans.Users.csproj` references, all acyclic — none of the targets names a `Humans.Users` type (they reference `Humans.Users.Contracts` at most):

- **Contracts surfaces** (a `.Contracts` leaf, or a section project for a `Contracts/` folder): `AuditLog` (every write path records an audit entry), `Auth` (role assignment on provisioning and merge), `Camps` (camp name + roles on the admin popover), `Consent` (profile completeness gates on the consent state), `Email` (verification, unsubscribe and merge-notification mail — Users owns its four templates (`email_verification`, `deletion_requested`, `account_deleted`, `access_suspended`) via `UsersEmails` + `UsersEmailPreviews`, keys `Users_Email_*`; Email is the transport only), `Gdpr` (`UserService` and `AccountMergeService` are Article 15 contributors), `Governance` (`IMembershipCalculator` for non-compliant suspension; `IGovernanceService` for membership-tier reads), `GoogleIntegration` (`ITeamResourceService` verifies a coordinator's team Group before team-reply messaging; `IGoogleIntegrationWrite.ClearIfAddressAsync` when an address leaves an account and `IGoogleIntegrationService` for the admin list's `googlerejected` filter; its `<vc:nobodies-email-badge>` and `<vc:workspace-identity>` render on Users' views), `Notifications` (merge requests and email problems notify the human), `Onboarding` (profile editing reports onboarding progress), `Shifts` (participation backfill reads the burn settings; `IShiftSignups` for no-show history), `Cantina` (`ICantinaService` — the medical prompt shows only while dietary is still open), `Teams` (account merge folds team memberships), `Tickets` (participation is derived from ticket sync).
- **Full section projects** — referenced whole because Razor binds a view component's tag helper only from its declaring assembly, so `@addTagHelper` needs the assembly, not its leaf: `Humans.Camps` (`<vc:my-camps />` on `Views/Profile/Index.cshtml`), `Humans.AuditLog` (`IAuditViewerService` in `ProfileController`; `<vc:audit-log>` on `AdminDetail.cshtml`), `Humans.Shifts` (`<vc:shift-signups>` on `Views/Profile/Index.cshtml` and `Views/UsersAdmin/AdminDetail.cshtml`, plus `IStringLocalizer<ShiftsResource>` in `Views/Profile/Edit.cshtml` for the shift-preference link's strings). Each drops out only when the component moves to the leaf.

The deletion cascade is `AccountDeletionService` (`Humans.Users/Services/`), the one Users service that explicitly bridges higher-level sections:
- **Gdpr:** `IGdprService.EraseForUserAsync` — the erasure fan-out lives in Gdpr; `AccountDeletionService` resolves the merge chain, calls it per id, and invalidates each archived id's cache as its erasure completes. It names no section to erase; Gdpr fans out over its own `IUserDataContributor` roster.
- Auth role projections for contact visibility, duplicate detection and profile cards use `IRoleAssignmentService`. Deletion-time revocation and post-merge cache invalidation go through `IRoleAssignmentWrite`; the admin assign/end pages are Auth's own.
- **Auth:** `IRoleAssignmentWrite.RevokeAllActiveAsync` (grace-period revoke on the deletion *request*), `IRoleAssignmentClaimsCacheInvalidator.Invalidate`.
- **Teams:** `ITeamWrite.RevokeAllMembershipsAsync` (same grace-period revoke), `RemoveMemberFromAllTeamsCache`.
- **Shifts:** `IShiftAuthorizationInvalidator.Invalidate`, `IShiftViewInvalidator.InvalidateUser` — cross-cutting cache drops only; the Shifts data itself is erased by that section's own contributor.

Communication preferences are Email's (Users decomposition commit 14). The only remaining write to the frozen `communication_preferences` table is erasure; `UserInfoSaveChangesInterceptor` still pattern-matches `CommunicationPreference` saves and refreshes the legacy slice (`RefreshCommunicationPreferencesAsync`).

Inbound (other sections → Users) — the typical direction:

- **Tickets:** reads the frozen `event_participations` rows through `IUserService.GetRawUserInfoAsync` / `GetAllRawUserInfosAsync` (`UserInfo.EventParticipations`, one suppressed reader) for its fallback, write seed, merge fold and copy. Nothing writes the table.
- **Notifications, Email, AuditLog:** call `IUserService.GetUserInfosAsync` (batched `UserInfo` lookup — the caching decorator's dict already carries the full payload, including `UserEmails`) to resolve recipient identity/email without navigating cross-domain navs.
- **Account-deletion job (`ProcessAccountDeletionsJob`, `Humans.Users/Jobs/`):** calls `IUserManagement.GetAccountsDueForAnonymizationAsync` + `IAccountDeletionService.AnonymizeExpiredAccountAsync`.
- **Account merge (`IAccountMergeService.AcceptAsync`, Users section):** the `IUserMerge` fan-out calls Users' own `IUserMerge.ReassignAsync` (which re-FKs `AspNetUserLogins` via `IUserRepository`) and `IUserManagement.AnonymizeForMergeAsync` to fold a source User into a target.
- **Audit Log / Consent / Budget / Expenses:** read `UserInfo.AllUserIds` off the row `GetUserInfoAsync` / `GetUserInfosAsync` already returned, to chain-follow merge tombstones on per-user reads of append-only entities.

## Architecture

Identity store registration stays in the section entry point. Shell calls the contract bridge, which names no EF types; `User : IdentityUser<Guid>` remains the intentional public Identity model.

A lazy per-key cache miss cannot republish its old result after an intervening cache eviction or refresh.

**Owning services:** `UserService`, `AccountProvisioningService`, `AccountDeletionService`, `AccountMergeService` + `DuplicateAccountService` (the one ordered merge engine and the stateless duplicate detector; `AccountMergeService` is backed by `IAccountMergeRepository` for `account_merge_requests` and contributes the `AccountMergeRequests` GDPR slice), `ExternalLoginService` (the OAuth-callback decision ladder, kept out of `AccountController` per HUM0031; sole caller of `IUserEmailService.ReconcileOAuthIdentityAsync`) — all in `Humans.Users/Services/`.
**Owned tables:** `users`, `user_claims`, `user_logins`, `user_tokens`, `roles` (legacy), `user_roles` (legacy), `role_claims` (legacy), `event_participations` (legacy, frozen — Tickets owns participation), `account_merge_requests`.

- `UserService`, `AccountProvisioningService` live in `Humans.Users/Services/` and never import `Microsoft.EntityFrameworkCore`. `AccountProvisioningService` does inject `UserManager<User>` per the §2a exception (Identity owns the password hash / security stamp surface).
- `IUserRepository` (impl `Humans.Users/Data/Repositories/UserRepository.cs`) owns the SQL surface for `users` plus reads of the frozen `event_participations`. `IUserEmailRepository` is the parallel surface for `UserEmail` (the Profiles half's table, read/written from the Users half for lookup + OAuth-email lock-step).
- **Caching decorator.** `CachingUserService` (Singleton, `Humans.Users/Data/`) owns a `ConcurrentDictionary<Guid, UserInfo>` of the unified read-model spanning the contributing tables (`users`, `user_emails`, `event_participations`, AspNet `user_logins`, `profiles`, `contact_fields`, `profile_languages`, `volunteer_history_entries`, `communication_preferences` (legacy, frozen)). Dict hits are served synchronously; a cache miss refills via the inner Scoped `UserService` (keyed `"user-inner"`); writes through `IUserService` refresh the affected entry (sign-in `LastLoginAt` flows through `IUserWrite.RecordLoginAsync`, whose decorator refreshes the entry directly); Identity-machinery writes that bypass the service surface (`UserManager.UpdateAsync`, OAuth `UserEmail` row creation) are caught by `UserInfoSaveChangesInterceptor` (EF `SaveChangesInterceptor`, registered on both the scoped and factory contexts) and routed through the section-internal `IUserInfoSliceRefresher`. Every contributing table is Users-owned, so no other section signals the cache. `CachingUserService` itself implements `IHostedService` (via `TrackedCache`) and populates the dict at startup; load-all reads call `EnsureWarmed` / `EnsureWarmedAsync` and recover transparently if startup warmup hasn't completed. `UserInfo` is the single cache — there is no separate profile cache (`Docs/health.md`).
- **Read/write interface split.** **Public contract.** `IUserService` (`GetUserInfoAsync`, `GetUserInfosAsync`, `GetAllUserInfosAsync`, `SearchUsersAsync`, `GetByEmailOrAlternateAsync`, `GetRawUserInfoAsync`, `GetAllRawUserInfosAsync`) on the `Humans.Users.Contracts` leaf is the cross-section read surface — only `UserInfo` / `HumanSearchResult` projections plus Guid primitives, no EF entities. `IUserWrite` (same leaf) carries only the writes another section calls (onboarding mutations, language, IBAN, login stamp, Google email status, user deletion). The internal `IUserManagement : IUserService, IUserWrite, IUserMerge` in `Humans.Users.Services` adds the section-only half of the write funnel — profile saves, deletion fields, `UserEmail` storage commands, the merge tombstone, the email-problems reads, the accounts-due-for-anonymization read; all three faces resolve to the same `CachingUserService` singleton. Neither leaf interface carries the `IApplicationService` marker: the leaf cannot reference Base without a cycle. See [`memory/architecture/section-public-contract.md`](../../../../memory/architecture/section-public-contract.md).
- The UserInfo interceptor completes its post-commit cache refresh without request cancellation, so an aborted request cannot turn a committed save into cancellation and skip its following audit.
- **Identity framework surface** — `AccountController` lives in `Humans.Web/Controllers/` (the Shell), not in this section, and injects `UserManager<User>` / `SignInManager<User>` directly per the §2a exception, as do the Development section's dev-login controller and its account-creating seeders. There is no `AuthController` or `ManageController` class — magic-link orchestration lives in `IMagicLinkService` (Auth section) and account self-management lives across `AccountController` and Profile views. Non-controller code routes through `IUserService`.
- **GDPR:** `UserService` implements `IUserDataContributor` and contributes the `UserService.Account` and `LegacyEventParticipations` slices plus the Profiles-half slices (`Profile`, `ContactFields`, `UserEmails`, `VolunteerHistory`, `Languages`, `LegacyCommunicationPreferences`); `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` discovers contributors by reflection — nothing to register in a list (design-rules §8).
- **Resource set:** `UsersResource.{resx,ca,de,es,fr,it}` at the project root (folder and namespace must agree — nobodies-collective/Humans#1365), covering the whole assembly (both parts of this doc). Views bind it as `Localizer` and `SharedResource` as `SharedLocalizer` in `Views/_ViewImports.cshtml`; `UsersResource.cs`'s remarks record the leave-or-duplicate decision for every key that stayed in `SharedResource`. What stayed and why: `Common_`, `Nav_`, `Dashboard_`, `Validation_` and `Privacy_` are generic; `Admin_` is rendered by the Admin shell frame too; `Application*_`/`ApplicationStatus_` are also rendered by Governance's copy of the tier form; `Teams_`/`MyTeams_`/`TeamDetail_` and `Search_` belong to other renderers; `Enum_EmailOutboxStatus_` is Humans.Email's set, reached through `SharedLocalizer.EnumDisplay`. `Views/Profile/Edit.cshtml` keeps `IStringLocalizer<ShiftsResource>` for its shift-preference strings rather than duplicate the copy. `UserArchitectureTests.SectionTypesLocalizeThroughTheSectionsOwnResourceSet` guards the constructor side; `UsersPageRenderTests` asserts no raw key reaches the HTML, in English and in Spanish.
- **Architecture tests:**
  - `tests/Humans.Users.Tests/Architecture/UserArchitectureTests.cs` — `IUserManagement_IUserService_IUserWrite_And_IUserInfoSliceRefresher_ResolveToSameSingleton` (the decorator is the same singleton under every key), and `SectionTypesLocalizeThroughTheSectionsOwnResourceSet`.
  - Analyzer `HUM0001` (`UserEmailLegacyFieldAnalyzer`, tested by `tests/Humans.Analyzers.Tests/UserEmailLegacyFieldAnalyzerTests.cs`) — flags any reference to the legacy shadow properties (`User.GoogleEmail`, `UserEmail.IsOAuth`, `UserEmail.DisplayOrder`).
  - Analyzer `HUM0003` (`IdentityFindByEmailAnalyzer`, tested by `tests/Humans.Analyzers.Tests/IdentityFindByEmailAnalyzerTests.cs`) — flags `UserManager.FindByEmailAsync` / `FindByNameAsync` calls outside the allowed sites; call sites route through `IUserEmailService.FindByAddressAsync` instead.
  - `AccountDeletionService` is covered functionally by `tests/Humans.Users.Tests/Services/AccountDeletionServiceTests.cs`.

### Touch-and-clean guidance

- `User` has no `Profile` / `TeamMemberships` / `RoleAssignments` / `Applications` / `ConsentRecords` / `CommunicationPreferences` navs and no `GetEffectiveEmail()` — readers route through `IUserService` / `ITeamService` / `IRoleAssignmentService` / etc. or use `user.Email` (which overrides via the `UserEmails` collection). When touching `TeamService` / `GoogleWorkspaceSyncService` / `ProfileEmailsController` and the notification jobs, prefer `IUserEmailRepository.GetByUserIdReadOnlyAsync` / `IUserService.GetUserInfosAsync` over reaching into the `UserEmails` nav directly.
- Do **not** inject a DbContext into any service under `Humans.Users/Services/`. Use `IUserRepository` / `IUserEmailRepository`.
- Event-participation writes belong to Tickets (`ITicketWrite`, and Tickets' internal `IEventParticipationManagement`); nothing writes the frozen `event_participations`.

---

# Part 2 — Profiles


Per-human personal data that Users keeps: the profile core (names, location, bio, pronouns, birthday, picture, emergency contact, languages, burner CV, medical conditions), contact fields, email addresses, account merge and duplicate detection.

Everything else that once sat on `User`, `Profile` and `UserEmail` has moved to the section that owns it (Users decomposition, commits 1 to 15): IBAN to Finance, dietary answers to Cantina, membership tier to Governance, consent review to Consent, event participation to Tickets, communication preferences to Email, Google Workspace identity to GoogleIntegration. The old columns and tables stay on disk as frozen legacy until each replacement's data migration reports done in production; see [Frozen legacy](#frozen-legacy-awaiting-drop).

## Concepts

- A **Profile** holds a human's personal information: name, city, country, birthday (month and day only — never year), profile picture, and admin notes.
- **Contact Fields** are per-field contact details (phone, Signal, Telegram, WhatsApp, Discord, custom) with per-field visibility controls.
- **Visibility Levels** determine who can see each contact field: BoardOnly (most restrictive), CoordinatorsAndBoard, MyTeams (shared team members), or AllActiveProfiles (least restrictive).
- **Membership Tier** (Volunteer, Colaborador, Asociado) is Governance's; Users reads it through `IGovernanceService.GetMembershipTierAsync` / `GetMembershipTiersAsync` (popover, human summary, admin detail, duplicate accounts). **Communication Preferences** are Email's (see `Humans.Email/Docs/Email.md`). The profile and preference columns are frozen legacy.
- **UserEmail** is a per-user email address record. A user has one "login" email plus zero-or-more verified additional addresses; one of them may be flagged as the notification target.
- **CV Entries** (sub-aggregate of Profile, table `volunteer_history_entries`) record volunteer involvement history.
- **Profile Languages** (sub-aggregate of Profile, table `profile_languages`) record self-assessed proficiency in ISO 639-1 language codes.
- **Duplicate Account Detection** scans for email addresses appearing on multiple accounts (across `User.Email` and `UserEmail.Email`, with gmail/googlemail equivalence). Admins act on candidates from the unified **Account Merges** page (`/Users/Admin/AccountMerges`).
- **Email Problems** scans every UserEmail invariant violation (multi/zero IsPrimary, unverified rows, cross-user collisions, orphan rows, ghost AspNetUserLogins). Reads source-of-truth via `IUserService.GetAllUserInfosAsync` (the `UserInfo` cache). Read-only admin surface with deep-links into the per-user `/Profile/{userId}/Admin/Emails` diagnostic for orphan/ghost remediation; the cross-user merge action shares its kernel with `IAccountMergeService.AcceptAsync`.
- **Account Merge** consolidates two accounts into one, transferring all associated data (emails, contact fields, CV entries, role assignments, memberships) to the surviving account.

## Data Model

### User (Identity extension)

User is owned by the **Users/Identity** section; the properties below are the profile-adjacent extensions that Profile consumers read most often. Field-level ownership still belongs here because `CachingUserService` stitches them into `UserInfo`.

#### Google email preference

`User` has no `GoogleEmail` property; the `users.GoogleEmail` column is an EF shadow property (`UserConfiguration.cs`) kept pending a deferred column-drop PR per `memory/architecture/no-drops-until-prod-verified.md`. `UserInfo.GoogleEmail` is gone (Users decomposition commit 15): the Google Workspace address is `IGoogleIntegrationService.GetWorkspaceIdentityAsync`; `UserInfo.PrimaryEmail` is the contact address.

Google sync status is GoogleIntegration's (`google_workspace_identities.Status`, Users decomposition commit 15). The per-address `UserEmail.GoogleEmailStatus` (`LegacyGoogleEmailStatus`) column is frozen until the `google.workspace-identities` drop PR; the user-level `User.GoogleEmailStatus` column is dead (USERS-14). Neither has a live writer.

#### Contact-import properties

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| ContactSource | ContactSource? | null | Where imported from (Manual, MailerLite, TicketTailor); null for self-registered users |
| ExternalSourceId | string?(256) | null | ID in the external source system |

A contact is identified by `ContactSource != null && LastLoginAt == null`. When a contact authenticates, `LastLoginAt` is set and they become a regular user.

#### Dead columns

`UnsubscribedFromCampaigns`, `SuppressScheduleChangeEmails` and `GoogleEmailStatus` (see the Part 1 field table) have no writer and no reader beyond the Art. 15 export, which keeps the first two flags while the columns exist; `UserInfo` and the cache interceptor do not carry them. `GoogleEmailStatus` has no reader at all. EF still maps them (`GoogleEmailStatus` as a shadow column, so no code can reach it; `[Obsolete]` guards the other two), and `Docs/debt.yml` USERS-12 to USERS-14 track their drop.

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
| DietaryPreference | string? (200) | null | **Legacy, no longer written.** Food answers moved to Cantina's `cantina_dietary_profiles` (Users decomposition commit 10). Carried on `ProfileInfo` as `[Obsolete]` (`HUM_PROFILE_DIETARY`) for Cantina's read fallback and the `cantina.profile-dietary` copy only; still exported and erased here until the column is dropped. |
| Allergies / Intolerances | List&lt;string&gt; (jsonb) | [] | Food allergies / intolerances (+ `AllergyOtherText` / `IntoleranceOtherText` for free-text "Other"). **Legacy, no longer written** — same as `DietaryPreference`. |
| MedicalConditions | string? (4000) | null | **GDPR Art. 9** health data. On the cached `UserInfo` but gated at every render/serialize surface by the `MedicalDataViewer` policy (Admin / NoInfoAdmin). Owner edits their own on `/Profile/Me/Medical`. |
| ProfilePictureContentType | string? (100) | null | MIME type of the stored picture. Doubles as the "has custom picture?" predicate (`HasCustomProfilePicture`) and supplies the file extension — the picture bytes themselves live on the filesystem. |
| ProfilePictureStorageKey | string? (300) | null | Versioned `IFileStorage` key of the current picture (`uploads/profile-pictures/{profileId}-{guid}{.ext}`). Null for pictures still at the legacy deterministic key `{profileId}{.ext}`; readers fall back to it. Cleared with the content type on removal, merge and anonymization. |
| ContributionInterests | string? | null | Skills / availability statement (publicly visible on profile). |
| BoardNotes | string? | null | Notes from the human intended for the Board (self + Board only). |
| AdminNotes | string? (4000) | null | Admin-only notes (not visible to the human). |
| Iban | string? (34) | null | **Legacy, no longer written.** The member's IBAN moved to Finance's `finance_payment_details` (Users decomposition commit 1). Carried on `ProfileInfo.Iban` as `[Obsolete]` (`HUM_PROFILE_IBAN`) for Finance's read fallback and the `finance.iban-to-payment-details` copy only; still erased by `EraseProfileExtrasForUserAsync` until the column is dropped. |
| IsApproved | bool | false | **Legacy, no longer written.** Moved to Consent's `consent_reviews` (Users decomposition commit 12). Carried on `ProfileInfo` as `[Obsolete]` (`HUM_PROFILE_REVIEW`) for Consent's read fallback and the `consent.profile-review` copy only; still exported and erased here until the column is dropped. |
| MembershipTier | LegacyMembershipTier | Volunteer | **Legacy, no longer written.** The tier moved to Governance's `governance_membership_tiers` (Users decomposition commit 11). Carried on `ProfileInfo` as `[Obsolete]` (`HUM_PROFILE_TIER`) for Governance's read fallback and the `governance.profile-tier` copy only; still exported here until the column is dropped. |
| ConsentCheckStatus | LegacyConsentCheckStatus? | null | Legacy, as `IsApproved`. |
| ConsentCheckAt | Instant? | null | When consent check was performed. Legacy, as `IsApproved`. |
| ConsentCheckedByUserId | Guid? | null | Consent Coordinator who performed the check. Legacy, as `IsApproved`. |
| ConsentCheckNotes | string? (4000) | null | Notes from the Consent Coordinator. Legacy, as `IsApproved`. |
| RejectionReason | string? (4000) | null | Reason for rejection (when Admin rejects a flagged check). Legacy, as `IsApproved`. |
| RejectedAt | Instant? | null | Legacy, as `IsApproved`. Still read by the state classifier and `UserInfo.IsRejected` (legacy arm) until the retirement PR. |
| RejectedByUserId | Guid? | null | Admin who rejected the profile. Legacy, as `IsApproved`. |
| NoPriorBurnExperience | bool | false | When true, CV entries are not required during onboarding. |
| CreatedAt | Instant | — | Set on insert. |
| UpdatedAt | Instant | — | Maintained by services. |

**Indexes:** unique on `UserId`; non-unique on `ConsentCheckStatus`.

`Profile` has no `User` nav. Consumers resolve User data via `IUserService.GetUserInfosAsync`. Aggregate-local navs `ContactFields`, `VolunteerHistory`, and `Languages` are kept.

### ContactField

**Table:** `contact_fields`

Contact fields allow humans to share different types of contact information with per-field visibility controls. Default labels use the viewer's current culture, including legacy email fields; custom Other labels are preserved verbatim. Profile cards and contact lookup details receive the same localized labels.
Profile-card contact and email visibility badges reuse the email grid's localized privacy labels in all six cultures; visibility filtering is unchanged.
The private-email label on self-profile cards uses the same six-culture email label as public addresses.

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

Per-user email addresses (login, verified, notifications). `UserEmail` has no `User` nav.

Self and admin email-grid mutation rejections remain Warning logs with the member, email-row id, and reason, without exception stacks. Unexpected exception types propagate to normal error handling.

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → User (Cascade) — **FK only**, no nav |
| Email | string (256) | Required. **Mutation has exactly one path:** `IUserEmailService.ReconcileOAuthIdentityAsync(userId, provider, providerKey, claimEmail, claimEmailVerified, ...)`, called only by `ExternalLoginService` (the OAuth sign-in callback's decision service, dispatched to from `AccountController.ExternalLoginCallback`). See [`memory/architecture/email-mutation-paths.md`](../../../../memory/architecture/email-mutation-paths.md). No admin flow, profile UI flow, or sync job rewrites this field; renames self-heal on the user's next Google sign-in |
| IsVerified | bool | Required |
| Provider | string? (64) | OAuth provider that owns this row when the user signed in via OIDC ("Google" today; future Apple/Microsoft). Null when no OAuth identity is linked. Single-row-per-(Provider, ProviderKey) is service-enforced |
| ProviderKey | string? (256) | OAuth subject/key (OIDC `sub`) for the linked identity. Stable across Google Workspace email renames. `(Provider, ProviderKey)` is the only legitimate match key for rewriting `Email` (via `UserEmailRepository.UpdateEmailAsync`) |
| IsGoogle | bool | **Frozen** (Users decomposition commit 15): the Google Workspace identity moved to GoogleIntegration's `google_workspace_identities`. No writes; read only by GoogleIntegration's legacy fallback and `google.workspace-identities` copy via the obsolete `UserEmailInfo.IsGoogle`. Dropped once the copy reports done in production. |
| IsPrimary | bool | Exactly one verified email per user is the system-notification target. Service-enforced via `EnsurePrimaryInvariantAsync`; column persists under legacy name `IsNotificationTarget` per `no-column-drops-for-decoupling.md` |
| Visibility | ContactFieldVisibility? | Stored as string (max 50); null hides the email from profile view |
| VerificationSentAt | Instant? | Last time a verification email was sent (rate limiting) |
| CreatedAt / UpdatedAt | Instant | Maintained by `UserEmailService` |

OAuth reconciliation translates only a collision on `IX_user_emails_Email` into the existing verified-email race exception; unrelated persistence failures retain their original exception and follow the callers’ normal failure handling.

**Indexes:** `UserId`; **unique partial index** on `Email` filtered to `IsVerified = true` (Postgres `"IsVerified" = true`). The cross-account check is service-enforced (`VerifyEmailAsync`, `AdminMarkVerifiedAsync`, the OAuth reconcile); the index is a unique index on an editable string, forbidden by `memory/architecture/unique-constraints-ids-only.md`, and its drop is recorded in `Docs/debt.yml`.

No shadow columns (`IsOAuth` and `DisplayOrder` are gone from the table; `HUM0001` rejects references to either). Display sorting is alphabetical on `Email`.

### CommunicationPreference (legacy, frozen)

**Table:** `communication_preferences`

**Legacy, no longer written.** Communication preferences moved to Email's `email_communication_preferences` (Users decomposition commit 14; see `Humans.Email/Docs/Email.md`). This table stays until the `email.communication-preferences` copy reports done in production and a drop PR removes it.

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → User (Cascade) — **FK only**, no nav |
| Category | LegacyMessageCategory | Frozen copy of Email's `MessageCategory` (same names and values). Stored as string (max 50) |
| OptedOut | bool | true = opted out of email for this category |
| InboxEnabled | bool | Default true |
| UpdatedAt | Instant | Last change |
| UpdateSource | string (100) | Last writer |
| SubscribedAt | Instant? | First opt-in |

The rows still load into `UserInfo.CommunicationPreferences` (`[Obsolete(HUM_USER_COMMPREFS)]`), read only by Email's `CommunicationPreferenceService.LegacyRows`. Users no longer folds the table on account merge — Email folds the effective preferences. Erasure still deletes the rows.

**Unique constraint:** `(UserId, Category)`. **Indexes:** `UserId`.

Reads do not create preference rows. Missing categories use their natural defaults: opted-in (`OptedOut = false`) except Marketing, which defaults to opted-out. Explicit updates create rows as needed; historical `Default` rows remain readable. System and CampaignCodes are always on (cannot be opted out).

### VolunteerHistoryEntry (CV Entry)

**Table:** `volunteer_history_entries`

Sub-aggregate of Profile — no separate service. Written through `IProfileEditorService.SaveProfileAsync` (which calls `IUserManagement.SaveProfileAsync` to persist fields and CV together); read via `UserInfo.VolunteerHistory`.

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

### Frozen legacy (awaiting drop)

Nothing here is written. Each row is read only by its new owner's fallback and data-migration copy (through an `[Obsolete]` field on `UserInfo`/`ProfileInfo`) and by Users' GDPR export/erase, until the owner's retirement PR and then a drop PR remove it. The production `done` reading of the migration at `GET /api/data-migrations` is the drop's approval (`memory/architecture/no-drops-until-prod-verified.md`). Ledger rows: retirement in `docs/architecture/debt-ledger.yml` (CENTRAL-89 to CENTRAL-95, CENTRAL-97), drops in `Docs/debt.yml`.

| Legacy object | Replaced by | Migration key | Drop row |
|---|---|---|---|
| `profiles.Iban` | Finance `finance_payment_details` | `finance.iban-to-payment-details` | USERS-15 |
| `profiles` food columns (`DietaryPreference`, `Allergies`, `AllergyOtherText`, `Intolerances`, `IntoleranceOtherText`) | Cantina `cantina_dietary_profiles` | `cantina.profile-dietary` | USERS-16 |
| `profiles.MembershipTier` | Governance `governance_membership_tiers` | `governance.profile-tier` | USERS-17 |
| `profiles` review columns (`ConsentCheck*`, `RejectionReason`, `RejectedAt`, `RejectedByUserId`, `IsApproved`) and the `ConsentCheckStatus` index | Consent `consent_reviews` | `consent.profile-review` | USERS-18 |
| `event_participations` table | Tickets `tickets_event_participations` | `tickets.event-participations` | USERS-19 |
| `communication_preferences` table | Email `email_communication_preferences` | `email.communication-preferences` | USERS-20 |
| `user_emails.IsGoogle`, `user_emails.GoogleEmailStatus` | GoogleIntegration `google_workspace_identities` | `google.workspace-identities` | USERS-21 |
| `users.LastConsentReminderSentAt` | Consent `consent_reminders` | `consent.reminder-stamps` | USERS-22 |
| `users.UnsubscribedFromCampaigns`, `users.SuppressScheduleChangeEmails`, `users.GoogleEmailStatus` | none (dead, no migration) | none | USERS-12 to USERS-14 (per-case approval) |

The `Legacy*` enums (`LegacyMembershipTier`, `LegacyConsentCheckStatus`, `LegacyParticipationStatus`, `LegacyParticipationSource`, `LegacyMessageCategory`, `LegacyGoogleEmailStatus`) die with their retirement PRs.

### LegacyMembershipTier

Frozen copy of Governance's `MembershipTier` (`Volunteer` 0, `Colaborador` 1, `Asociado` 2) typing the legacy `profiles.MembershipTier` column, stored as string via `HasConversion<string>()`. The live enum and its meaning live in Governance.Contracts.

### LegacyParticipationStatus, LegacyParticipationSource

Frozen copies of Tickets' `ParticipationStatus` (`NotAttending` 0, `Ticketed` 1, `Attended` 2, `NoShow` 3) and `ParticipationSource` (`UserDeclared` 0, `TicketSync` 1, `AdminBackfill` 2) typing the legacy `event_participations` columns, stored as string. Tickets maps them by name; deleted with the `tickets.event-participations` retirement PR.

### LegacyConsentCheckStatus

Frozen copy of Consent's `ConsentCheckStatus` (`Pending` 0, `Cleared` 1, `Flagged` 2) typing the legacy `profiles.ConsentCheckStatus` column, stored as string via `HasConversion<string>()`. The live enum and its meaning live in Consent's contracts; deleted with the `consent.profile-review` retirement PR.

### LegacyMessageCategory

Frozen copy of Email's `MessageCategory` (System 0, EventOperations 1, CommunityUpdates 2, Marketing 3, Governance 4, CampaignCodes 5, FacilitatedMessages 6, Ticketing 7, VolunteerUpdates 8, TeamUpdates 9) typing the legacy `communication_preferences.Category` column, stored as string. The live enum and its meaning live in `Humans.Email.Contracts`; deleted with the `email.communication-preferences` retirement PR.

## Routing

Self-service profile functionality lives under `/Profile`, split by shape across `ProfileController` (own profile), `ProfileEmailsController` (own and admin email grids) and `ProfileViewController` (other members' profiles, search), each `[Authorize]` class-wide; human administration lives under `/Users/Admin`. Own-profile maintenance and own-email actions are individually exempt from the membership-state gate, so suspension, rejection, or pending deletion does not block self-service. `Deleted` and `Merged` accounts cannot use recovery exemptions, including name entry: a lingering session reaches only the status wall, session/language routes, and existing anonymous endpoints. Cleared names never send these tombstones back to onboarding. Other-profile views, authenticated popovers, search, messaging, and admin email actions require `Active` state as well as their existing action-specific permissions. Public pictures, public coordinator popovers, and token-based email verification retain `[AllowAnonymous]`. GET unless noted; POSTs carry an anti-forgery token.

| Route | Purpose |
|-------|---------|
| `/Profile` | Redirect to `/Profile/Me` |
| `/Profile/Me` | View own profile |
| `/Profile/Me/Edit` | GET/POST — edit own profile |
| `/Profile/Me/Emails` | Email management grid |
| `/Profile/Me/Emails/Add` | POST — add an address and send its verification link |
| `/Profile/Me/Emails/Verify` | `[AllowAnonymous]` — consume a verification token |
| `/Profile/Me/Emails/SetPrimary`, `/SetVisibility`, `/Delete` | POST — per-row flag/visibility edits and delete (`UserEmailOperations.Edit`) |
| `/Profile/Me/Emails/ClearPrimary` | POST — self-recovery: drop a single row's `IsPrimary` flag (only surfaced in UI on N>1 violation; auth is self-or-admin). The Google column's Set form posts to GoogleIntegration's `/Google/WorkspaceEmail` |
| `/Profile/Me/Emails/Link/{provider}`, `/Profile/Me/Emails/Unlink/{id}` | POST — start an OAuth link for the signed-in user / unlink a provider-attached row |
| `/Profile/Me/LinkedAccounts/Unlink` | POST — unlink by `(provider, providerKey)`; enforces the auth-method invariant server-side |
| `/Profile/{id}/Admin/Emails` | Per-user email diagnostic (`UserEmailOperations.Edit`) |
| `/Profile/{id}/Admin/Emails/Add`, `/SetPrimary`, `/SetVisibility`, `/Delete`, `/Unlink/{emailId}` | POST — admin mirrors of the self-service email edits |
| `/Profile/{id}/Admin/Emails/ClearPrimary` | POST — admin remediation: drop a single row's flag without auto-promoting a successor. The admin Google column posts to GoogleIntegration's `/Google/Admin/{id}/WorkspaceEmail` |
| `/Profile/{id}/Admin/Emails/AddVerified` | POST (`PolicyNames.AdminOnly`) — add an address already verified |
| `/Profile/{id}/Admin/Emails/Verify` | POST (`PolicyNames.AdminOnly`) — mark a pending plain UserEmail row verified without consuming a token; creates a merge request when the address is already verified on another account |
| `/Shifts/ShiftInfo` | Shift preferences (skills, work style, languages, preferred shift tags) — served by Shifts' `ShiftProfileController`, not this section; the Edit form carries only a link to it |
| `/Profile/Me/Medical` | GET/POST — the member's own medical conditions (Art. 9; trimmed, blank saves null). The `medical` things-to-do links here while it is empty and dietary is still open ([spec](../../Humans.Cantina/Docs/features/dietary-nudge.md)). Dietary answers are Cantina's `/Cantina/Dietary`. |
| `/Email/CommunicationPreferences` (+ `/Update`) | Served by Email's `CommunicationPreferencesController` (Users decomposition commit 14); the profile hub links to it |
| `/Consent/MyData` | My Data page — served by Consent's `MyDataController`; Users supplies the deletion timestamps through `UserInfo` and links to it from the profile hub |
| `/Gdpr/RequestDeletion` | POST — start the 30-day deletion countdown — served by Gdpr's `MemberDataController`; the My Data page's form posts to it |
| `/Gdpr/DownloadData` | GDPR Article 15 JSON export download — served by Gdpr's `MemberDataController`; linked from the My Data page |
| `/Email/Outbox` | Own email outbox — served by Email's `MemberOutboxController`, linked from the Profile page |
| `/Profile/{id}` | View another human's profile |
| `/Profile/{id}/Popover` | Quick profile popup |
| `/Profile/{id}/PublicPopover` | `[AllowAnonymous]` — public-page coordinator popover; 404 unless the target coordinates a public-page team ([spec](features/public-coordinator-popover.md)) |
| `/Email/SendMessage/{id}` | GET/POST — send facilitated message; served by Email's `FacilitatedMessageController` (the profile card links to it). Send as team is Teams' `/Teams/{teamId}/SendMessage/{id}`, offered by Teams' `Profile` user part |
| `/Profile/Search` | People search |
| `/Profile/Picture` | `[AllowAnonymous]` — profile picture bytes |
| `/api/profiles/search` | API people search |
| `/api/profiles/burner-name-count` | Live burner-name collision count, excluding the caller ([spec](features/burner-name-collision-warning.md)) |
| `/api/profiles/by-userid/{userId}` | Profile lookup by user id |
| `/Users/Admin` | Admin list of all humans; `AdminAll` searches burner/legal names, public fields and admin-only emails/contact fields. Search ignores surrounding whitespace, including the fallback for accounts without profiles |
| `/Auth/Admin/Roles` | System-wide role-assignment roster, filterable by role (`HumanAdminBoardOrAdmin`) — served by Auth's `RoleAssignmentsController` (`role_assignments` is Auth's); Auth adds the sidebar entry to the Users admin group |
| `/Users/Admin/{id}` | Admin detail view; the read-only Communication preferences card is Email's `AdminDetail` user part (Users decomposition commit 14); the Google Sync and @nobodies.team cards are GoogleIntegration's `AdminDetailSidebar` user part (commit 15) |
| `/Email/Admin/{id}/Outbox` | Admin view of person's outbox — served by Email's `MemberOutboxController`; the card linking to it is Email's `AdminDetail` user part |
| `/Users/Admin/{id}/Suspend`, `/Users/Admin/{id}/Unsuspend` | POST — suspend / unsuspend member |
| `/Users/Admin/{id}/Reject` | POST — reject signup |
| `/Auth/Admin/{id}/Roles/Add` (GET/POST), `/Auth/Admin/{id}/Roles/{roleId}/End` (POST) | Role management — served by Auth's `RoleAssignmentsController`; the Role Assignments card on the admin detail page is Auth's `AdminDetail` user part, and both redirect back to `UsersAdmin/AdminDetail` |
| `/Users/Admin/Audience` | `AdminOnly` — audience-segmentation diagnostic (profile-completion × ticket-purchase); a cancelled request propagates rather than becoming an error redirect |
| `/Users/Admin/{id}/Purge` | POST (`AdminOnly`, non-Production only) — purge a human |

Admin-only flows for the section's cross-account hygiene (the `/Profile/Admin/*` routes pre-date `memory/architecture/no-admin-url-section.md` — not yet moved to `/<Section>/Admin/*`):

| Route | Purpose |
|-------|---------|
| `/Users/Admin/AccountMerges` | Unified account-merge queue — pending merge requests **and** detected duplicate pairs (`UsersAdminAccountMergesController`, `AdminOnly` — see [Part 1 — Users / Identity](#part-1--users--identity)). Admin picks the survivor; the other account is folded in and tombstoned. |
| `/Users/Admin/AccountMerges/Merge` | POST — merge a detected duplicate pair (no request row) |
| `/Users/Admin/AccountMerges/{requestId}/Merge`, `/Dismiss`, `/Close` | POST — accept a pending request with the chosen survivor / dismiss it (`Rejected`) / close a request whose accounts are already merged |
| `/Debug/Users` | Served by Debug's `UsersDebugController`; the all-users debug grid reads `IUserService.GetAllRawUserInfosAsync`. See `Debug.md` |
| `/Profile/Admin/EmailProblems` | List UserEmail invariant violations across all accounts (`ProfileAdminController`, `AdminOnly`) |
| `/Profile/Admin/EmailProblems/DeleteOrphanEmail` | POST — delete a single orphan UserEmail row |

Users' one-off copies are **data migrations** (`SectionDataMigrations : ISectionDataMigrations` at the project root), listed and applied on Debug's `/Debug/DataMigrations` (`AdminOnly`, review every pending row → confirm → `DataMigrationApplied` audit) and reported, counts only, by the anonymous `/api/data-migrations`. Each resolves Users' own services, so the copy stays here; each is idempotent and its status lists exactly what its apply can fix. They replace the retired Temp screens `/Profile/Admin/Backfill`, `/Profile/Admin/NameBackfill`, `/Profile/Admin/PictureMigration` and `/Profile/Admin/EmailProblems/BackfillLegacyEmails`.

| Key | Copies | Per-row audit |
|-----|--------|---------------|
| `users.stub-profiles` | A Stub Profile for every live (non-tombstone) user without one, via `IUserManagement.EnsureStubProfileAsync` | `StubProfileBackfilled` |
| `users.names-onto-user` | BurnerName/legal name from `Profile` onto `users`, via `IUserNameSyncService.SyncAllAsync` | `UserNamesBackfilled` |
| `users.profile-pictures-to-filesystem` | DB-recorded custom pictures missing on disk, by driving `IProfilePictureService.GetProfilePictureAsync`'s migrate-on-read | — (the run's `DataMigrationApplied` only) |
| `users.legacy-identity-emails` | A verified `UserEmail` row from the legacy `User.Email` Identity column, via `IEmailProblemsService.BackfillLegacyIdentityEmailsAsync` (the Email Problems page still lists these rows and links to the migration) | `LegacyIdentityEmailBackfilled` |
| `users.legacy-gdpr-tombstones` | Re-runs today's erasure (`IAccountDeletionService.AnonymizeExpiredAccountAsync`, every section, no email) on rows erased before the tombstone email existed, so they carry `deleted-<id>@deleted.local` and the sentinel BurnerName. Status shows ids only (nobodies-collective/Humans#1742) | `AccountAnonymized` |

Email Problems remediation buttons use the shared `data-confirm` handler: each click prompts once, and cancellation prevents submission.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View and edit own profile, manage own emails, manage own contact fields, upload profile picture, set notification and communication preferences, request data export (GDPR Article 15), request account deletion |
| Any active human | View other active humans' profiles (contact fields restricted by per-field visibility). Send facilitated messages to other humans. Search for humans |
| Coordinator (any team) or PrivilegedSignupApprover | On another human's `/Profile/{id}`, view the **Sent messages** panel — a history of in-platform messages sent to that human (`AuditAction.FacilitatedMessageSent`, up to 50 entries), rendered by `<vc:audit-log layout="table">` with `column-labels` carrying the page's own `Common_Date`/`Common_Sender`/`Common_Preview` — the controller decides visibility, AuditLog owns the read, Profile owns the copy. Not shown on own-profile views. |
| HumanAdmin, Board, Admin | View any profile with full detail. Manage humans via admin pages (suspend, unsuspend, approve volunteer, reject signup, view audit log; role assignments are added and ended on Auth's pages, linked from the admin detail page). (Membership tier changes go through tier applications in Governance, not the profile admin page.) |
| Admin | Review duplicate-account candidates and approve/reject `AccountMergeRequest`s at the unified `/Users/Admin/AccountMerges` queue (`PolicyNames.AdminOnly`; **Users** section — see [Part 1 — Users / Identity](#part-1--users--identity). |
| Admin (non-production only) | Purge a human and all associated data |

## Invariants

- The `UserEmails` GDPR export includes each owned address’s Google-selection flag, Google sync status, and UTC creation, update and nullable last-verification-email timestamps. Existing export keys retain their names; other users’ addresses are excluded.

- The live burner-name collision warning invalidates pending responses and hides the previous name’s warning on every input, before debounce; clearing the field cannot revive an older result.

- Member communication-preference saves reject redirects. An expired or revoked session rolls back the changed checkbox and shows the existing failure feedback instead of confirming a save.

- People-search bio and contribution-interest excerpts preserve complete Unicode characters at both context boundaries.
- Profile API search, burner-name count and user-id lookup carry request cancellation through current-user resolution as well as their subsequent reads.
- Profile shift-history and sent-message visibility checks carry request cancellation through coordinator checks and no-show history reads.
- Human-picker prefills pair the resolved name with the resolved account ID, so an archived account ID is never reposted as the selected value.

- Outbox, privacy and medical GETs carry request cancellation through their current-user reads and, for outbox, email history. An abandoned request propagates cancellation.

- The medical page, its save feedback and the profile's dietary/medical links render in all six supported cultures.

- Users no longer writes food answers: `/Profile/Me/Edit` links to Cantina's dietary form instead of embedding it, and no save path touches the legacy food columns ([dietary nudge](../../Humans.Cantina/Docs/features/dietary-nudge.md)).
- Profile-editor cross-field guards return ordinary refusal keys before any profile, picture or CV write. The profile form localizes them using existing messages; refused saves stop language/contact saves and consent checks. Unexpected validation diagnostics are logged and replaced with generic translated feedback.
- Every authenticated human can edit their own profile regardless of membership status (available during onboarding). Profile fields and their nested contact-field, Burner CV, and language forms, medical details, facilitated messages, and email-add forms use shared validation messages in all six cultures; their input limits are unchanged.
- Facilitated member messages use the recipient’s supported preferred language, with English fallback for blank, malformed or unsupported preferences. Team reply-to, contact sharing, opt-out checks and audit behavior are unchanged.
- Profile contact rows require an explicit supported contact type and visibility before any profile save. Missing visibility cannot default a private contact to AllActiveProfiles, and the retired email contact type cannot be authored. Historical email contacts appear read-only in the owner editor and survive ordinary contact replacement, including stale attempts to relabel their IDs. Only explicitly selected legacy IDs on that owner's profile are removed; other profiles' IDs cannot be deleted. Validation failures reload these rows from the owner's cached profile. Existing visibility, GDPR export and account-deletion paths still apply.
- Contact field visibility is enforced per-field: a human viewing their own profile sees everything. Board members see everything. Coordinators see CoordinatorsAndBoard-level and below. Shared-team members see MyTeams-level and below. Other active members see only AllActiveProfiles fields. Each call derives permissions for its supplied viewer; a reused service cannot carry another viewer’s roles or team membership into the result.
- Birthday stores month and day only — never year. UI text uses "birthday", not "date of birth". The editor’s month names follow the selected UI language; numeric option values and English input parsing stay unchanged. Displayed location coordinates also follow the UI culture; hidden coordinate form values retain English input formatting.
- Membership tier (Volunteer, Colaborador, Asociado) is owned by Governance, not the profile and not a role assignment. Initial profile setup shows the tier descriptions and links to `/Governance/Applications/Create` (new tab); the profile form carries no tier fields and submits no application.
- The Consent Coordinator review (status, approval, rejection record) is Consent's (`consent_reviews`, Users decomposition commit 12) and gates nothing here: Volunteer admission is name + required consents. Users holds only the resulting `UserState.Rejected`. The admin detail page's consent-check badge and rejection alert are Consent's `AdminDetail` user part.
- App access is gated by the stored `User.State` (`UserState`: Bare/Active/DeletePending/Suspended/Rejected/Deleted/Merged/AdminSuspended). `Profile` carries no state column.
- Suspension notices use the recipient’s supported preferred language (English fallback), including the title, action, and consent-expiry email reason. Administrative suspension reasons are excerpted only when needed to fit the 2,000-character notification body; the full original reason remains in the suspension audit entry. Admin and consent-expiry notices reuse the account-status resources; suspension audit entries keep their existing text and actor.
- Profile deletion request sets `User.DeletionRequestedAt` and `User.DeletionScheduledFor = now + 30 days` on the User record. Team memberships and governance role assignments are revoked immediately. Actual data purge is deferred to a background job. The request result, confirmation email, privacy page and deletion status wall report the later of the grace deadline and any ticket-holder event-hold floor, matching the job’s eligibility check. The existing `UserInfo` read model exposes this derived value as `EffectiveDeletionDate`.
- Data export returns all personal data as a JSON download (GDPR Article 15), using Gdpr’s shared `GdprExportSerializer` for the same document format as the guest route. The `Account` slice includes the stored lifecycle state and nullable post-event deletion hold alongside the existing deletion request and grace-period timestamps. The communication-preference slice includes the stored first-subscription timestamp as UTC ISO 8601, or null when unknown. If the complete export fails, it is logged and the profile route returns the user to Privacy with a generic error instead of emitting a partial file or an unhandled response; a request-abort cancellation propagates instead. `AccountMergeService` is an `IUserDataContributor` per design-rules §8a (the `AccountMergeRequests` slice); the profile slices are emitted by `UserService`. `ProfileService` implements no contributor interface. The orchestration lives in `GdprService`. Users resolves archived request IDs to the survivor before exporting moved rows. The `MergedProfiles` collection includes every archived profile in the transitive merge chain, with its user ID and the existing profile export shape, including retained dietary and medical data; unrelated profiles are excluded. The existing deletion fan-out erases those archived profiles too.
- Profile pictures are stored on the filesystem via `IFileStorage` — the only store; there is no DB fallback column. `GetProfilePictureAsync` checks `ProfilePictureContentType` as the GDPR gate: null → 404, even if a stale file exists on disk. The edit route permits 21 MB for multipart overhead; uploaded images are validated against an allowed-content-type set (JPEG, PNG, WebP, HEIC/HEIF, AVIF) and a 20 MB file cap, then oriented and resized by `ProfilePictureProcessor` to a long-side of 1000 px and re-encoded as JPEG before persistence. Upload metadata (including EXIF location tags and comments) is stripped after applying orientation; the ICC color profile is preserved. This applies to new uploads; existing pictures are not rewritten. The section-only profile save writes each upload to a new versioned key (`uploads/profile-pictures/{profileId}-{guid}{.ext}`, stored in `ProfilePictureStorageKey`) before committing metadata, so a failed save leaves the previous picture's bytes intact. Cleanup verifies the committed picture reference without request cancellation: only a confirmed unreferenced upload is deleted; committed or unverifiable uploads are retained, and the original save failure propagates. A null stored key means the legacy key `uploads/profile-pictures/{profileId}{.ext}`. GDPR erasure deletes whichever key the profile resolves to. Non-cancellation file-write failures retain the previous picture metadata while allowing other profile edits to save; cancellation aborts the save. The editor deletes the previous file only after the save commits and its key differs from the current one. Account merges also delete the archived profile’s resolved picture file after the profile fold commits, preserving the survivor’s picture. Cleanup ignores request cancellation and logs failures; the content-type gate keeps stale files inaccessible.
- `CachingUserService` (Singleton) and `IUserInfoSliceRefresher` must resolve to the **same** instance — both registrations point to the single decorator. Two instances would split the `ConcurrentDictionary<Guid, UserInfo>` cache and silently lose invalidations.
- Purging a human permanently deletes the account and all associated data, including severing the OAuth link so the next Google login creates a fresh account. Purge is disabled in production environments. No one can purge their own account.
- Duplicate account detection applies gmail/googlemail equivalence when scanning for address collisions. User and user-email lookup services share the section-internal `EmailLookupAliases` for Gmail/Googlemail query variants.
- `AccountMergeService` writes and `DuplicateAccountService` reads go through this section's repositories and `IUserService` — never through cross-section `DbSet` reads.
- `AccountMergeService.AcceptAsync` is the **fold-into-target** orchestrator: it re-FKs every owning section's user-scoped rows from source to target via per-section `Reassign…ToUserAsync` methods, then tombstones the source User row (sets `MergedToUserId` + `MergedAt` via `IUserManagement.AnonymizeForMergeAsync`) — it does NOT delete or wipe the source. Append-only history (audit log, consent records, budget audit log) stays at source by design and is surfaced via chain-follow reads.
- Public profile cards hide hidden team memberships and hidden parent-name prefixes on visible child memberships. Self/admin cards retain both, including the existing self-profile exception for hidden memberships.
- The preferred-language flag (rendered next to a person's name on `ProfileCard` and `_HumanPopover`) is visible only to `HumanAdmin` / `Board` / `Admin` viewers — general active humans see other people's profile cards and popovers without it. Self-view is unaffected (the flag isn't shown there in the first place; preferred language is editable on `/Profile/Me/Edit`). `ProfileCard`'s `nobodies.team` account badge and public email label use the Users resource set. Its profile, permissions, contact fields, email, team, membership, and messaging-preference reads all receive `HttpContext.RequestAborted`; the standalone membership-tier and `nobodies.team` badges do too. The admin-only Users widget gallery passes it to its current-user lookup as well.
- Public coordinator popovers use the shared coordinator label in all six cultures, preserving the team name and the existing public-coordinator gate. Profile popover requests reject redirects, so an expired session’s login page is never embedded or cached as profile content. Network/HTTP failures dispose the loading spinner and allow a later hover to retry; 404 continues to suppress the popover without another probe.
- The authenticated `_HumanPopover` (`GET /Profile/{id}/Popover`) also renders the person's active-season camp name + camp roles, sourced from `ICampService.GetCampUserInfoAsync`. This block is gated to `AnyAdminRole` viewers (admin-only) and is omitted when the person is not an Active member of any camp this season.
- Profile-less users (legacy contact imports whose Stub Profile has not yet been materialized by the `users.stub-profiles` data migration) render in `GET /Profile/{id}/Popover` as a sparse card showing display name, best-available verified email, and a localized "imported · no profile yet" badge — the endpoint never returns 404 for a known `User`. Truly unknown user ids still return 404. In the fallback (no-profile) branch the email is derived directly from canonical `UserEmail` rows (verified primary, then any verified) — `User.Email` is not read on that branch because it silently falls back to the legacy Identity column when `UserEmails` is not loaded (see `User.Email` SILENT-FALLBACK FOOTGUN). The full-profile branch still reads `popoverUser.Email` (pre-existing); same footgun applies and is tracked separately.
- Facilitated messaging (`/Email/SendMessage/{id}`) is a **conduit, not a mailbox** — Humans relays one-off emails between humans and stores nothing: no message persistence, no threading, no attachments. (Deliberate contrast with Feedback, which *does* persist a conversation thread.) The body is edited through the shared `_EmailComposer` (Base) and rendered through `SanitizedMarkdownRenderer` (Markdown, `https://`-only images); max 2000 chars. The recipient's address is never disclosed to the sender. The pages, the relay, the audit entry and the team Reply-To rules (`TeamMessageOptionsProvider`) live in Email (`FacilitatedMessageController`); the profile card here only shows the "Send a message" button, hidden when the recipient opts out. A coordinator may send with `Reply-To` set to a team Google Group only when `TeamMessageOptionsProvider` (in Email, an orchestrator over `ITeamService` and `ITeamResourceService`, no tables) offers the team: the viewer coordinates an active team with a Google Group prefix, and that team has an active `Group` resource with a successful sync, no error, and a `Url` equal to the group's `https://groups.google.com/a/{domain}/g/{prefix}` (the one field both GoogleIntegration link paths write consistently). `FacilitatedMessageController` only looks the requested team up in that list, on GET and on POST alike, and Email's `TeamMessageButtonsViewComponent` (a `Profile` user part) sorts it for display. Personal sends set `Reply-To` to the sender's notification email only when the sender ticks "include my contact info". Every send uses `AuditAction.FacilitatedMessageSent`; team sends name and link the team in the audit entry without copying its address into the human's audit description.
- `<vc:user-parts slot user-id view-mode>` (Base's `UserPartsViewComponent`) collects every active section's `IUserPart` descriptors for a named slot (`UserPartSlots`), orders them by weight, and invokes each component `Type` with a `UserPartArgs(userId, viewMode)`; a contributor that throws is logged and skipped, and contributing components own their data and any visibility beyond the host's gate. Hosted at: `user-profile` (own/other profile main column, Events is the reference contributor; Campaigns' `MyCodesViewComponent`, own profile only); `user-profile-sidebar` (profile right sidebar, own-profile only); `user-admin-detail` (AdminDetail main column — Calendar's `UserCalendarViewComponent`, Auth's `RoleAssignmentsCardViewComponent`, Campaigns' `CampaignCodesViewComponent`, Email's `OutboxCardViewComponent`); `user-admin-detail-sidebar` (AdminDetail right sidebar — Tickets' `TicketHoldingsViewComponent`, gated to non-Public view modes); `board-vote-applicant` / `onboarding-review-applicant` (Governance's and Onboarding's applicant detail pages — Users' own `ProfileCardViewComponent`, Admin view mode); `ticket-transfer-party` (Tickets' transfer admin detail, sender and receiver — Users' `HumanSummaryViewComponent`). Users contributes the last three via `Humans.Users/SectionUserParts.cs`. The profile communication-preferences panel uses the Users resource set, including its non-editable "Always on" state, and passes the request-abort token to both preference and ticket-holdings reads. `HumanSummaryViewComponent` also passes it through its profile and team reads.

## Negative Access Rules

- No one **can** read or edit another member's medical conditions through `/Profile/Me/Medical` — it takes the user from the principal and has no target-user parameter. (The dietary redirect-and-replay flow moved to Cantina's `CantinaProfileController`, which keeps the same own-user-only rule.)
- Regular humans **cannot** view suspended profiles.
- Regular humans **cannot** edit another human's profile.
- Regular humans **cannot** see contact fields above their access level on other humans' profiles.
- Non-active humans **cannot** use authenticated other-profile views, popovers, search, messaging, or admin email actions, even if they retain an admin or Board role. Public picture and coordinator-popover routes remain available. An active viewer can still see a suspended target's basic identity through authenticated popovers.
- Any Admin **cannot** purge their own account.
- Purge **cannot** run in production environments (gate on `IHostEnvironment` inside `AccountDeletionService.PurgeAsync`).
- Admins **cannot** establish a new OAuth link on a user's behalf — there is no admin `Link` action under `/Profile/{id}/Admin/Emails/*`. Google authenticates whoever is at the keyboard, and admins must never hold user credentials; linking requires the target user's own session (`POST /Profile/Me/Emails/Link/{provider}`). Admins may `Unlink` existing provider-attached rows (that operates on already-stored data, no OAuth flow).

## Triggers

- When Onboarding rejects a signup, `IUserWrite.SetRejectedAsync` stores `UserState.Rejected` (Consent records the reason, time and reviewer).
- When a human requests account deletion, team memberships and governance roles are revoked immediately and `User.DeletionScheduledFor` is set to `now + 30 days`. `ProcessAccountDeletionsJob` runs the actual anonymisation via `IAccountDeletionService.AnonymizeExpiredAccountAsync` after the scheduled date passes. The deletion-scheduled email formats that UTC date through the shared display formatter in the recipient’s language. Users email templates carry their resolved culture into Email’s wrapper so the branded footer uses the same language as the body.
- Token verification and admin manual verification share `UserEmailService`'s conflict-to-merge path. A pending email already verified on another row creates an `AccountMergeRequest` (status `Pending`) for admin review instead of marking the email verified; an existing pending request for that email row is reused.
- When an `AccountMergeRequest` is accepted, `IAccountMergeService.AcceptAsync` orchestrates a **fold-into-target** as an ordered fan-out with no wrapping transaction (see Part 1, "A merge is atomic by ordering"): it runs every registered `IUserMerge` implementation (`merger.ReassignAsync(source, target, …)`), each owning section re-FKing its own user-keyed rows source→target; then calls `IUserManagement.AnonymizeForMergeAsync` last to tombstone the source User row (sets `MergedToUserId` + `MergedAt`, locks out login). The source row is **not** deleted — it stays as a redirect for chain-follow reads on append-only history.
- When `DuplicateAccountService` surfaces a pair of live accounts sharing an address, it writes one `DuplicateAccountFlagged` audit entry (job `DuplicateAccountScan`; entity the pair's lower id, related entity the higher) the first time the scan sees the pair, and never again for that pair.
- When a profile field changes through any owning service, `CachingUserService` reloads the affected `UserInfo` dict entry from the section's repositories. After a successful write that reload does not inherit the request cancellation token, so a disconnected client cannot leave the singleton snapshot stale.

## Cross-Section Dependencies

- **Consent:** `IConsentManagement` — consent-check status gating depends on all required document versions having active consent records.
- **Teams:** `ITeamService` — active membership equals membership in the Volunteers system team. Profile activation triggers addition.
- **Onboarding:** none (one-directional — `OnboardingService` consumes `IUserService`, never the reverse). The consent-check threshold (Consent's review `Status → Pending` + Consent Coordinator notification) is director-level work and lives on `IOnboardingIntake.SetConsentCheckPendingIfEligibleAsync`; controllers call it as a peer call after `IProfileEditorService.SaveProfileAsync`.
- **Cantina:** `ICantinaService` (project reference; acyclic — Cantina reaches only `Users.Contracts`) for the medical things-to-do. Cantina owns the food answers, their form and nudge, and its own export/erase/merge.
- **Finance:** none at compile time. The admin detail page's Payment Details card and its reveal (`/Finance/Admin/Members/{id}/RevealIban`) are Finance's, contributed to the `AdminDetailSidebar` user-part slot; Users only keeps the legacy `profiles.Iban` column (see Profile).
- **Google Integration:** owns the member's Google Workspace identity (`google_workspace_identities`, Users decomposition commit 15) — `IGoogleIntegrationService.GetWorkspaceIdentityAsync` is the address Google Groups and Drive sync target. Users reports addresses leaving an account via `IGoogleIntegrationWrite.ClearIfAddressAsync`; the Emails grid's Google column, the sync-rejected banner, the admin detail's Google Sync / @nobodies.team cards and the @nobodies.team badge are GoogleIntegration view components.
- **Users/Identity:** `IUserService.GetUserInfosAsync` — display data for cross-domain nav stitching. `IUserManagement.AnonymizeForMergeAsync` — invoked by `AccountMergeService.AcceptAsync` to tombstone the source User during fold; AspNetUserLogins re-FK is handled by the Users section's own `IUserMerge` implementation (`IUserRepository.ReassignLoginsToUserAsync`); event participation by Tickets' `IUserMerge`.
- **Account merge fold fan-out:** `IAccountMergeService.AcceptAsync` fans out across every registered `IUserMerge` implementation (`ReassignAsync`), each committing independently. Each owning section — Tickets, Teams, Shifts, Governance, Campaigns, Camps, Notifications, Email, Feedback, Role assignments, and the Profiles sub-aggregates (Profile / UserEmail / ContactField) — registers its own `IUserMerge` impl and re-FKs its user-scoped rows source→target, rather than the orchestrator naming each service.

## Architecture

**Owning services:** `ProfileService` (`IProfilePictureService` — read-only pictures and migration snapshots), `ProfileEditorService` (`IProfileEditorService` — validates and orchestrates profile-edit saves, delegating the actual persist to `IUserService`), `ContactFieldService`, `UserEmailService`, `EmailProblemsService`. Account merge and duplicate detection are Part 1's (`AccountMergeService`, `DuplicateAccountService`).
**Owned tables:** `profiles`, `contact_fields`, `user_emails`, `communication_preferences` (legacy, frozen — Email owns preferences), `volunteer_history_entries`, `profile_languages`
`IUserRepository`, `IUserEmailRepository` and `ICommunicationPreferenceRepository` are the only code paths that touch these tables via `DbContext`. Repositories are Singleton, using `IDbContextFactory<UsersDbContext>` and short-lived contexts per method.
- **Caching.** Profile field storage (save, medical conditions, volunteer history, anonymize) is on `UserService`; readers get the single unified `UserInfo` projection served by `CachingUserService` (Singleton, `Humans.Users/Data/`) — see Part 1's caching-decorator bullet. There is no separate profile-only cache or warmup service (`Docs/health.md`).
- **`UserInfo` is canonical.** Derived properties `PrimaryEmail` and `AllVerifiedEmails` are the email read sites (the Google Workspace address is GoogleIntegration's `IGoogleIntegrationService`); `CachingUserService` populates them from already-loaded `UserEmail` rows (no new repo lookups). The lifecycle marker is the stored `User.State`, surfaced as the non-nullable `UserInfo.State`; `ProfileInfo` carries no state at all.
- **Stub Profile invariant.** New imported accounts commit their User, provisioned UserEmail and stub Profile in one local transaction; failures roll back all three so a retry cannot duplicate an orphan account. The creation audit follows the local commit, and UserInfo refresh follows commit or rollback (replacement imports retain their outer transaction’s refresh). Import and magic-link provisioning retries complete a missing Profile before returning an existing account; the idempotent ensure preserves an existing Profile and its names. Each repaired Profile records `StubProfileBackfilled` after save (import service or signup actor), and another failure propagates so the caller can retry. Every newly created User materializes a bare Profile inline at the User-creation call site (`ExternalLoginService.CompleteExternalLoginAsync`, `AccountProvisioningService.FindOrCreateUserByEmailAsync`/`CompleteMagicLinkSignupAsync`); the User row carries `UserState.Bare`. `IProfileEditorService.SaveProfileAsync` (via `IUserManagement.SaveProfileAsync`) promotes `User.State` to `Active` once `BurnerName`/`FirstName`/`LastName` are all populated. Legacy profile-less users (contact imports) are reconciled through the `users.stub-profiles` data migration on `/Debug/DataMigrations` — idempotent; its pending rows exclude tombstones (which the ensure refuses), so it reaches done. The apply requires an admin actor ID and records `StubProfileBackfilled` per successfully created member profile after save, before claims/cache refresh; existing profiles and tombstone refusals emit no success audit. Until the backfill is run, `GET /Profile/{id}/Popover` renders a sparse fallback card for these users so `<human-link>` hovers don't 404 — see the Invariants section bullet.
- **Missing display names.** Base’s shared human component resolves missing or empty names through six-culture `Common_Unknown`; a real BurnerName equal to `Unknown` remains unchanged.
- **Names live on `User`, dual-written from `Profile` (nobodies-collective/Humans#1097).** `UserRepository`'s profile `AddAsync`/`UpdateAsync` seam copies `BurnerName`/`FirstName`/`LastName` onto the owning `users` row in the same save; the merge, purge and GDPR-anonymize paths overwrite them with their tombstone labels so no purged name can resolve. `Profile` remains the read source for the legal name; only `UserInfo.BurnerName` reads the `users` column first. Legacy rows are reconciled through the `users.names-onto-user` data migration on `/Debug/DataMigrations` — idempotent review → confirm listing every unsynced row; the apply requires an admin actor ID, and every completed member save records `UserNamesBackfilled` before continuing the batch (no name values in the audit description); retiring the `Profile` columns (nobodies-collective/Humans#1098) waits on that migration reporting done on production's `/api/data-migrations`.
- Email add/update/remove writes keep the caller's cancellation token until save. After a successful save, their caching decorator finishes its email-slice refresh without request cancellation before returning, so the caller can complete its audit or notification. Read-only refresh entry points remain cancellable.
- **`UserEmail.IsPrimary` invariants.** Service-layer guarantee in `UserEmailService.EnsurePrimaryInvariantAsync` (exactly one verified `IsPrimary` per user, recovers from zero/multi states). Account-merge fold preserves target's `IsPrimary` and demotes the source's. No DB unique index — per `memory/architecture/db-enforcement-minimal.md` the service is the contract; a DB partial unique index would push violations to runtime as untyped `DbUpdateException` failures rather than service-layer recovery (column persists under legacy name `IsNotificationTarget` per `no-column-drops-for-decoupling.md`). **`UserEmailService.ClearPrimaryAsync` is the duplicate-flag recovery path** — it drops `IsPrimary` from a single row *without* invoking `EnsurePrimaryInvariantAsync`, but only when at least one other verified `IsPrimary` row exists; it returns `false` otherwise so the user can never end in a zero-verified-primary state via this path. Surface (admin and self): `/Profile/{id}/Admin/Emails/ClearPrimary` and `/Profile/Me/Emails/ClearPrimary` — both UI buttons appear only when ≥ 2 verified `IsPrimary` rows exist, and the service rejects direct form replay below that threshold.
- **Email delete guards (issue nobodies-collective/Humans#758).** `UserEmailService.DeleteEmailAsync` returns `DeleteEmailResult` refusal keys for removal of (a) the primary email — the user must promote another verified email to primary first — (b) the last verified email, and (c) any email whose address matches the user's event ticket (order buyer or matched attendee), read via `ITicketService.GetTicketOrdersAsync` (lazy-resolved through `IServiceProvider` to break the `TicketQueryService → IUserEmailService → ITicketService` DI cycle). The `/Profile/Me/Emails` grid hides the Delete action for primary and ticket-linked rows (the Linked Accounts card reads `UserEmailEditDto.CanUnlink`, the `UnlinkAsync` invariant); the service re-validates all guards server-side for self and admin paths. Both routes localize refusal keys and audit only successful deletions; unexpected member-route operation failures use the shared generic error. Admin recovery for a deleted ticket/primary email uses the existing "Add verified email directly" card (`AdminAddVerifiedEmail`) + `AdminSetPrimary`.
- **Email re-add fix (issue nobodies-collective/Humans#758).** `UserEmailService.AddEmailAsync` rejects an address already held by a live `UserEmail` row on that account. The prior check used `GetPrimaryEmailAsync`, which falls back to the legacy `AspNetUsers.Email` column — so a user who deleted their primary row could never re-add the same address (the original incident). `IsGoogle` is never set or inferred on this path.
- **Email flag updates return expected refusals.** Missing rows and unverified notification targets return `UpdateEmailResult.ErrorKey`. Member and admin target/visibility routes localize refusals and audit only success; refused updates preserve flags and cached data. Verification callers check inner results before success or audit.
- **Member email dependency failures.** Notification-target, visibility, Google-identity and duplicate-flag failures keep exception text in logs; members see the shared generic error.
- **Adding an email returns expected refusals.** Invalid format, an address already on the account, and a pending merge return `AddEmailResult.ErrorKey` before creating a row or token. Member and admin routes localize the key and send no verification email or admin addition audit for refused requests. Both routes share verification link, recipient-culture and message construction. Verification messages explicitly name the account being verified as their outbox owner, so a new unverified address or an address also held by someone else cannot omit or misattribute that account's history, export or erasure. Once an admin addition returns committed, its actor audit precedes verification queueing and both finish independently of request cancellation; a queue failure propagates without erasing the addition audit or claiming success.
- **Overlapping email additions cannot duplicate the address.** The owner serializes its fresh duplicate check, insertion and required flag repairs. A concurrent addition that finds the committed row returns the same expected refusal and creates no verification token or email.
- **Unlink and Delete operate on disjoint row sets.** `UserEmailService.DeleteEmailAsync` returns an unsuccessful result for Provider-attached rows (guards non-UI callers; the grid never routes one there), and `UnlinkAsync` operates only on rows with `Provider`+`ProviderKey`: it removes the `AspNetUserLogins` entry via `UserManager.RemoveLoginAsync` **before** deleting the email row, and hard-fails (no email-row deletion) if login removal fails. After Identity removal succeeds, email-row deletion, invariant repair, cache refresh and the actor audit finish independently of request cancellation, keeping the two records synchronized across browser disconnects. Earlier guard reads remain cancellable. `UnlinkAsync` returns `UnlinkEmailResult` with a localized refusal key if no *other* verified email would remain — the guard runs even when the row being unlinked is itself unverified, because an unverified row can still carry the user's only OAuth login (issue nobodies-collective/Humans#731). Member unlink routes localize that result; unexpected operation failures use the shared generic error while diagnostics stay in logs. Admin mirror routes exist for both (`/Profile/{id}/Admin/Emails/Unlink/{emailId}`, `/Delete`).
- **Inner service** is `Humans.Users.Services.UserService`, registered as `AddKeyedScoped` under `CachingUserService.InnerServiceKey` (`"user-inner"`) — the same decorator/inner pair Part 1 describes for the User aggregate; profile writes go through it too since `UserService` absorbed `ProfileService`'s storage-mutation methods. The decorator resolves it per-call via `IServiceScopeFactory`.
- **No cross-section cache signal.** Every `UserInfo` table is Users-owned: other sections write through Users' services or `UserManager<User>`, whose decorator pass-through and `UserInfoSaveChangesInterceptor` refresh the entry. Users' own services that write past the decorator use the section-internal `IUserInfoSliceRefresher`, aliased to the same Singleton.
- **No cross-domain navs:** `Profile`, `UserEmail` and `CommunicationPreference` carry no `User` nav. Display stitching routes through `IUserService.GetUserInfosAsync`.
- **GDPR:** `AccountMergeService` implements `IUserDataContributor` (design-rules §8a) and emits the `AccountMergeRequests` slice, including each request involving any account in the subject’s resolved merge chain once. Requests under archived IDs use that same chain; unrelated requests are excluded. The `Profile`, `MergedProfiles`, `ContactFields`, `UserEmails`, `VolunteerHistory`, `Languages` and `LegacyCommunicationPreferences` slices are emitted by `UserService` — `ProfileService` implements no contributor interface. Each contributor declares its own export keys as constants on its class (`AccountMergeService`, `UserService`). `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` discovers contributors by reflection — nothing to register in a list.
- **Architecture tests** — `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` (the DI wiring; the orchestrator's own tests are in `tests/Humans.Gdpr.Tests/`).

### Account deletion cascade

Account deletion cascades (user-requested / admin-initiated / expiry-triggered) are orchestrated by `IAccountDeletionService` (`src/Sections/Humans.Users/Services/AccountDeletionService.cs`). The interface is declared on the `Humans.Users.Contracts` leaf, not the section's `Contracts/` folder, so Gdpr's deletion screens can inject it; it carries no `IOrchestrator` marker there (the leaf cannot name Base), and `AccountDeletionService` carries it instead. `ProfileService` owns only profile-picture storage (`IProfilePictureService`); profile-data anonymization is `IUserManagement.AnonymizeProfileForDeletionAsync`. User-initiated deletion request and cancel both live on `IAccountDeletionService` (`RequestDeletionAsync`, `CancelDeletionAsync`). Gdpr's `MemberDataController.RequestDeletion` (signed-in users with profiles), `GuestDataController.RequestDeletion` / `CancelDeletion` (profileless users) and Gdpr's `UserController.CancelDeletion` call the orchestrator directly — no manual `User.DeletionRequestedAt`/`DeletionScheduledFor`/`DeletionEligibleAfter` writes anywhere else. The orchestrator computes the optional `DeletionEligibleAfter` (post-event hold for current-event ticket holders) inline via `ITicketService.GetUserTicketHoldingsAsync` so every entry point gets the same treatment.

Deletion requests invalidate the user's shift authorization and view caches after the write attempt, including failures in later membership/role cleanup or the confirmation email. Those failures still propagate; cache cleanup never reports an incomplete cascade as successful.

### Touch-and-clean guidance

- Cross-section reads for `Profile.User` / `UserEmail.User` / `CommunicationPreference.User` must go through `IUserService.GetUserInfosAsync` — do not re-add nav properties to the entities.
- The token "OAuth" is banned from `IUserEmailService` / `UserEmailRepository` method, parameter, and property names — provider operations are parameterized (`LinkAsync(provider, providerKey, …)`) so new providers add data, not methods. This is documentation, not a pinned test — a test asserting a method-name token is absent is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md). The single allowed exception is `ReconcileOAuthIdentityAsync`, where "OAuth" is categorical (the OAuth-callback write channel, distinct from user-driven email management).

## Issue queue

`Section` also declares this queue’s Area-label resource key and page-path aliases; Issues discovers both with the queue.

Users owns the `Profiles` issue queue: it implements `IIssueQueueOwner` (Issues' `Contracts/`
folder) on its `Section` entry point, declaring the queue key and the roles that handle
issues filed against it — `HumanAdmin`, plus `Admin`, which handles every queue. Issues
discovers the declaration through DI and holds no list of sections; dropping the seam
sends this section's stored issues to the Admin-only queue. The key is `Profiles`, not
`Users`: Profiles merged into this section at nobodies-collective/Humans#866 and stored
rows still carry the old string, so the queue keeps its name (and `/Debug/Sections` lists
it as an unmatched annotation, which is the rename showing rather than hiding).

Deletion confirmations use the no-persistence email path. Delivery failures are terminal and logged by exception type only, without the erased address/name/subject or transport exception contents; they do not turn a completed erasure into a failed deletion.

- Verification links refuse a missing user, missing/already-verified/OAuth email row or invalid token through `VerifyEmailResult.ErrorKey`. All member-facing refusals use the existing six-culture invalid-link message; owner warnings identify only user/email row ids and a fixed reason, without token or address. Dependency failures propagate instead of being classified as invalid links or exposed as validation text. Row-bound token purposes, conflict merge requests and email update/provisioning behavior remain.

After email insertion, verification, or removal commits, required primary and Google flag repairs finish independently of browser cancellation; initial reads and the first write remain cancellable.

Email insertion and OAuth reconciliation refuse deleted or merged accounts, including legacy tombstones, before changing email rows. OAuth linking likewise refuses terminal targets before adding or moving provider credentials; a merged source may still transfer its existing login to a live survivor. Admin recovery bypasses verification cooldowns only for live accounts.
