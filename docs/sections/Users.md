# Users/Identity — Section Invariants

The User aggregate and its identity surface. Profile-adjacent User properties (Google email preference, contact import, campaign unsubscribe) are documented under [`Profiles.md`](Profiles.md#user-identity-extension) because the Profile decorator stitches them into `FullProfile`; this section owns the entity itself, the identity framework extensions, and cross-event participation state.

## Concepts

- A **User** is the ASP.NET Core Identity aggregate for every human in the system. Authenticates via Google OAuth or magic link. The entity extends `IdentityUser<Guid>`.
- **Account Provisioning** creates new `User` rows from an OAuth login or an import (ContactSource-flagged rows that are not yet activated).
- **Unsubscribe** is the one-click email opt-out surface (`/Unsubscribe/{token}`) that flips `User.UnsubscribedFromCampaigns` without requiring a login. Powered by `IUnsubscribeTokenProvider` for tokenisation.
- **Event Participation** is a per-user, per-event record (`Ticketed`, `Attended`, `NotAttending`) derived from ticket sync and volunteer declarations. Owned by Users because the participation key is User, not Ticket or Shift.
- Identity sub-tables (`AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `AspNetRoles`, `AspNetUserRoles`) are managed by ASP.NET Identity's `UserManager<User>` / `SignInManager<User>`. Controllers may inject those framework services directly (design-rules §2a exception).

## Data Model

### User

**Table:** `AspNetUsers`

Extends `IdentityUser<Guid>` with project-specific columns. The full field table is split across two files:

- [Profiles.md → User (Identity extension)](Profiles.md#user-identity-extension) — Google email preference, contact-import properties, campaign unsubscribe flag. These are profile-adjacent fields that Profile's `CachingProfileService` stitches into `FullProfile`.
- Below — identity / activity / magic-link fields owned by this section directly.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Email | string | OAuth login email — primary identity |
| DisplayName | string? | User-provided display name |
| IsArchived | bool | Soft-delete via archive flag (set by merge/purge flows) |
| LastLoginAt | Instant? | Most recent login timestamp — distinguishes imported contacts (null) from active users |
| MagicLinkSentAt | Instant? | Rate-limit anchor for magic-link sends (see Auth §invariants) |
| DeletionRequestedAt | Instant? | GDPR deletion requested at this time — memberships revoked immediately; purge deferred to job |
| SuspendedAt | Instant? | Non-null when the account is suspended |
| SuspendedByUserId | Guid? | **FK only**, no nav — who suspended |
| SuspensionReason | string? | Reason text from suspension |

Cross-domain navs on `User` (`User.Profile`, `User.UserEmails`, `User.TeamMemberships`, `User.RoleAssignments`, `User.Applications`, `User.ConsentRecords`, `User.CommunicationPreferences`) are **still declared** but `[Obsolete]` in the target migration plan. PR #243 landed the Application-layer move but deferred the nav strip — that follow-up is the single biggest cross-cutting cleanup remaining (design-rules §15i).

`User.GetEffectiveEmail()` routes through `IUserEmailService` (Profiles) — do not reimplement email resolution here.

### EventParticipation

Per-user, per-event record of event involvement. Derived from ticket sync (`Ticketed`, `Attended`) and user declaration (`NotAttending`). Owned by Users because the natural key is User + Year, not Order or Shift.

**Table:** `event_participations`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK → User — **FK only**, no nav |
| EventYear | int | Year of the event |
| Participation | ParticipationStatus | `Ticketed`, `Attended`, `NotAttending` |
| Source | ParticipationSource | `TicketSync`, `UserDeclaration`, `AdminBackfill` |
| UpdatedAt | Instant | Last modification |

`Attended` is permanent (check-in produced it). `Ticketed` can be downgraded if the last valid ticket is voided or transferred. `NotAttending` is overridden by a ticket purchase.

### Identity framework tables

- `AspNetUserClaims`
- `AspNetUserLogins`
- `AspNetUserTokens`
- `AspNetRoles` — **legacy**, not used by Auth (Auth reads `role_assignments` instead — see [Auth.md](Auth.md)). Kept because ASP.NET Identity creates the table.
- `AspNetUserRoles` — **legacy**, same rationale.

These are managed by `UserManager<User>` / `SignInManager<User>` / `RoleManager<IdentityRole<Guid>>` from `Microsoft.AspNetCore.Identity`. Do not write a custom repository over them.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone with a valid email | Sign up via OAuth or magic link. Trigger `AccountProvisioningService` as a side-effect of first login. |
| Authenticated human | Read own User row (as `CurrentUser`). Use `/Unsubscribe/{token}` to opt out of campaign emails without login |
| HumanAdmin, Board, Admin | Read any User's archive/suspension state. Suspend / unsuspend accounts. View last-login audit |
| Admin | Trigger account merge flows (see [Profiles.md](Profiles.md) — `AccountMergeService` lives there). Admin-only environments: purge a human |

## Invariants

- OAuth login checks verified `UserEmails`, unverified `UserEmails`, and `User.Email` before creating a new account — preventing duplicate accounts when the same email exists on another user in any form.
- `User.IsArchived` flips to true during merge (source account) or purge (pre-delete). Archived users are excluded from active-member queries but their row is retained for audit linkage.
- `AuthController` / `ManageController` / any other controller may inject `UserManager<User>` and `SignInManager<User>` directly — this is the explicit §2a exception because Identity is a framework concern, not a domain service. Everything else goes through `IUserService`.
- Event-participation derivation is monotonic on `Attended`: once an attendee has been checked in, their `EventParticipation.Status = Attended` row cannot be downgraded. `Ticketed` and `NotAttending` are both mutable.
- `/Unsubscribe/{token}` is unauthenticated; the token carries the user id + unsubscribe intent signed by ASP.NET Data Protection. Token tampering fails the request; no account enumeration.
- `UserService` implements `IUserDataContributor` (design-rules §8a) and contributes the User row (plus EventParticipation slice) to the GDPR export.

## Negative Access Rules

- Controllers (other than `ManageController` / `AuthController` / `DevLoginController` / the ASP.NET Identity framework surface) **cannot** inject `UserManager<User>` or `SignInManager<User>`. They go through `IUserService`.
- Application-layer services **cannot** read `DbContext.AspNetUsers` directly — use `IUserService` / `IUserRepository`.
- Regular humans **cannot** suspend, archive, or purge any account.
- No one **cannot** purge their own account — the purge surface explicitly filters out `CurrentUserId`.
- Purge **cannot** run outside development / QA environments (gated via `IWebHostEnvironment`).

## Triggers

- **On first login:** `AccountProvisioningService.ProvisionAsync` creates a new `User` row (if no duplicate is found across verified and unverified emails), seeds a `Profile` row via `IProfileService`, and writes an audit entry.
- **On magic-link send:** `User.MagicLinkSentAt` is stamped for rate-limiting (see [Auth.md](Auth.md)).
- **On unsubscribe click:** `User.UnsubscribedFromCampaigns` flips to `true`; future campaign waves skip this user.
- **On ticket sync:** `TicketSyncService` calls `IUserService` to upsert / downgrade `EventParticipation` rows; never writes the table directly.
- **On account suspension:** audit entry is written (`AuditAction.UserSuspended`); the user's Volunteers team membership is revoked via `ISystemTeamSync`.
- **On account deletion:** orchestrated by `IAccountDeletionService` (Users section). Three lifecycle paths, each a separate entry point:
  - `RequestDeletionAsync` — user-initiated, sets a 30-day deletion schedule on the User row, revokes team memberships and governance roles immediately, sends a confirmation email.
  - `PurgeAsync` — admin-initiated, identity-only (renames + drops `UserEmail` rows, locks out the account, drops per-user team / role-assignment / shift-auth caches). Calls into `IUserService.PurgeOwnDataAsync` for the actual identity collapse; does not cascade to Profile rows.
  - `AnonymizeExpiredAccountAsync` — invoked nightly by `ProcessAccountDeletionsJob` when `DeletionEligibleAfter` has passed. Runs the full cross-section cascade: revokes team memberships and role assignments, anonymizes the Profile (`IProfileService.AnonymizeExpiredProfileAsync`), cancels active shift signups, removes shift profiles, then collapses the User identity (`IUserService.ApplyExpiredDeletionAnonymizationAsync`).

## Cross-Section Dependencies

- **Profiles:** `IProfileService`, `IUserEmailService`, `IContactFieldService`, `ICommunicationPreferenceService` — account provisioning seeds a Profile; merge/purge cascade through Profile-owned tables.
- **Auth:** `IRoleAssignmentService.RevokeAllActiveAsync` — called on purge / merge to clean up role assignments owned by Auth.
- **Teams:** `ISystemTeamSync` — suspension / activation cascades to Volunteers system team membership.
- **Notifications:** `INotificationService` — emits in-app notifications on admin actions (suspend, merge accept, etc.).
- **Shifts / Tickets:** `IUserService.UpsertParticipationAsync` is called by ticket sync and volunteer declarations; never by Shifts or Tickets writing the table directly.

## Architecture

**Owning services:** `UserService`, `AccountProvisioningService`, `UnsubscribeService`, `AccountDeletionService`
**Owned tables:** `AspNetUsers`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `AspNetRoles` (legacy), `AspNetUserRoles` (legacy), `event_participations`
**Status:** (A) Migrated (peterdrier/Humans PR #243 for issue nobodies-collective/Humans#511, 2026-04-21).

- `UserService`, `AccountProvisioningService`, `UnsubscribeService` live in `Humans.Application.Services.Users/` and never import `Microsoft.EntityFrameworkCore`.
- `IUserRepository` (impl `Humans.Infrastructure/Repositories/Users/UserRepository.cs`) owns the SQL surface for `AspNetUsers` (plus `event_participations` under the same repo — the natural key is User).
- **Decorator decision — no caching decorator.** User is ~500 rows with no hot bulk-read path. Same rationale as Governance / Feedback / Auth. Writes that change FullProfile-visible fields invalidate via `IFullProfileInvalidator` (aliased to the Profile decorator's Singleton instance).
- **Cross-domain navs still declared (deferred strip):** `User.Profile`, `User.UserEmails`, `User.TeamMemberships`, `User.RoleAssignments`, `User.Applications`, `User.ConsentRecords`, `User.CommunicationPreferences`. PR #243 landed the Application-layer move but deferred the strip because ~15 call sites need service-routing migration (and a new `IUserEmailService.GetNotificationEmailAsync` surface). Tracked as the single biggest cross-cutting cleanup in §15i.
- **Identity framework surface** — controllers (`AuthController`, `ManageController`, `DevLoginController`) may inject `UserManager<User>` / `SignInManager<User>` directly per the §2a exception. Non-controller code routes through `IUserService`.
- **GDPR:** `UserService` implements `IUserDataContributor`; `ExpectedContributorTypes` in `GdprExportDependencyInjectionTests` enforces registration.
- **Option A (no decorator)** is documented in `docs/superpowers/specs/2026-04-21-issue-511-user-migration.md`.

### Touch-and-clean guidance

- When touching `TeamService`, `GoogleWorkspaceSyncService`, `SendBoardDailyDigestJob`, `SyncLegalDocumentsJob`, `SystemTeamSyncJob`, `SuspendNonCompliantMembersJob`, or `ProfileController` — do **not** add new reads of `user.UserEmails`, `user.Profile`, `user.TeamMemberships`, or `user.GetEffectiveEmail()` via nav properties. Route through `IUserEmailService` / `IProfileService` / `ITeamService` / `IUserService.GetNotificationEmailAsync`. The existing nav reads are grandfathered under the deferred-strip bucket; no new ones land.
- Do **not** inject `HumansDbContext` into any Application-layer service under `Humans.Application.Services.Users/`. Use `IUserRepository`.
- `/Unsubscribe/{token}` must stay unauthenticated. If new unsubscribe-adjacent surfaces are added, route them through `IUnsubscribeTokenProvider` rather than opening additional unauthenticated endpoints.
- Event-participation writes must all go through `IUserService.UpsertParticipationAsync`. `TicketSyncService` already does this as of PR #545c; new writers must follow the same pattern.
