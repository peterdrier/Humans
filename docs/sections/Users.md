<!-- freshness:triggers
  src/Humans.Application/Services/Users/**
  src/Humans.Domain/Entities/User.cs
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

Extends `IdentityUser<Guid>` with project-specific columns. The full field table is split across two files:

- [Profiles.md → User (Identity extension)](Profiles.md#user-identity-extension) — Google email preference (`GoogleEmail`, `GoogleEmailStatus`), contact-import properties (`ContactSource`, `ExternalSourceId`), campaign unsubscribe flag (`UnsubscribedFromCampaigns`). These are profile-adjacent fields that Profile's `CachingProfileService` stitches into `FullProfile`.
- Below — identity / activity / magic-link / deletion fields owned by this section directly.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Email | string | Primary identity / OAuth or signup email |
| DisplayName | string | User-provided display name (max 256, required) |
| PreferredLanguage | string | UI / email locale, default `"en"` (max 10) |
| ProfilePictureUrl | string? | Google profile picture URL (max 2048) |
| CreatedAt | Instant | Set on insert; immutable (`init`) |
| LastLoginAt | Instant? | Most recent login timestamp — distinguishes imported contacts (null) from active users |
| MagicLinkSentAt | Instant? | Rate-limit anchor for magic-link sends (see Auth invariants) |
| LastConsentReminderSentAt | Instant? | Rate-limit anchor for the re-consent reminder email |
| ICalToken | Guid? | Token in the user's personal iCal feed URL; regeneratable |
| SuppressScheduleChangeEmails | bool | Per-user opt-out for schedule-change notifications |
| DeletionRequestedAt | Instant? | When the user requested account deletion |
| DeletionScheduledFor | Instant? | `DeletionRequestedAt + 30 days`; the earliest the job will anonymize |
| DeletionEligibleAfter | Instant? | Optional event-hold floor for ticket holders; the job waits past this date too |

Computed: `IsDeletionPending => DeletionRequestedAt.HasValue`.

User-suspension state lives on `Profile.IsSuspended`, not on User. The User entity has no `IsArchived` / `SuspendedAt` / `SuspensionReason` columns; "archive" / "lockout" semantics are achieved by anonymizing identity fields and removing OAuth logins through `IUserService.PurgeAsync` / `AnonymizeExpiredAccountAsync`.

Cross-domain navs on `User` (`User.Profile`, `User.UserEmails`, `User.TeamMemberships`, `User.RoleAssignments`, `User.Applications`, `User.ConsentRecords`, `User.CommunicationPreferences`, `User.EventParticipations`) are **still declared** and **not yet `[Obsolete]`-marked** on the User class — the inverse navs on the other entities (`RoleAssignment.User`, `TeamMember.User`, etc.) carry the `[Obsolete]` markers per design-rules §6c. The User-side nav-strip is the deferred follow-up tracked in design-rules §15i.

`User.GetEffectiveEmail()` is an instance method on the entity that scans the loaded `UserEmails` collection for the verified notification target and falls back to `Email`; callers must hydrate `UserEmails` first (e.g., via `IUserService.GetByIdsWithEmailsAsync`). `User.GetGoogleServiceEmail()` returns `GoogleEmail ?? Email` and does not require additional loading.

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
- `User.GoogleEmailStatus = Rejected` is terminal for sync-driven writes: `TrySetGoogleEmailStatusFromSyncAsync` refuses to flip a `Rejected` user back to `Valid`. Operator-driven overrides (email-backfill flows that promote a freshly-provisioned `@nobodies.team`) use the unconditional `SetGoogleEmailStatusAsync`.
- `/Unsubscribe/{token}` is unauthenticated; new-format tokens are validated by Profile's `CommunicationPreferenceService` (signed via ASP.NET Data Protection with the `CommunicationPreferences` purpose), legacy tokens by the `CampaignUnsubscribe` time-limited protector. Token tampering returns `NotFound`; no account enumeration. The RFC 8058 `/Unsubscribe/OneClick` POST is also unauthenticated and skips the anti-forgery token by design.
- `UserService` implements `IUserDataContributor` (design-rules §8) and contributes the User-account slice (Id, Email, DisplayName, PreferredLanguage, GoogleEmail, UnsubscribedFromCampaigns, SuppressScheduleChangeEmails, ContactSource, deletion / created / last-login timestamps) under `GdprExportSections.Account` to the GDPR export. EventParticipation is not currently exported — tracked at nobodies-collective/Humans#595.

## Negative Access Rules

- Controllers (other than `AccountController` / `DevLoginController` / the ASP.NET Identity framework surface) **cannot** inject `UserManager<User>` or `SignInManager<User>`. They go through `IUserService`.
- Application-layer services in `Humans.Application.Services.Users/` **cannot** inject `HumansDbContext` directly — they go through `IUserRepository` / `IUserEmailRepository`. The Application project's reference graph blocks `Microsoft.EntityFrameworkCore`.
- Other Application-layer services **cannot** read or write the `users` / Identity tables directly — they go through `IUserService`.
- Regular humans **cannot** purge any account.
- An Admin **cannot** purge their own account — `AdminController.PurgeHuman` returns the user to the admin-detail page with an error when `user.Id == currentUser.Id`.
- Purge **cannot** run in Production — `AdminController.PurgeHuman` returns `NotFound` when `IWebHostEnvironment.IsProduction()`. Account anonymization (the GDPR-deletion path through `AnonymizeExpiredAccountAsync`) runs in every environment via `ProcessAccountDeletionsJob`.

## Triggers

- **On first OAuth login (no matching account):** `AccountController.ExternalLoginCallback` creates the `User` via `UserManager.CreateAsync`, attaches the external login, persists an OAuth `UserEmail` row via `IUserEmailService.AddOAuthEmailAsync`, and signs the user in. Profile creation happens lazily in the Profile section (see [Profiles.md](Profiles.md)).
- **On import (Tickets / MailerLite contact upsert):** `AccountProvisioningService.FindOrCreateUserByEmailAsync` looks up an existing user by `UserEmail` and `User.Email` (with gmail/googlemail equivalence), creates a contact-only `User` + `UserEmail` if no match, layers `User.ContactSource` onto an existing self-registered user when null, and writes an `AuditAction.ContactCreated` audit entry on creation.
- **On magic-link send:** `MagicLinkService` (Auth) stamps `User.MagicLinkSentAt` for rate-limiting (see [Auth.md](Auth.md)).
- **On unsubscribe click / RFC 8058 one-click:** `UnsubscribeService.ConfirmUnsubscribeAsync` calls Profile's `ICommunicationPreferenceService.UpdatePreferenceAsync` to opt the user out of the message category (`Marketing` for legacy tokens, the token's category otherwise). The `User.UnsubscribedFromCampaigns` flag exists but is **not** flipped here — the per-category `CommunicationPreference` table is the source of truth for opt-out.
- **On ticket sync:** `TicketSyncService` calls `IUserService.SetParticipationFromTicketSyncAsync` (or `RemoveTicketSyncParticipationAsync`) for each user with a status delta — never writes `event_participations` directly.
- **On admin participation backfill:** `IUserService.BackfillParticipationsAsync` writes records with `Source = AdminBackfill`.
- **On account-deletion request:** `IUserService.SetDeletionPendingAsync` stamps `DeletionRequestedAt` + `DeletionScheduledFor` (and optionally `DeletionEligibleAfter`).
- **On scheduled deletion expiry:** `ProcessAccountDeletionsJob` calls `IUserService.AnonymizeExpiredAccountAsync` per due user. That coordinates: end team memberships (`ITeamService.RevokeAllMembershipsAsync`), end governance role assignments (`IRoleAssignmentService.RevokeAllActiveAsync`), anonymize the profile (`IProfileService.AnonymizeExpiredProfileAsync`), cancel active shift signups (`IShiftSignupService.CancelActiveSignupsForUserAsync`), delete VolunteerEventProfile rows (`IShiftManagementService.DeleteShiftProfilesForUserAsync`), then anonymize identity / remove `UserEmail` rows (`IUserRepository.ApplyExpiredDeletionAnonymizationAsync`). Cross-cutting caches (`IFullProfileInvalidator`, Teams active-teams + member, role-assignment claims, shift authorization) are then invalidated. The job writes the `AuditAction.AccountAnonymized` audit entry and sends the confirmation email.
- **On admin purge (non-Production only):** `AdminController.PurgeHuman` removes external logins via `UserManager.RemoveLoginAsync`, then calls `IOnboardingService.PurgeHumanAsync` (which composes Profile / UserEmail / role-assignment cleanup). `IUserService.PurgeAsync` itself anonymizes the `users` row, drops `UserEmail` rows, invalidates the FullProfile cache, and drops the Teams active-teams cache.

## Cross-Section Dependencies

Outbound (Users → other sections), per the foundational-section rule (Users sits at the bottom of the stack; the only outbound calls are during the multi-section anonymization orchestration in `AnonymizeExpiredAccountAsync`):

- **Profiles:** `IProfileService.AnonymizeExpiredProfileAsync` (lazy-resolved via `IServiceProvider` to avoid construction-time cycles), `ICommunicationPreferenceService.UpdatePreferenceAsync` (called from `UnsubscribeService`), `IFullProfileInvalidator.InvalidateAsync` (called on writes that change FullProfile-visible fields: `DisplayName`, `GoogleEmail`, `GoogleEmailStatus`, purge / anonymize).
- **Auth:** `IRoleAssignmentService.RevokeAllActiveAsync` (lazy-resolved) and `IRoleAssignmentClaimsCacheInvalidator.Invalidate` — called during `AnonymizeExpiredAccountAsync`.
- **Teams:** `ITeamService.RevokeAllMembershipsAsync`, `InvalidateActiveTeamsCache`, `RemoveMemberFromAllTeamsCache` — called during purge / anonymize.
- **Shifts:** `IShiftSignupService.CancelActiveSignupsForUserAsync` (lazy), `IShiftManagementService.DeleteShiftProfilesForUserAsync` (lazy), `IShiftAuthorizationInvalidator.Invalidate` — called during anonymization.

Inbound (other sections → Users) — the typical direction:

- **Shifts / Tickets:** call `IUserService.DeclareNotAttendingAsync` (Home controller for self-declaration), `SetParticipationFromTicketSyncAsync`, `RemoveTicketSyncParticipationAsync`, `BackfillParticipationsAsync`. Direct writes to `event_participations` are forbidden.
- **Notifications, Email, AuditLog:** call `IUserService.GetByIdsAsync` / `GetByIdsWithEmailsAsync` to resolve recipient identity/email without navigating cross-domain navs.
- **Account-deletion job (Infrastructure):** calls `IUserService.GetAccountsDueForAnonymizationAsync` + `AnonymizeExpiredAccountAsync`.

## Architecture

**Owning services:** `UserService`, `AccountProvisioningService`, `UnsubscribeService` (all in `Humans.Application.Services.Users/`).
**Owned tables:** `users`, `user_claims`, `user_logins`, `user_tokens`, `roles` (legacy), `user_roles` (legacy), `role_claims` (legacy), `event_participations`.
**Status:** (A) Migrated (peterdrier/Humans PR #243 for issue nobodies-collective/Humans#511, 2026-04-21).

- `UserService`, `AccountProvisioningService`, `UnsubscribeService` live in `Humans.Application.Services.Users/` and never import `Microsoft.EntityFrameworkCore`. `AccountProvisioningService` does inject `UserManager<User>` per the §2a exception (Identity owns the password hash / security stamp surface).
- `IUserRepository` (impl `Humans.Infrastructure/Repositories/Users/UserRepository.cs`) owns the SQL surface for `users` plus `event_participations` (the natural key is User). `IUserEmailRepository` is the parallel surface for `UserEmail` (owned by Profiles but read/written from Users for lookup + OAuth-email lock-step).
- **Decorator decision — no caching decorator.** User is ~500 rows with no hot bulk-read path. Same rationale as Governance / Feedback / Auth. Writes that change FullProfile-visible fields (`DisplayName`, `GoogleEmail`, `GoogleEmailStatus`) invalidate via `IFullProfileInvalidator`. Writes to deletion state and event-participation do not invalidate — those fields are not part of the FullProfile projection.
- **Cross-domain navs still declared (deferred strip):** `User.Profile`, `User.UserEmails`, `User.TeamMemberships`, `User.RoleAssignments`, `User.Applications`, `User.ConsentRecords`, `User.CommunicationPreferences`, `User.EventParticipations`. The User-side navs are not yet `[Obsolete]`-marked; the inverse navs on the other entities (`RoleAssignment.User`, `TeamMember.User`, `FeedbackReport.User`, etc.) carry the markers. Tracked as the deferred cross-cutting cleanup in design-rules §15i.
- **Identity framework surface** — `AccountController` and `DevLoginController` (the only two controllers in this section) inject `UserManager<User>` / `SignInManager<User>` directly per the §2a exception. There is no `AuthController` or `ManageController` class — magic-link orchestration lives in `IMagicLinkService` (Auth section) and account self-management lives across `AccountController` and Profile views. Non-controller code routes through `IUserService`.
- **GDPR:** `UserService` implements `IUserDataContributor` and contributes the `GdprExportSections.Account` slice; `ExpectedContributorTypes` in `GdprExportDependencyInjectionTests` enforces registration (design-rules §8).
- **Option A (no decorator)** is documented in `docs/superpowers/specs/2026-04-21-issue-511-user-migration.md`.

### Touch-and-clean guidance

- When touching `TeamService`, `GoogleWorkspaceSyncService`, `SendBoardDailyDigestJob`, `SyncLegalDocumentsJob`, `SystemTeamSyncJob`, `SuspendNonCompliantMembersJob`, or `ProfileController` — do **not** add new reads of `user.UserEmails`, `user.Profile`, `user.TeamMemberships`, or `user.GetEffectiveEmail()` via nav properties on a singly-loaded `User`. Route through `IUserEmailService` / `IProfileService` / `ITeamService` / `IUserService.GetByIdsWithEmailsAsync`. The existing nav reads are grandfathered under the deferred-strip bucket; no new ones land.
- Do **not** inject `HumansDbContext` into any Application-layer service under `Humans.Application.Services.Users/`. Use `IUserRepository` / `IUserEmailRepository`.
- `/Unsubscribe/{token}` and `/Unsubscribe/OneClick` must stay unauthenticated. If new unsubscribe-adjacent surfaces are added, route them through `IUnsubscribeService` (which delegates token validation to Profile's `ICommunicationPreferenceService` / the legacy `CampaignUnsubscribe` Data Protection purpose) rather than opening additional unauthenticated endpoints.
- Event-participation writes must all go through one of `IUserService.DeclareNotAttendingAsync`, `UndoNotAttendingAsync`, `SetParticipationFromTicketSyncAsync`, `RemoveTicketSyncParticipationAsync`, or `BackfillParticipationsAsync`. `TicketSyncService` already does this as of nobodies-collective/Humans#545; new writers must follow the same pattern. The repository-level `UpsertParticipationAsync` is internal to the section.
