<!-- freshness:triggers
  src/Humans.Application/Services/Users/**
  src/Humans.Domain/Entities/User.cs
  src/Humans.Domain/Entities/UserEmail.cs
  src/Humans.Domain/Entities/EventParticipation.cs
  src/Humans.Infrastructure/Data/Configurations/Users/**
  src/Humans.Web/Controllers/AccountController.cs
  src/Humans.Web/Controllers/UnsubscribeController.cs
-->
<!-- freshness:flag-on-change
  User entity surface, OAuth-vs-magic-link provisioning, event-participation monotonicity, unsubscribe token rules, and Identity-framework §2a exception — review when Users services/entity/controllers change.
-->

# Users/Identity — Section Invariants

The User aggregate and its identity surface. Profile-adjacent User properties (Google email preference, contact import, campaign unsubscribe) are documented under [`Profiles.md`](Profiles.md#user-identity-extension) because the Profile decorator stitches them into `FullProfile`; this section owns the entity itself, the identity framework extensions, and cross-event participation state.

## Concepts

- A **User** is the ASP.NET Core Identity aggregate for every human in the system. Authenticates via Google OAuth or magic link. The entity extends `IdentityUser<Guid>`.
- **Account Provisioning** creates new `User` rows from an OAuth login (`AccountController.ExternalLoginCallback`), a magic-link signup (`AccountController.CompleteSignup`), or an import (`AccountProvisioningService.FindOrCreateUserByEmailAsync` for ticket / MailerLite contacts). All three paths look up an existing user across `UserEmail` records and `User.Email` (with gmail/googlemail equivalence) before creating a new row.
- **Unsubscribe** is the one-click email opt-out surface (`/Unsubscribe/{token}`) that updates the user's per-category `CommunicationPreference` via Profile's `ICommunicationPreferenceService`. New category-aware tokens redirect to the comms-preferences page; legacy campaign-only tokens (`CampaignUnsubscribe` Data Protection purpose) show the confirmation page and are treated as `MessageCategory.Marketing`. RFC 8058 one-click POST (`/Unsubscribe/OneClick`) also routes through the same service. No login required.
- **Event Participation** is a per-user, per-year record (`Ticketed`, `Attended`, `NotAttending`, `NoShow`) derived from ticket sync, user self-declaration, and admin backfill. Owned by Users because the participation key is User + Year, not Ticket or Shift.
- **Account deletion** is a 30-day grace period: `User.DeletionRequestedAt` + `DeletionScheduledFor` are stamped when a user requests deletion (with optional `DeletionEligibleAfter` for ticket-holder event holds). `ProcessAccountDeletionsJob` runs daily and calls `IUserService.AnonymizeExpiredAccountAsync` for each due user.
- Identity sub-tables (renamed to `users`, `user_claims`, `user_logins`, `user_tokens`, `roles`, `user_roles` per Postgres convention in `HumansDbContext.OnModelCreating`) are managed by ASP.NET Identity's `UserManager<User>` / `SignInManager<User>`. Controllers may inject those framework services directly (design-rules §2a exception).

## Data Model

### User

**Table:** `users` (ASP.NET Identity table, renamed from `AspNetUsers` in `HumansDbContext.OnModelCreating`).

Extends `IdentityUser<Guid>` with project-specific columns.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Email | string | Computed from `UserEmails` (first verified, primary-preferred); falls back to `base.Email` when collection is not loaded. Override — not a plain column. |
| DisplayName | string | User-provided display name (max 256, required) |
| PreferredLanguage | string | UI / email locale, default `"en"` (max 10) |
| ProfilePictureUrl | string? | Google profile picture URL (max 2048) |
| CreatedAt | Instant | Set on insert; immutable (`init`) |
| LastLoginAt | Instant? | Most recent login timestamp — distinguishes imported contacts (`null`) from active users |
| MagicLinkSentAt | Instant? | Rate-limit anchor for magic-link sends (see Auth invariants) |
| LastConsentReminderSentAt | Instant? | Rate-limit anchor for the re-consent reminder email |
| ICalToken | Guid? | Token in the user's personal iCal feed URL; regeneratable |
| SuppressScheduleChangeEmails | bool | Per-user opt-out for schedule-change notifications |
| UnsubscribedFromCampaigns | bool | Legacy campaign opt-out flag. **Not** flipped by the current unsubscribe flow — the per-category `CommunicationPreference` table is the live source of truth. |
| GoogleEmailStatus | GoogleEmailStatus | Sync status for the user's Google Workspace identity. Stored as string; default `Unknown`. Set to `Rejected` on permanent Google API error; reset to `Unknown` on email change. |
| ContactSource | ContactSource? | Where imported from (`Manual`, `MailerLite`, `TicketTailor`); null for self-registered users. |
| ExternalSourceId | string? (256) | ID in the external source system (e.g., MailerLite subscriber ID). |
| DeletionRequestedAt | Instant? | When the user requested account deletion |
| DeletionScheduledFor | Instant? | `DeletionRequestedAt + 30 days`; the earliest the job will anonymize |
| DeletionEligibleAfter | Instant? | Optional event-hold floor for ticket holders; the job waits past this date too |
| MergedToUserId | Guid? | Self-referential FK → User. Set on this row when `AccountMergeService.AcceptAsync` folds it into the target. Source rows are tombstones — they outlive their target's lifecycle (`OnDelete: Restrict`) so append-only history (audit log, consent records, budget audit log) stays attributable. Filtered index `IX_users_MergedToUserId` (WHERE NOT NULL) backs the chain-follow lookup. |
| MergedAt | Instant? | When the merge tombstone was applied. Null while live. |

**Deleted C# properties (shadow columns only):** `GoogleEmail` — C# property removed (issue #635 §15i / email-identity-decoupling PR 3). Column kept on disk as EF shadow property (`UserConfiguration.cs:27`) pending deferred column-drop PR per `memory/architecture/no-drops-until-prod-verified.md`. Canonical read path is `FullProfile.GoogleEmail` (derived from `UserEmail.IsGoogle`). Similarly `UserEmail.IsOAuth` and `UserEmail.DisplayOrder` are shadow-only columns. `UserEmailLegacyFieldRestrictionsTests` enforces that Application + Web code never references these deleted properties.

**Profile-adjacent fields documented in `Profiles.md#user-identity-extension`:** `GoogleEmail` (shadow/deleted), `GoogleEmailStatus`, `ContactSource`, `ExternalSourceId`, `UnsubscribedFromCampaigns` are also described there because `CachingProfileService` stitches them into `FullProfile`. The canonical field table is here; `Profiles.md` describes FullProfile projection semantics.

Computed: `IsDeletionPending => DeletionRequestedAt.HasValue`.

User-suspension state lives on `Profile.State` (`ProfileState.Suspended`) — see `docs/sections/Profiles.md`. The legacy `Profile.IsSuspended` bool is `[Obsolete]` (issue #635 §15i, custom diagnostic id `HUM_PROFILE_ISSUSPENDED`); both are dual-written by `ProfileService.SetSuspendedAsync` / `ProfileRepository.SuspendManyAsync` until a follow-up PR drops the column after prod soak. The User entity has no `IsArchived` / `SuspendedAt` / `SuspensionReason` columns; "archive" / "lockout" semantics are achieved by anonymizing identity fields and removing OAuth logins through `IUserService.PurgeAsync` / `AnonymizeExpiredAccountAsync`.

Issue #635 (2026-05-04) **stripped** six User-side cross-domain navs (`User.Profile`, `User.RoleAssignments`, `User.ConsentRecords`, `User.Applications`, `User.TeamMemberships`, `User.CommunicationPreferences`) and the `User.GetEffectiveEmail()` method. Inverse-side EF configurations on each owning entity now own the schema-level FK constraints (e.g., `ProfileConfiguration.HasOne<User>().WithOne().HasForeignKey<Profile>(p => p.UserId)`); the strip is verified non-destructive via a fresh `dotnet ef migrations add` producing an empty `Up()`/`Down()`. Two navs **remain declared** on User: `User.UserEmails` (required by the `User.Email` override per the issue's AC) and `User.EventParticipations` (owned by the Users section itself). Arch test `User_HasNoCrossDomainNavigationProperties` enforces the strip.

`User.Email` is overridden on the entity to compute from `UserEmails` (first verified, primary-preferred) — application code reads `user.Email` and gets the canonical address without touching the underlying Identity column. `User.NormalizedEmail` is `[Obsolete]` (issue #635 §15i, diagnostic id `HUM_USER_NORMALIZEDEMAIL`); applications must use `user.Email` or `IUserEmailRepository` for canonical lookups. `User.EmailConfirmed` is also overridden (true when any `UserEmail` is verified). Identity's email-lookup APIs (`UserManager.FindByEmailAsync` / `FindByNameAsync`) are forbidden by `IdentityFindByEmailRestrictionsTests` — application code routes through `IUserEmailService.FindVerifiedEmailWithUserAsync` / `IMagicLinkService.FindUserByVerifiedEmailAsync` instead. The §15i spec proposed a `HumansUserStore` subclass that would reroute those Identity calls; an observability shim (`LoggingUserStoreDecorator`) ran in production for a soak window in 2026-05 and was retired (issue #701) once the data confirmed Identity itself does not internally call these.

`User.GetEffectiveEmail()` was deleted in issue #635 (§15i, 2026-05-04). It was a literal alias for the `User.Email` override; callers were migrated to read `user.Email` directly (or `FullProfile.PrimaryEmail` for canonical reads on a singly-loaded User without UserEmails hydrated).

Every newly created User has a corresponding `ProfileState.Stub` Profile row materialized inline at the User-creation call site (see Profiles.md "Stub Profile invariant"). Legacy profile-less users are reconciled via `/Profile/Admin/Backfill`.

### EventParticipation

Per-user, per-year record of event involvement. Derived from ticket sync, user self-declaration, and admin backfill. Owned by Users because the natural key is User + Year, not Order or Shift.

**Table:** `event_participations` (EF configuration lives in `Configurations/Shifts/EventParticipationConfiguration.cs` for historical reasons; the entity itself is owned by Users.)

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK (`init`) |
| UserId | Guid | FK → User. The `User` nav is declared on the entity for EF; the inverse `User.EventParticipations` collection is also declared. |
| Year | int | Year of the event |
| Status | ParticipationStatus | `NotAttending` (0), `Ticketed` (1), `Attended` (2), `NoShow` (3). Stored as string. |
| Source | ParticipationSource | `UserDeclared` (0), `TicketSync` (1), `AdminBackfill` (2). Stored as string. |
| DeclaredAt | Instant? | Set when the user self-declared (Source = `UserDeclared`); null otherwise. |

**Indexes:** unique on `(UserId, Year)`.

`Attended` is permanent: ticket sync cannot downgrade it. `Ticketed` is removable when the last valid ticket is voided / transferred (`RemoveTicketSyncParticipationAsync`). `NotAttending` (with Source = `UserDeclared`) can only be undone by the same user via `UndoNotAttendingAsync`; ticket sync also overrides it when a ticket is purchased. `NoShow` is a post-event derivation for ticket holders who did not check in.

### Identity framework tables

`HumansDbContext.OnModelCreating` renames every Identity table to a lowercase `snake_case` Postgres-friendly name:

- `user_claims` (was `AspNetUserClaims`)
- `user_logins` (was `AspNetUserLogins`)
- `user_tokens` (was `AspNetUserTokens`)
- `roles` (was `AspNetRoles`) — ASP.NET Identity creates the table because `IdentityDbContext<User, IdentityRole<Guid>, Guid>` is used. Authorization itself does **not** read this table — role membership is computed from `role_assignments` by `RoleAssignmentClaimsTransformation` (see [Auth.md](Auth.md)).
- `user_roles` (was `AspNetUserRoles`) — same rationale; not used by the runtime authorization path.
- `role_claims` (was `AspNetRoleClaims`) — same rationale.

These are managed by `UserManager<User>` / `SignInManager<User>` / `RoleManager<IdentityRole<Guid>>` from `Microsoft.AspNetCore.Identity`. Do not write a custom repository over them.

## Routing

Two controllers serve this section:

**`AccountController`** (`/Account/*`) — authentication and user creation:
- `GET /Account/Login` — login page
- `POST /Account/ExternalLogin` — initiates Google OAuth
- `GET /Account/ExternalLoginCallback` — OAuth callback; creates/links/signs-in user
- `POST /Account/MagicLinkRequest` — sends magic link to email
- `GET /Account/MagicLinkConfirm` — landing page (prevents scanner token consumption)
- `POST /Account/MagicLink` — verifies token and signs in
- `GET /Account/MagicLinkSignup` — displays signup form after token verification
- `POST /Account/CompleteSignup` — creates new user via magic link
- `POST /Account/Logout`
- `GET /Account/AccessDenied`

**`UnsubscribeController`** (`/Unsubscribe/*`) — unauthenticated email opt-out:
- `GET /Unsubscribe/{token}` — confirms unsubscribe (legacy campaign or new category-aware token)
- `POST /Unsubscribe/OneClick` — RFC 8058 one-click unsubscribe (no anti-forgery token by design)

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone with a valid email | Sign up via OAuth (`/Account/ExternalLogin`) or magic link (`/Account/MagicLinkRequest`). Use `/Unsubscribe/{token}` (no login required). |
| Import jobs (Tickets, MailerLite) | Call `IAccountProvisioningService.FindOrCreateUserByEmailAsync` to materialize contact-only User rows (no `LastLoginAt`). |
| Authenticated human | Read own User row. Self-declare `NotAttending` for the active event year via `IUserService.DeclareNotAttendingAsync` (and undo via `UndoNotAttendingAsync`). Request account deletion. |
| HumanAdmin, Board, Admin | Read any User's deletion / login state. Suspension itself is a Profile concern (see [Profiles.md](Profiles.md)). |
| Admin | Trigger account merge flows (see [Profiles.md](Profiles.md) — `AccountMergeService` lives there). Outside Production only: purge a human via `AdminController.PurgeHuman`. |

## Invariants

- OAuth login (`AccountController.ExternalLoginCallback`) checks verified `UserEmails`, then unverified `UserEmails` / `User.Email`, before creating a new account — preventing duplicate accounts when the same email exists on another user in any form. The locked-out branch additionally re-links a stale OAuth login from a merged source account to the active target account.
- `AccountController` / `DevLoginController` and the ASP.NET Identity framework surface may inject `UserManager<User>` and `SignInManager<User>` directly — this is the explicit §2a exception because Identity is a framework concern, not a domain service. Application-layer code (`AccountProvisioningService`) may also inject `UserManager<User>` for user creation; everything else routes through `IUserService`.
- Event-participation derivation is monotonic on `Attended`: once an attendee has been checked in, their `EventParticipation.Status = Attended` row cannot be downgraded by ticket sync. `Ticketed`, `NotAttending`, and `NoShow` are mutable.
- `UndoNotAttendingAsync` only succeeds when the existing record is `(Status = NotAttending, Source = UserDeclared)`; an admin backfill or ticket-sync row cannot be undone via the user surface.
- `User.GoogleEmailStatus = Rejected` is terminal for sync-driven writes: `TrySetGoogleEmailStatusFromSyncAsync` refuses to flip a `Rejected` user back to `Valid`. The unconditional override (`SetGoogleEmailStatusAsync`) was removed from the public surface as of the account-merge fold redesign — all external callers are sync-driven and go through the Try variant.
- `/Unsubscribe/{token}` is unauthenticated; new-format tokens are validated by Profile's `CommunicationPreferenceService` (signed via ASP.NET Data Protection with the `CommunicationPreferences` purpose), legacy tokens by the `CampaignUnsubscribe` time-limited protector. Token tampering returns `NotFound`; no account enumeration. The RFC 8058 `/Unsubscribe/OneClick` POST is also unauthenticated and skips the anti-forgery token by design.
- `UserService` implements `IUserDataContributor` (design-rules §8) and contributes the User-account slice (Id, Email, DisplayName, PreferredLanguage, GoogleEmail, UnsubscribedFromCampaigns, SuppressScheduleChangeEmails, ContactSource, deletion / created / last-login timestamps) under `GdprExportSections.Account` plus an array of `{ Year, Status, Source, DeclaredAt }` rows under `GdprExportSections.EventParticipations` (every `EventParticipation` row owned by the user) to the GDPR export.
- A `User` row whose `MergedToUserId` is non-null is a **merge tombstone**: its identity fields are anonymized, its OAuth logins have been re-FK'd to the target, `LockoutEnd` is far-future, but the row itself persists indefinitely so append-only history written under the source id stays resolvable. The self-referential FK is `OnDelete(Restrict)` — deleting a target cannot cascade-delete its source tombstones.

## Negative Access Rules

- Controllers (other than `AccountController` / `DevLoginController` / the ASP.NET Identity framework surface) **cannot** inject `UserManager<User>` or `SignInManager<User>`. They go through `IUserService`.
- Application-layer services in `Humans.Application.Services.Users/` **cannot** inject `HumansDbContext` directly — they go through `IUserRepository` / `IUserEmailRepository`. The Application project's reference graph blocks `Microsoft.EntityFrameworkCore`.
- Other Application-layer services **cannot** read or write the `users` / Identity tables directly — they go through `IUserService`.
- Regular humans **cannot** purge any account.
- An Admin **cannot** purge their own account — `AdminController.PurgeHuman` returns the user to the admin-detail page with an error when `user.Id == currentUser.Id`.
- Purge **cannot** run in Production — `AdminController.PurgeHuman` returns `NotFound` when `IWebHostEnvironment.IsProduction()`. Account anonymization (the GDPR-deletion path through `AnonymizeExpiredAccountAsync`) runs in every environment via `ProcessAccountDeletionsJob`.
- A merge-tombstoned `User` (`MergedToUserId` non-null) **cannot** sign in — `LockoutEnd` is bumped far-future during fold. Application code outside `IAccountMergeService` **cannot** clear `MergedToUserId` / `MergedAt` or revive a tombstone.

## Triggers

- **On first OAuth login (no matching account):** `AccountController.ExternalLoginCallback` creates the `User` via `UserManager.CreateAsync`, attaches the external login, persists a provider-tagged `UserEmail` row via `IUserEmailService.LinkAsync` (provider-parameterized, replaces the removed `AddOAuthEmailAsync`), and signs the user in. Profile creation happens lazily in the Profile section (see [Profiles.md](Profiles.md)).
- **On import (Tickets / MailerLite contact upsert):** `AccountProvisioningService.FindOrCreateUserByEmailAsync` looks up an existing user by `UserEmail` and `User.Email` (with gmail/googlemail equivalence), creates a contact-only `User` + `UserEmail` if no match, layers `User.ContactSource` onto an existing self-registered user when null, and writes an `AuditAction.ContactCreated` audit entry on creation.
- **On magic-link send:** `MagicLinkService` (Auth) stamps `User.MagicLinkSentAt` for rate-limiting (see [Auth.md](Auth.md)).
- **On unsubscribe click / RFC 8058 one-click:** `UnsubscribeService.ConfirmUnsubscribeAsync` calls Profile's `ICommunicationPreferenceService.UpdatePreferenceAsync` to opt the user out of the message category (`Marketing` for legacy tokens, the token's category otherwise). The `User.UnsubscribedFromCampaigns` flag exists but is **not** flipped here — the per-category `CommunicationPreference` table is the source of truth for opt-out.
- **On ticket sync:** `TicketSyncService` calls `IUserService.SetParticipationFromTicketSyncAsync` (or `RemoveTicketSyncParticipationAsync`) for each user with a status delta — never writes `event_participations` directly.
- **On admin participation backfill:** `IUserService.BackfillParticipationsAsync` writes records with `Source = AdminBackfill`.
- **On account-deletion request (user-initiated):** `IAccountDeletionService.RequestDeletionAsync` orchestrates the user-initiated path. Internally calls `IUserService.SetDeletionPendingAsync` to stamp `DeletionRequestedAt` + `DeletionScheduledFor` on the User row, revokes team memberships and governance roles immediately so the user loses access during the 30-day grace period, and sends a confirmation email.
- **On scheduled deletion expiry:** `ProcessAccountDeletionsJob` calls `IAccountDeletionService.AnonymizeExpiredAccountAsync` per due user. That coordinates: end team memberships (`ITeamService.RevokeAllMembershipsAsync`), end governance role assignments (`IRoleAssignmentService.RevokeAllActiveAsync`), anonymize the profile (`IProfileService.AnonymizeExpiredProfileAsync`), cancel active shift signups (`IShiftSignupService.CancelActiveSignupsForUserAsync`), delete VolunteerEventProfile rows (`IShiftManagementService.DeleteShiftProfilesForUserAsync`), then anonymize identity / remove `UserEmail` rows and `AspNetUserLogins` rows (`IUserService.ApplyExpiredDeletionAnonymizationAsync`). Cross-cutting caches (`IFullProfileInvalidator`, Teams active-teams + member, role-assignment claims, shift authorization) are then invalidated. The orchestrator writes the `AuditAction.AccountAnonymized` audit entry and sends the confirmation email.
- **On admin purge (non-Production only):** `AdminController.PurgeHuman` removes external logins via `UserManager.RemoveLoginAsync`, then calls `IAccountDeletionService.PurgeAsync`. The orchestrator delegates the actual identity collapse to `IUserService.PurgeOwnDataAsync` (anonymizes the `users` row, drops `UserEmail` rows and `AspNetUserLogins` rows, invalidates the FullProfile cache), then drops the Teams active-teams cache and per-user role-assignment / shift-authorization caches. Identity-only — does not cascade to Profile rows. (Replaces the pre-PR routing through `IOnboardingService.PurgeHumanAsync` and the renamed `IUserService.PurgeAsync`.)
- **On account merge accept:** `IAccountMergeService.AcceptAsync` (Profiles section) calls `IUserService.ReassignLoginsToUserAsync` (re-FKs `AspNetUserLogins` source → target) and `ReassignEventParticipationToUserAsync` (re-FKs `event_participations`), then `AnonymizeForMergeAsync` to tombstone the source row by setting `MergedToUserId` + `MergedAt` and bumping `LockoutEnd` far-future so the source can no longer sign in. The source `User` row stays in place as a redirect — it is NOT deleted — so append-only history written under the source id (audit log, consent records, budget audit log) remains attributable.
- **On per-user reads of a target after merge:** `IUserService.GetMergedSourceIdsAsync(targetUserId)` returns the set of source ids whose `MergedToUserId` points at the target. Append-only sections (`IAuditLogService`, `IConsentService`, `IBudgetService.ContributeForUserAsync`) union this set with `targetUserId` before querying so source-tombstoned rows surface for the target.

## Cross-Section Dependencies

Outbound (Users → other sections), split between the foundational `UserService` (no higher-section edges, enforced by `UserArchitectureTests.UserService_HasNoOutboundEdgeToHigherLevelSections`) and `AccountDeletionService` (the deletion cascade orchestrator that explicitly bridges higher-level sections):

**From `UserService` / `UnsubscribeService`:**
- **Profiles:** `ICommunicationPreferenceService.UpdatePreferenceAsync` (called from `UnsubscribeService`), `IFullProfileInvalidator.InvalidateAsync` (called on writes that change FullProfile-visible fields: `DisplayName`, `GoogleEmailStatus`, purge / anonymize).

**From `AccountDeletionService` (cascade orchestrator — `Humans.Application.Services.Users.AccountLifecycle/`):**
- **Profiles:** `IProfileService.AnonymizeExpiredProfileAsync` (lazy-resolved via `IServiceProvider` to avoid construction-time DI cycles), `IFullProfileInvalidator.InvalidateAsync`.
- **Auth:** `IRoleAssignmentService.RevokeAllActiveAsync` (lazy-resolved), `IRoleAssignmentClaimsCacheInvalidator.Invalidate`.
- **Teams:** `ITeamService.RevokeAllMembershipsAsync`, `InvalidateActiveTeamsCache`, `RemoveMemberFromAllTeamsCache`.
- **Shifts:** `IShiftSignupService.CancelActiveSignupsForUserAsync` (lazy), `IShiftManagementService.DeleteShiftProfilesForUserAsync` (lazy), `IShiftAuthorizationInvalidator.Invalidate`.

Inbound (other sections → Users) — the typical direction:

- **Shifts / Tickets:** call `IUserService.DeclareNotAttendingAsync` (Home controller for self-declaration), `SetParticipationFromTicketSyncAsync`, `RemoveTicketSyncParticipationAsync`, `BackfillParticipationsAsync`. Direct writes to `event_participations` are forbidden.
- **Notifications, Email, AuditLog:** call `IUserService.GetByIdsAsync` (always returns `User` with `UserEmails` populated — there is no "without emails" variant; the caching decorator's `UserInfo` dict already carries the full payload) to resolve recipient identity/email without navigating cross-domain navs.
- **Account-deletion job (Infrastructure):** calls `IUserService.GetAccountsDueForAnonymizationAsync` + `AnonymizeExpiredAccountAsync`.
- **Profiles (`IAccountMergeService.AcceptAsync`):** calls `IUserService.ReassignLoginsToUserAsync`, `ReassignEventParticipationToUserAsync`, and `AnonymizeForMergeAsync` to fold a source User into a target.
- **Audit Log / Legal & Consent / Budget:** call `IUserService.GetMergedSourceIdsAsync(targetUserId)` to chain-follow merge tombstones on per-user reads of append-only entities.

## Architecture

**Owning services:** `UserService`, `AccountProvisioningService`, `UnsubscribeService` (all in `Humans.Application.Services.Users/`); `AccountDeletionService` (in `Humans.Application.Services.Users/AccountLifecycle/`); `UserEmailProviderBackfillService` (in `Humans.Application.Services.Users/` — one-shot backfill utility for Provider/IsGoogle fields on `UserEmail` rows, reads `user_emails` via `IUserEmailRepository`).
**Owned tables:** `users`, `user_claims`, `user_logins`, `user_tokens`, `roles` (legacy), `user_roles` (legacy), `role_claims` (legacy), `event_participations`.
**Status:** (A) Migrated (peterdrier/Humans PR #243 for issue nobodies-collective/Humans#511, 2026-04-21). Account merge fold support added 2026-05-01 (User.MergedToUserId / MergedAt; Reassign + AnonymizeForMerge methods).

- `UserService`, `AccountProvisioningService`, `UnsubscribeService` live in `Humans.Application.Services.Users/` and never import `Microsoft.EntityFrameworkCore`. `AccountProvisioningService` does inject `UserManager<User>` per the §2a exception (Identity owns the password hash / security stamp surface).
- `IUserRepository` (impl `Humans.Infrastructure/Repositories/Users/UserRepository.cs`) owns the SQL surface for `users` plus `event_participations` (the natural key is User). `IUserEmailRepository` is the parallel surface for `UserEmail` (owned by Profiles but read/written from Users for lookup + OAuth-email lock-step).
- **Decorator decision — caching decorator added 2026-05-13 (issue #703).** `CachingUserService` (Singleton, `Humans.Infrastructure/Services/Users/`) owns a `ConcurrentDictionary<Guid, UserInfo>` of the unified read-model spanning the 8 contributing tables (`users`, `user_emails`, `event_participations`, AspNet `user_logins`, `profiles`, `contact_fields`, `profile_languages`, `volunteer_history_entries`). Pattern mirrors `CachingProfileService` exactly: dict hits served synchronously; cache miss refills via the inner Scoped `UserService` (keyed `"user-inner"`); writes through `IUserService` refresh the affected entry; Identity-machinery writes (`UserManager.UpdateAsync`, sign-in `LastLoginAt`, OAuth `UserEmail` row creation) are caught by `UserInfoSaveChangesInterceptor` (EF `SaveChangesInterceptor`) and routed through `IUserInfoInvalidator.InvalidateAsync`. `CachingUserService` itself implements `IHostedService` (via `TrackedCache`) and populates the dict at startup; load-all reads call `EnsureWarmed` / `EnsureWarmedAsync` and recover transparently if startup warmup hasn't completed. Existing `FullProfile` cache (`CachingProfileService`) continues to coexist — migration of FullProfile consumers to `UserInfo` is a follow-up. UserService also still calls `IFullProfileInvalidator` on FullProfile-visible field writes (DisplayName, GoogleEmailStatus, deletion fields) so both caches stay consistent.
- **Read/write interface split.** `IUserServiceRead` (6 methods: `GetUserInfoAsync`, `GetUserInfosAsync`, `GetAllUserInfosAsync`, `SearchUsersAsync`, `GetOnsiteUsersAsync`, `GetMergedSourceIdsAsync`) is the cross-section read surface — only `UserInfo` / `HumanSearchResult` / `OnsiteUserRow` projections plus the merge-chain-follow Guid primitive, no EF entities. `IUserService : IUserServiceRead` adds writes, cache invalidation, entity-returning reads, and Users-internal reads. External sections that only read inject `IUserServiceRead`. Enforcement is advisory pending the Roslyn analyzer; arch tests in `UserArchitectureTests` pin the inheritance + same-singleton DI. See `memory/architecture/section-read-write-split.md` and Teams' PR #678.
- **Cross-domain navs (post-§15i strip):** only `User.UserEmails` (kept for the `User.Email` override) and `User.EventParticipations` (owned by Users) remain declared. The other six (`Profile`, `RoleAssignments`, `ConsentRecords`, `Applications`, `TeamMemberships`, `CommunicationPreferences`) and `GetEffectiveEmail()` were deleted in issue #635 (2026-05-04). Inverse-side EF configurations on each owning entity now own the schema-level FK constraints. Arch test `User_HasNoCrossDomainNavigationProperties` enforces.
- **Identity framework surface** — `AccountController` and `DevLoginController` (the only two controllers in this section) inject `UserManager<User>` / `SignInManager<User>` directly per the §2a exception. There is no `AuthController` or `ManageController` class — magic-link orchestration lives in `IMagicLinkService` (Auth section) and account self-management lives across `AccountController` and Profile views. Non-controller code routes through `IUserService`.
- **GDPR:** `UserService` implements `IUserDataContributor` and contributes the `GdprExportSections.Account` slice; `ExpectedContributorTypes` in `GdprExportDependencyInjectionTests` enforces registration (design-rules §8).
- **Architecture tests:**
  - `tests/Humans.Application.Tests/Architecture/UserArchitectureTests.cs` — pins UserService/AccountProvisioningService/UnsubscribeService to Application, no DbContext, IUserRepository required, no outbound edges to higher sections, nav-strip enforcement (`User_HasNoCrossDomainNavigationProperties`).
  - `tests/Humans.Application.Tests/Architecture/AccountDeletionArchitectureTests.cs` — pins AccountDeletionService namespace and ensures it owns no tables (no DbContext, no IDbContextFactory).
  - `tests/Humans.Application.Tests/Architecture/UserEmailLegacyFieldRestrictionsTests.cs` — IL scan ensuring no Application/Web code references the deleted shadow properties (`User.GoogleEmail`, `UserEmail.IsOAuth`, `UserEmail.DisplayOrder`).
  - `tests/Humans.Application.Tests/Architecture/IdentityFindByEmailRestrictionsTests.cs` — enforces that application code routes through `IUserEmailService.FindVerifiedEmailWithUserAsync` rather than `UserManager.FindByEmailAsync` / `FindByNameAsync`.
- The original Option A (no decorator) decision is documented in `docs/superpowers/specs/2026-04-21-issue-511-user-migration.md`; it was reversed by issue nobodies-collective/Humans#703 (2026-05-13) when measured read traffic on the `users` table exceeded the `Profile` decorator's traffic by ~7×.

### Touch-and-clean guidance

- After issue #635 (§15i nav strip): `user.Profile` / `user.TeamMemberships` / `user.RoleAssignments` / `user.Applications` / `user.ConsentRecords` / `user.CommunicationPreferences` / `user.GetEffectiveEmail()` no longer exist as User-side navs/method — readers route through `IProfileService` / `ITeamService` / `IRoleAssignmentService` / etc. or use `user.Email` (which still overrides via the surviving `UserEmails` collection). When touching `TeamService` / `GoogleWorkspaceSyncService` / `ProfileController` and the four notification jobs, prefer `IUserEmailRepository.GetByUserIdReadOnlyAsync` / `IUserService.GetByIdsAsync` over reaching into the `UserEmails` nav directly.
- Do **not** inject `HumansDbContext` into any Application-layer service under `Humans.Application.Services.Users/`. Use `IUserRepository` / `IUserEmailRepository`.
- `/Unsubscribe/{token}` and `/Unsubscribe/OneClick` must stay unauthenticated. If new unsubscribe-adjacent surfaces are added, route them through `IUnsubscribeService` (which delegates token validation to Profile's `ICommunicationPreferenceService` / the legacy `CampaignUnsubscribe` Data Protection purpose) rather than opening additional unauthenticated endpoints.
- Event-participation writes must all go through one of `IUserService.DeclareNotAttendingAsync`, `UndoNotAttendingAsync`, `SetParticipationFromTicketSyncAsync`, `RemoveTicketSyncParticipationAsync`, or `BackfillParticipationsAsync`. `TicketSyncService` already does this as of nobodies-collective/Humans#545; new writers must follow the same pattern. The repository-level `UpsertParticipationAsync` is internal to the section.
