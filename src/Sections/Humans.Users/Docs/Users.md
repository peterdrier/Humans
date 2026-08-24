<!-- freshness:triggers
  src/Sections/Humans.Users/**
  src/Sections/Humans.Users.Contracts/**
-->
<!-- freshness:flag-on-change
  User entity surface, OAuth-vs-magic-link provisioning, event-participation monotonicity, unsubscribe token rules, the Identity-framework §2a exception, the Profile data model, contact-field visibility tiers, UserInfo caching/invalidation, GDPR contributor wiring, and merge/duplicate flows — review when Humans.Users services/entities/controllers/views change.
-->

# Users — Section Invariants

`src/Sections/Humans.Users` (+ `.Contracts`) owns two formerly separate section docs, merged
when the project was carved at nobodies-collective/Humans#866 (G5): the **User/Identity**
aggregate and **Profiles**. They share a project because they share the `UsersDbContext` and
because `CachingUserService` stitches User-owned columns into the unified `UserInfo` read model; the invariants below
are still stated separately, because they are separate invariants.

The agent's `fetch_section_guide` tool reaches this file under the canonical key `Users`, with
`Profiles` and `Profile` as aliases (`AgentSectionKeys`). The old `docs/sections/Users.md` and
`docs/sections/Profiles.md` are gone.

---

# Part 1 — Users / Identity


The User aggregate and its identity surface. Profile-adjacent User properties (Google email preference, contact import, campaign unsubscribe) are documented under [Part 2 — Profiles](#part-2--profiles) because `CachingUserService` stitches them into `UserInfo`; this section owns the entity itself, the identity framework extensions, and cross-event participation state.

## Concepts

- A **User** is the ASP.NET Core Identity aggregate for every human in the system. Authenticates via Google OAuth or magic link. The entity extends `IdentityUser<Guid>`.
- **Account Provisioning** creates new `User` rows from an OAuth login (`ExternalLoginService.CompleteExternalLoginAsync`, dispatched from `AccountController.ExternalLoginCallback`), a magic-link signup (`AccountController.CompleteSignup`), or an import (`AccountProvisioningService.FindOrCreateUserByEmailAsync` for ticket / MailerLite contacts). All three paths look up an existing user across `UserEmail` records and `User.Email` (with gmail/googlemail equivalence) before creating a new row.
- **Unsubscribe** is the one-click email opt-out surface (`/Unsubscribe/{token}`) that updates the user's per-category `CommunicationPreference` via Profile's `ICommunicationPreferenceService`. New category-aware tokens redirect to the comms-preferences page; legacy campaign-only tokens (`CampaignUnsubscribe` Data Protection purpose) show the confirmation page and are treated as `MessageCategory.Marketing`. RFC 8058 one-click POST (`/Unsubscribe/OneClick`) also routes through the same service. No login required.
- **Event Participation** is a per-user, per-year record (`Ticketed`, `Attended`, `NotAttending`, `NoShow`) derived from ticket sync, user self-declaration, and admin backfill. Owned by Users because the participation key is User + Year, not Ticket or Shift.
- **Account deletion** is a 30-day grace period: `User.DeletionRequestedAt` + `DeletionScheduledFor` are stamped when a user requests deletion (with optional `DeletionEligibleAfter` for ticket-holder event holds). `ProcessAccountDeletionsJob` runs daily and calls `IAccountDeletionService.AnonymizeExpiredAccountAsync` for each due user. That job and `SuspendNonCompliantMembersJob` are `Humans.Users/Contracts/*Job.cs` since G5 lane 5b-4 (nobodies-collective/Humans#866) — `public` because Shell names each concrete type when it registers and schedules it; the registration sites stay in Shell.
- Identity sub-tables (renamed to `users`, `user_claims`, `user_logins`, `user_tokens`, `roles`, `user_roles` per Postgres convention in `UsersDbContext.OnModelCreating`) are managed by ASP.NET Identity's `UserManager<User>` / `SignInManager<User>`. Controllers may inject those framework services directly (design-rules §2a exception).

## Data Model

### User

**Table:** `users` (ASP.NET Identity table, renamed from `AspNetUsers` in `UsersDbContext.OnModelCreating`).

Extends `IdentityUser<Guid>` with project-specific columns.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Email | string | Computed from `UserEmails` (first verified, primary-preferred); falls back to `base.Email` when collection is not loaded. Override — not a plain column. |
| DisplayName | string | User-provided display name (max 256, required). Name of last resort — see `BurnerName`. |
| BurnerName | string? | The name we render (max 256). Dual-written from `Profile.BurnerName` on every profile save; null on rows not yet backfilled. Resolution order into `UserInfo.BurnerName` is `User.BurnerName` → `Profile.BurnerName` → `DisplayName`. |
| FirstName | string? | Legal given name (max 256). Dual-written from `Profile.FirstName`; `Profile` is still the read source. |
| LastName | string? | Legal family name (max 256). Dual-written from `Profile.LastName`; `Profile` is still the read source. |
| PreferredLanguage | string | UI / email locale, default `"en"` (max 10) |
| ProfilePictureUrl | string? | Google profile picture URL (max 2048) |
| CreatedAt | Instant | Set on insert; immutable (`init`) |
| LastLoginAt | Instant? | Most recent login timestamp — distinguishes imported contacts (`null`) from active users |
| MagicLinkSentAt | Instant? | Rate-limit anchor for magic-link sends (see Auth invariants) |
| LastConsentReminderSentAt | Instant? | Rate-limit anchor for the re-consent reminder email |
| ICalToken | Guid? | Token in the user's personal iCal feed URL; regeneratable |
| SuppressScheduleChangeEmails | bool | Per-user opt-out for schedule-change notifications |
| UnsubscribedFromCampaigns | bool | Legacy campaign opt-out flag. **Not** flipped by the current unsubscribe flow — the per-category `CommunicationPreference` table is the live source of truth. |
| GoogleEmailStatus | GoogleEmailStatus | **Deprecated (#687).** Google sync status is now per-address on `UserEmail.GoogleEmailStatus` (the address Google actually rejected), not the user. This column is retained on disk pending a deferred drop migration; `[Obsolete]`, no live reader/writer. |
| ContactSource | ContactSource? | Where imported from (`Manual`, `MailerLite`, `TicketTailor`); null for self-registered users. |
| ExternalSourceId | string? (256) | ID in the external source system (e.g., MailerLite subscriber ID). |
| DeletionRequestedAt | Instant? | When the user requested account deletion |
| DeletionScheduledFor | Instant? | `DeletionRequestedAt + 30 days`; the earliest the job will anonymize |
| DeletionEligibleAfter | Instant? | Optional event-hold floor for ticket holders; the job waits past this date too |
| MergedToUserId | Guid? | Self-referential FK → User. Set on this row when `AccountMergeService.AcceptAsync` folds it into the target. Source rows are tombstones — they outlive their target's lifecycle (`OnDelete: Restrict`) so append-only history (audit log, consent records, budget audit log) stays attributable. Filtered index `IX_users_MergedToUserId` (WHERE NOT NULL) backs the chain-follow lookup. |
| MergedAt | Instant? | When the merge tombstone was applied. Null while live. |
| State | UserState | Lifecycle state and the single source of truth for access, suspension included (`Bare`, `Active`, `DeletePending`, `Suspended`, `Rejected`, `Deleted`, `Merged`, `AdminSuspended`). Required string column (`HasConversion<string>()`, physical default `Bare`); written at each transition and never derived on read. The full app is reachable only when `State == Active`. |

**Deleted C# properties (shadow columns only):** `GoogleEmail` — C# property removed (issue #635 §15i / email-identity-decoupling PR 3). Column kept on disk as EF shadow property (`UserConfiguration.cs:23`) pending deferred column-drop PR per `memory/architecture/no-drops-until-prod-verified.md`. Canonical read path is `UserInfo.GoogleEmail` (derived from `UserEmail.IsGoogle`). Similarly `UserEmail.IsOAuth` is a shadow-only column — `UserEmailProviderBackfillService` still reads it via `EF.Property<T>` (nobodies-collective/Humans#507). The companion `UserEmail.DisplayOrder` shadow column was dropped outright (nobodies-collective/Humans#1217); display sorting is alphabetical on `Email`. Analyzer `HUM0001` (`UserEmailLegacyFieldAnalyzer`) enforces that no code references these deleted properties.

**Profile-adjacent fields documented in `Profiles.md#user-identity-extension`:** `GoogleEmail` (shadow/deleted), `GoogleEmailStatus`, `ContactSource`, `ExternalSourceId`, `UnsubscribedFromCampaigns` are also described there because `CachingUserService` stitches them into `UserInfo`. The canonical field table is here; `Profiles.md` describes `UserInfo` projection semantics.

Computed: `IsDeletionPending => DeletionRequestedAt.HasValue`.

User-suspension state lives on `User.State` (`UserState.Suspended` / `AdminSuspended`); `UserInfo.IsSuspended` is the canonical predicate. Single-user writes go through `IUserService.ApplyProfileOnboardingMutationAsync` with `UserProfileOnboardingMutation.SetSuspension` (called by `HumanLifecycleService`), which delegates to `IUserRepository.SetSuspensionAsync` — that call writes `users.State` and nothing else. The suspension **reason** is not stored on the row: `HumanLifecycleService.SuspendAsync` puts it in the audit log (`AuditAction.MemberSuspended`) and in the `AccessSuspended` notification body. `Profile.AdminNotes` is a general-purpose admin field and is never written by the suspension path. The bulk path is `IUserService.SuspendProfilesForMissingConsentAsync` → `IUserRepository.SuspendManyAsync`, which returns only the users whose state actually moved — a row already `Rejected`/`Merged`/`Deleted` outranks `Suspended` and is left alone. Unsuspending re-classifies from the remaining fields, so a rejected or deletion-pending row does not come back as `Active`. The User entity has no `IsArchived` / `SuspendedAt` / `SuspensionReason` columns; "archive" / "lockout" semantics are achieved by anonymizing identity fields and removing OAuth logins through `IUserService.PurgeAsync` / `AnonymizeExpiredAccountAsync`.

Issue #635 (2026-05-04) **stripped** six User-side cross-domain navs (`User.Profile`, `User.RoleAssignments`, `User.ConsentRecords`, `User.Applications`, `User.TeamMemberships`, `User.CommunicationPreferences`) and the `User.GetEffectiveEmail()` method. Inverse-side EF configurations on each owning entity now own the schema-level FK constraints (e.g., `ProfileConfiguration.HasOne<User>().WithOne().HasForeignKey<Profile>(p => p.UserId)`); the strip is verified non-destructive via a fresh `dotnet ef migrations add` producing an empty `Up()`/`Down()`. Two navs **remain declared** on User: `User.UserEmails` (required by the `User.Email` override per the issue's AC) and `User.EventParticipations` (owned by the Users section itself). The strip is documented here, not test-pinned — a test asserting an entity lacks a nav property is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md).

`User.Email` is overridden on the entity to compute from `UserEmails` (first verified, primary-preferred) — application code reads `user.Email` and gets the canonical address without touching the underlying Identity column. `User.NormalizedEmail` is `[Obsolete]` (issue #635 §15i, diagnostic id `HUM_USER_NORMALIZEDEMAIL`); applications must use `user.Email` or `IUserEmailRepository` for canonical lookups. `User.EmailConfirmed` is also overridden (true when any `UserEmail` is verified). Identity's email-lookup APIs (`UserManager.FindByEmailAsync` / `FindByNameAsync`) are forbidden by `IdentityFindByEmailRestrictionsTests` — application code routes through `IUserEmailService.FindVerifiedEmailWithUserAsync` / `IMagicLinkService.FindUserByVerifiedEmailAsync` instead. The §15i spec proposed a `HumansUserStore` subclass that would reroute those Identity calls; an observability shim (`LoggingUserStoreDecorator`) ran in production for a soak window in 2026-05 and was retired (issue #701) once the data confirmed Identity itself does not internally call these.

`User.GetEffectiveEmail()` was deleted in issue #635 (§15i, 2026-05-04). It was a literal alias for the `User.Email` override; callers were migrated to read `user.Email` directly (or `UserInfo.PrimaryEmail` for canonical reads on a singly-loaded User without UserEmails hydrated).

Every newly created User has a corresponding Profile row materialized inline at the User-creation call site (see Profiles.md "Stub Profile invariant"); until the names are filled the user is `UserState.Bare`. Legacy profile-less users are reconciled via `/Profile/Admin/Backfill`.

### EventParticipation

Per-user, per-year record of event involvement. Derived from ticket sync, user self-declaration, and admin backfill. Owned by Users because the natural key is User + Year, not Order or Shift.

**Table:** `event_participations` (EF configuration lives in `Configurations/Users/EventParticipationConfiguration.cs`, alongside the entity's owning section.)

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK (`init`) |
| UserId | Guid | FK → User. The inverse `User.EventParticipations` collection is declared on `User`; there is no forward `EventParticipation.User` nav. |
| Year | int | Year of the event |
| Status | ParticipationStatus | `NotAttending` (0), `Ticketed` (1), `Attended` (2), `NoShow` (3). Stored as string. |
| Source | ParticipationSource | `UserDeclared` (0), `TicketSync` (1), `AdminBackfill` (2). Stored as string. |
| DeclaredAt | Instant? | Set when the user self-declared (Source = `UserDeclared`); null otherwise. |

**Indexes:** unique on `(UserId, Year)`.

`Attended` is permanent: ticket sync cannot downgrade it. `Ticketed` is removable when the last valid ticket is voided / transferred (`RemoveTicketSyncParticipationAsync`). `NotAttending` (with Source = `UserDeclared`) can only be undone by the same user via `UndoNotAttendingAsync`; ticket sync also overrides it when a ticket is purchased. `NoShow` is a post-event derivation for ticket holders who did not check in.

On account-merge fold, an `(UserId, Year)` collision between source and target keeps the **highest-precedence status** — `Attended` > `Ticketed` > `NoShow` > `NotAttending` — copying the winning row's `Status`/`Source`/`DeclaredAt` onto the target row and deleting the source row (`UserRepository.ReassignEventParticipationToUserAsync`).

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

The entity still carries `TargetUser`, `SourceUser`, and `ResolvedByUser` navigation properties (configured with `HasOne(...).WithMany().HasForeignKey(...)`). They predate the §15i nav-strip work; the merge admin views read them directly today. Strip and route through `IUserServiceRead.GetUserInfosAsync` when this pattern is generalised across the section.

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

`UnsubscribeController` is this section's own controller. Authentication routes are served by `AccountController`, which lives in `Humans.Web/Controllers/` (the Shell) but dispatches into this section's services (`ExternalLoginService`, `IMagicLinkService`, `AccountProvisioningService`):

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

**`UnsubscribeController`** (`/Unsubscribe/*`) — unauthenticated email opt-out:
- `GET /Unsubscribe/{token}` — confirms unsubscribe (legacy campaign or new category-aware token)
- `POST /Unsubscribe/{token}` — submits the confirmation form (legacy path only; new-format tokens redirect to the comms-preferences page on GET)
- `POST /Unsubscribe/OneClick` — RFC 8058 one-click unsubscribe (no anti-forgery token by design)

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone with a valid email | Sign up via OAuth (`/Account/ExternalLogin`) or magic link (`/Account/MagicLinkRequest`). Use `/Unsubscribe/{token}` (no login required). |
| Import jobs (Tickets, MailerLite) | Call `IAccountProvisioningService.FindOrCreateUserByEmailAsync` to materialize contact-only User rows (no `LastLoginAt`). |
| Authenticated human | Read own User row. Self-declare `NotAttending` for the active event year via `IUserService.DeclareNotAttendingAsync` (and undo via `UndoNotAttendingAsync`). Request account deletion. |
| HumanAdmin, Board, Admin | Read any User's deletion / login state. Suspension itself is a Profile concern (see [Part 2 — Profiles](#part-2--profiles). |
| Admin | Trigger account merge flows on the unified **Account Merges** page (`/Users/Admin/AccountMerges`, `UsersAdminAccountMergesController`); `AccountMergeService` + `DuplicateAccountService` are Users-section services. Outside Production only: purge a human via `UsersAdminController.PurgeHuman` (`POST /Users/Admin/{id}/Purge`). |

## Invariants

- OAuth login (`ExternalLoginService.CompleteExternalLoginAsync`, dispatched from `AccountController.ExternalLoginCallback`) checks verified `UserEmails`, then unverified `UserEmails` / `User.Email`, before creating a new account — preventing duplicate accounts when the same email exists on another user in any form. The locked-out branch additionally re-links a stale OAuth login from a merged source account to the active target account.
- `AccountController`, the Development section's `DevLoginController` / `DevPersonaSeeder` / `DevelopmentDashboardSeeder`, and the ASP.NET Identity framework surface may inject `UserManager<User>` and `SignInManager<User>` directly — this is the explicit §2a exception because Identity is a framework concern, not a domain service. Application-layer code (`AccountProvisioningService`, `ExternalLoginService`) may also inject `UserManager<User>` for user creation; everything else routes through `IUserService`.
- Event-participation derivation is monotonic on `Attended`: once an attendee has been checked in, their `EventParticipation.Status = Attended` row cannot be downgraded by ticket sync. `Ticketed`, `NotAttending`, and `NoShow` are mutable.
- `UndoNotAttendingAsync` only succeeds when the existing record is `(Status = NotAttending, Source = UserDeclared)`; an admin backfill or ticket-sync row cannot be undone via the user surface.
- `UserEmail.GoogleEmailStatus = Rejected` is terminal for sync-driven writes (#687): `TrySetGoogleEmailStatusFromSyncAsync` targets the user's canonical verified `IsGoogle` address and refuses to flip a `Rejected` address back to `Valid`. The user clears it by selecting a different Google email — a fresh row that starts `Unknown` — so a rejection never strands sync after the address changes. Status moved off `User` (column deprecated); the user-level repository override (`SetGoogleEmailStatusAsync`) was removed.
- `/Unsubscribe/{token}` is unauthenticated; new-format tokens are validated by Profile's `CommunicationPreferenceService` (signed via ASP.NET Data Protection with the `CommunicationPreferences` purpose), legacy tokens by the `CampaignUnsubscribe` time-limited protector. Token tampering returns `NotFound`; no account enumeration. The RFC 8058 `/Unsubscribe/OneClick` POST is also unauthenticated and skips the anti-forgery token by design.
- `UserService` implements `IUserDataContributor` (design-rules §8) and contributes the User-account slice (Id, Email, DisplayName, PreferredLanguage, GoogleEmail, UnsubscribedFromCampaigns, SuppressScheduleChangeEmails, ContactSource, deletion / created / last-login timestamps) under `GdprExportSections.Account` plus an array of `{ Year, Status, Source, DeclaredAt }` rows under `GdprExportSections.EventParticipations` (every `EventParticipation` row owned by the user) to the GDPR export. Since profile storage was consolidated into `UserService` (nobodies-collective/Humans#745), it also emits the Profiles section's `Profile`, `ContactFields`, `UserEmails`, `VolunteerHistory`, `Languages` and `CommunicationPreferences` slices — see [Part 2 — Profiles](#part-2--profiles).
- A `User` row whose `MergedToUserId` is non-null is a **merge tombstone**: its identity fields are anonymized, its OAuth logins have been re-FK'd to the target, `LockoutEnd` is far-future, but the row itself persists indefinitely so append-only history written under the source id stays resolvable. The self-referential FK is `OnDelete(Restrict)` — deleting a target cannot cascade-delete its source tombstones.

## Negative Access Rules

- Controllers (other than `AccountController` / the Development section's `DevLoginController` / the ASP.NET Identity framework surface) **cannot** inject `UserManager<User>` or `SignInManager<User>`. They go through `IUserService`.
- Services in `Humans.Users/Services/` **cannot** inject a DbContext directly — they go through `IUserRepository` / `IUserEmailRepository`.
- Other Application-layer services **cannot** read or write the `users` / Identity tables directly — they go through `IUserService`.
- Regular humans **cannot** purge any account.
- An Admin **cannot** purge their own account — `UsersAdminController.PurgeHuman` returns the user to the admin-detail page with an error when `user.Id == currentUser.Id`.
- Purge **cannot** run in Production — `UsersAdminController.PurgeHuman` returns `NotFound` when `IWebHostEnvironment.IsProduction()`. Account anonymization (the GDPR-deletion path through `AnonymizeExpiredAccountAsync`) runs in every environment via `ProcessAccountDeletionsJob`.
- A merge-tombstoned `User` (`MergedToUserId` non-null) **cannot** sign in — `LockoutEnd` is bumped far-future during fold. Application code outside `IAccountMergeService` **cannot** clear `MergedToUserId` / `MergedAt` or revive a tombstone.

## Triggers

- **On first OAuth login (no matching account):** `ExternalLoginService.CompleteExternalLoginAsync` (dispatched from `AccountController.ExternalLoginCallback`) creates the `User` via `UserManager.CreateAsync`, attaches the external login, and persists a provider-tagged `UserEmail` row via `IUserEmailService.ReconcileOAuthIdentityAsync` (HUM0005 pins `ExternalLoginService` as its sole caller); `AccountController` then signs the user in via `SignInManager.SignInAsync`. Profile creation happens lazily in the Profile section (see [Part 2 — Profiles](#part-2--profiles).
- **On import (Tickets / MailerLite contact upsert):** `AccountProvisioningService.FindOrCreateUserByEmailAsync` looks up an existing user by `UserEmail` and `User.Email` (with gmail/googlemail equivalence), creates a contact-only `User` + `UserEmail` if no match, layers `User.ContactSource` onto an existing self-registered user when null, and writes an `AuditAction.ContactCreated` audit entry on creation.
- **On magic-link send:** `MagicLinkService` (Auth) stamps `User.MagicLinkSentAt` for rate-limiting (see [Auth.md](../../Humans.Auth/Docs/Auth.md)).
- **On unsubscribe click / RFC 8058 one-click:** `UnsubscribeService.ConfirmUnsubscribeAsync` calls Profile's `ICommunicationPreferenceService.UpdatePreferenceAsync` to opt the user out of the message category (`Marketing` for legacy tokens, the token's category otherwise). The `User.UnsubscribedFromCampaigns` flag exists but is **not** flipped here — the per-category `CommunicationPreference` table is the source of truth for opt-out.
- **On ticket sync:** `TicketSyncService` calls `IUserService.SetParticipationFromTicketSyncAsync` (or `RemoveTicketSyncParticipationAsync`) for each user with a status delta — never writes `event_participations` directly.
- **On admin participation backfill:** `IUserService.BackfillParticipationsAsync` writes records with `Source = AdminBackfill`.
- **On account-deletion request (user-initiated):** `IAccountDeletionService.RequestDeletionAsync` orchestrates the user-initiated path. Internally calls `IUserService.SetDeletionPendingAsync` to stamp `DeletionRequestedAt` + `DeletionScheduledFor` on the User row, revokes team memberships and governance roles immediately so the user loses access during the 30-day grace period, and sends a confirmation email.
- **On scheduled deletion expiry:** `ProcessAccountDeletionsJob` calls `IAccountDeletionService.AnonymizeExpiredAccountAsync` per due user. Since nobodies-collective/Humans#853 that is a fan-out over `IEnumerable<IUserDataContributor>`, not a hand-wired cascade: it captures the identity slice the caller needs for the confirmation email, then calls `EraseForUserAsync` on every contributor in turn. Sequential, because scoped section DbContexts are not thread-safe (same as the export); the contributor declaring `GdprExportSections.Account` runs last, so sections that still need the human's addresses (the Workspace suspend) can resolve them; ordering is derived from the declarations, not a pinned type list. A contributor that throws aborts the run with the deletion markers still set, so the whole cascade retries the next day. Cross-cutting caches (Teams active-teams + member, role-assignment claims, shift authorization, shift view) are then invalidated — the `UserInfo` cache entry itself refreshes automatically as part of the `IUserService` write path, with no separate invalidator call needed. The job writes the `AuditAction.AccountAnonymized` audit entry and sends the confirmation email.
- **On admin purge (non-Production only):** `UsersAdminController.PurgeHuman` (`POST /Users/Admin/{id}/Purge`) calls `IAccountDeletionService.PurgeAsync`. The orchestrator delegates the actual identity collapse to `IUserService.PurgeOwnDataAsync` (anonymizes the `users` row, drops `UserEmail` rows and `AspNetUserLogins` rows, refreshes the cached `UserInfo` entry), then drops the Teams active-teams cache and per-user role-assignment / shift-authorization caches. Identity-only — does not cascade to Profile rows. (Replaces the pre-PR routing through `IOnboardingService.PurgeHumanAsync` and the renamed `IUserService.PurgeAsync`.)
- **On account merge accept:** `IAccountMergeService.AcceptAsync` (Users section) fans out its `IUserMerge` implementations to re-FK source → target (e.g. `IUserService.ReassignLoginsToUserAsync` for `AspNetUserLogins`, `ReassignEventParticipationToUserAsync` for `event_participations`), then `AnonymizeForMergeAsync` to tombstone the source row by setting `MergedToUserId` + `MergedAt` and bumping `LockoutEnd` far-future so the source can no longer sign in. The source `User` row stays in place as a redirect — it is NOT deleted — so append-only history written under the source id (audit log, consent records, budget audit log) remains attributable.
- **On per-user reads of a target after merge:** `IUserService.GetMergedSourceIdsAsync(targetUserId)` returns the set of source ids whose `MergedToUserId` points at the target. Append-only sections (`IAuditLogService`, `IConsentService`, `IBudgetService.ContributeForUserAsync`) union this set with `targetUserId` before querying so source-tombstoned rows surface for the target.

## Cross-Section Dependencies

Outbound (Users → other sections), split between the foundational `UserService` (no higher-section edges, enforced by `UserArchitectureTests.UserService_HasNoOutboundEdgeToHigherLevelSections`) and `AccountDeletionService` (the deletion cascade orchestrator that explicitly bridges higher-level sections):

**From `UserService` / `UnsubscribeService`:**
- **Profiles:** `ICommunicationPreferenceService.UpdatePreferenceAsync` (called from `UnsubscribeService`). `communication_preferences` is not one of the 8 tables `CachingUserService` projects into `UserInfo`, so this write needs no cache invalidation.

**From `AccountDeletionService` (cascade orchestrator — `Humans.Users/Services/`):**
- **Gdpr:** `IEnumerable<IUserDataContributor>` — since nobodies-collective/Humans#853 the erasure itself is a fan-out, not a hand-wired list of section calls. Each contributor erases what its own section owns; the orchestrator names no section to do it.
- **Auth:** `IRoleAssignmentService.RevokeAllActiveAsync` (grace-period revoke on the deletion *request*), `IRoleAssignmentClaimsCacheInvalidator.Invalidate`.
- **Teams:** `ITeamService.RevokeAllMembershipsAsync` (same grace-period revoke), `RemoveMemberFromAllTeamsCache`.
- **Shifts:** `IShiftAuthorizationInvalidator.Invalidate`, `IShiftViewInvalidator.InvalidateUser` — cross-cutting cache drops only; the Shifts data itself is erased by that section's own contributor.

Inbound (other sections → Users) — the typical direction:

- **Shifts / Tickets:** call `IUserService.DeclareNotAttendingAsync` (Home controller for self-declaration), `SetParticipationFromTicketSyncAsync`, `RemoveTicketSyncParticipationAsync`, `BackfillParticipationsAsync`. Direct writes to `event_participations` are forbidden.
- **Notifications, Email, AuditLog:** call `IUserServiceRead.GetUserInfosAsync` (batched `UserInfo` lookup — the caching decorator's dict already carries the full payload, including `UserEmails`) to resolve recipient identity/email without navigating cross-domain navs.
- **Account-deletion job (`ProcessAccountDeletionsJob`, `Humans.Users/Jobs/`):** calls `IUserService.GetAccountsDueForAnonymizationAsync` + `IAccountDeletionService.AnonymizeExpiredAccountAsync`.
- **Account merge (`IAccountMergeService.AcceptAsync`, Users section):** the `IUserMerge` fan-out calls `IUserService.ReassignLoginsToUserAsync`, `ReassignEventParticipationToUserAsync`, and `AnonymizeForMergeAsync` to fold a source User into a target.
- **Audit Log / Consent / Budget:** call `IUserService.GetMergedSourceIdsAsync(targetUserId)` to chain-follow merge tombstones on per-user reads of append-only entities.

## Architecture

**Owning services:** `UserService`, `AccountProvisioningService`, `UnsubscribeService`, `AccountDeletionService`, `AccountMergeService` + `DuplicateAccountService` (the one ordered merge engine and the stateless duplicate detector, moved from Profiles in the account-merge consolidation; `AccountMergeService` is backed by `IAccountMergeRepository` for `account_merge_requests` and contributes the `AccountMergeRequests` GDPR slice), `ExternalLoginService` (the OAuth-callback decision ladder lifted out of `AccountController` per HUM0031/#857; sole caller of `IUserEmailService.ReconcileOAuthIdentityAsync`) — all in `Humans.Users/Services/`.
**Owned tables:** `users`, `user_claims`, `user_logins`, `user_tokens`, `roles` (legacy), `user_roles` (legacy), `role_claims` (legacy), `event_participations`, `account_merge_requests`.
**Status:** (A) Migrated (peterdrier/Humans PR #243 for issue nobodies-collective/Humans#511, 2026-04-21). Account merge fold support added 2026-05-01 (User.MergedToUserId / MergedAt; Reassign + AnonymizeForMerge methods).

- `UserService`, `AccountProvisioningService`, `UnsubscribeService` live in `Humans.Users/Services/` and never import `Microsoft.EntityFrameworkCore`. `AccountProvisioningService` does inject `UserManager<User>` per the §2a exception (Identity owns the password hash / security stamp surface).
- `IUserRepository` (impl `Humans.Users/Data/Repositories/UserRepository.cs`) owns the SQL surface for `users` plus `event_participations` (the natural key is User). `IUserEmailRepository` is the parallel surface for `UserEmail` (owned by Profiles but read/written from Users for lookup + OAuth-email lock-step).
- **Decorator decision — caching decorator added 2026-05-13 (issue #703).** `CachingUserService` (Singleton, `Humans.Users/Data/`) owns a `ConcurrentDictionary<Guid, UserInfo>` of the unified read-model spanning the 8 contributing tables (`users`, `user_emails`, `event_participations`, AspNet `user_logins`, `profiles`, `contact_fields`, `profile_languages`, `volunteer_history_entries`). Pattern mirrors `CachingProfileService` exactly: dict hits served synchronously; cache miss refills via the inner Scoped `UserService` (keyed `"user-inner"`); writes through `IUserService` refresh the affected entry (e.g. sign-in `LastLoginAt` now flows through `IUserService.RecordLoginAsync`, whose decorator refreshes the entry directly — no longer a `UserManager.UpdateAsync` write); Identity-machinery writes that bypass the service surface (`UserManager.UpdateAsync`, OAuth `UserEmail` row creation) are caught by `UserInfoSaveChangesInterceptor` (EF `SaveChangesInterceptor`, registered on both the scoped and factory contexts) and routed through `IUserInfoInvalidator.InvalidateAsync`. `CachingUserService` itself implements `IHostedService` (via `TrackedCache`) and populates the dict at startup; load-all reads call `EnsureWarmed` / `EnsureWarmedAsync` and recover transparently if startup warmup hasn't completed. The former `FullProfile` cache (`CachingProfileService`) has since been retired — `UserService` absorbed `ProfileService`'s storage-mutation methods, so `UserInfo` is now the single cache and `ProfileService` was pared down to profile-picture storage only.
- **Read/write interface split.** `IUserServiceRead` (9 methods: `GetUserInfoAsync`, `GetUserInfosAsync`, `GetAllUserInfosAsync`, `SearchUsersAsync`, `GetOnsiteUsersAsync`, `GetMergedSourceIdsAsync`, `GetAllParticipationsForYearAsync`, `GetAccountsDueForAnonymizationAsync`, `GetByEmailOrAlternateAsync`) is the cross-section read surface — only `UserInfo` / `HumanSearchResult` / `OnsiteUserRow` / `UserParticipationRow` projections plus Guid primitives, no EF entities. `IUserService : IUserServiceRead` adds writes, cache invalidation, entity-returning reads, and Users-internal reads. External sections that only read inject `IUserServiceRead`. Enforcement is advisory pending the Roslyn analyzer; arch tests in `UserArchitectureTests` pin the inheritance + same-singleton DI. See `memory/architecture/section-read-write-split.md` and Teams' PR #678.
- **Cross-domain navs (post-§15i strip):** only `User.UserEmails` (kept for the `User.Email` override) and `User.EventParticipations` (owned by Users) remain declared. The other six (`Profile`, `RoleAssignments`, `ConsentRecords`, `Applications`, `TeamMemberships`, `CommunicationPreferences`) and `GetEffectiveEmail()` were deleted in issue #635 (2026-05-04). Inverse-side EF configurations on each owning entity now own the schema-level FK constraints.
- **Identity framework surface** — `AccountController` lives in `Humans.Web/Controllers/` (the Shell), not in this section, and injects `UserManager<User>` / `SignInManager<User>` directly per the §2a exception, as do the Development section's dev-login controller and its two account-creating seeders. `UnsubscribeController` is this section's only controller. There is no `AuthController` or `ManageController` class — magic-link orchestration lives in `IMagicLinkService` (Auth section) and account self-management lives across `AccountController` and Profile views. Non-controller code routes through `IUserService`.
- **GDPR:** `UserService` implements `IUserDataContributor` and contributes the `GdprExportSections.Account` and `EventParticipations` slices, plus the Profiles-section slices it absorbed in #745 (`Profile`, `ContactFields`, `UserEmails`, `VolunteerHistory`, `Languages`, `CommunicationPreferences`); `ExpectedContributorTypes` in `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` enforces registration (design-rules §8).
- **Resource set:** `UsersResource.{resx,ca,de,es,fr,it}` at the project root (folder and namespace must agree — nobodies-collective/Humans#1365) — 441 keys carved out of `SharedResource` at nobodies-collective/Humans#1050, covering the whole assembly (both parts of this doc). Views bind it as `Localizer` and `SharedResource` as `SharedLocalizer` in `Views/_ViewImports.cshtml`; `UsersResource.cs`'s remarks record the leave-or-duplicate decision for every key that stayed. What stayed and why: `Common_`, `Nav_`, `Dashboard_`, `Validation_` and `Privacy_` are generic; `Admin_` is rendered by the Admin shell frame too; `Application*_`/`ApplicationStatus_` are also rendered by Governance's copy of the tier form; `Teams_`/`MyTeams_`/`TeamDetail_` and `Search_` belong to other renderers; `Enum_EmailOutboxStatus_` is Humans.Email's set, reached through `SharedLocalizer.EnumDisplay`. `Views/Profile/Edit.cshtml` keeps `IStringLocalizer<ShiftsResource>` for its two shift-preference strings rather than duplicate the copy. `UserArchitectureTests.SectionTypesLocalizeThroughTheSectionsOwnResourceSet` guards the constructor side; `UsersPageRenderTests` asserts no raw key reaches the HTML, in English and in Spanish.
- **Architecture tests:**
  - `tests/Humans.Users.Tests/Architecture/UserArchitectureTests.cs` — pins UserService/AccountProvisioningService/UnsubscribeService, no DbContext, IUserRepository required, and the two-marker localizer guard.
  - Analyzer `HUM0001` (`UserEmailLegacyFieldAnalyzer`, tested by `tests/Humans.Analyzers.Tests/UserEmailLegacyFieldAnalyzerTests.cs`) — flags any reference to the deleted shadow properties (`User.GoogleEmail`, `UserEmail.IsOAuth`, `UserEmail.DisplayOrder`), replacing the retired IL-scan test.
  - Analyzer `HUM0003` (`IdentityFindByEmailAnalyzer`, tested by `tests/Humans.Analyzers.Tests/IdentityFindByEmailAnalyzerTests.cs`) — flags `UserManager.FindByEmailAsync` / `FindByNameAsync` calls outside the allowed sites, replacing the retired IL-scan test; call sites route through `IUserEmailService.FindVerifiedEmailWithUserAsync` instead.
  - The dedicated `AccountDeletionArchitectureTests.cs` (namespace + no-DbContext pin) was removed at the G5 move; `AccountDeletionService` is covered functionally by `tests/Humans.Users.Tests/Services/AccountDeletionServiceTests.cs`.
- The original Option A (no decorator) decision was reversed by issue nobodies-collective/Humans#703 (2026-05-13) when measured read traffic on the `users` table exceeded the `Profile` decorator's traffic by ~7×.

### Touch-and-clean guidance

- After issue #635 (§15i nav strip): `user.Profile` / `user.TeamMemberships` / `user.RoleAssignments` / `user.Applications` / `user.ConsentRecords` / `user.CommunicationPreferences` / `user.GetEffectiveEmail()` no longer exist as User-side navs/method — readers route through `IUserService` / `ITeamService` / `IRoleAssignmentService` / etc. or use `user.Email` (which still overrides via the surviving `UserEmails` collection). When touching `TeamService` / `GoogleWorkspaceSyncService` / `ProfileController` and the four notification jobs, prefer `IUserEmailRepository.GetByUserIdReadOnlyAsync` / `IUserServiceRead.GetUserInfosAsync` over reaching into the `UserEmails` nav directly.
- Do **not** inject a DbContext into any service under `Humans.Users/Services/`. Use `IUserRepository` / `IUserEmailRepository`.
- `/Unsubscribe/{token}` and `/Unsubscribe/OneClick` must stay unauthenticated. If new unsubscribe-adjacent surfaces are added, route them through `IUnsubscribeService` (which delegates token validation to Profile's `ICommunicationPreferenceService` / the legacy `CampaignUnsubscribe` Data Protection purpose) rather than opening additional unauthenticated endpoints.
- Event-participation writes must all go through one of `IUserService.DeclareNotAttendingAsync`, `UndoNotAttendingAsync`, `SetParticipationFromTicketSyncAsync`, `RemoveTicketSyncParticipationAsync`, or `BackfillParticipationsAsync`. `TicketSyncService` already does this as of nobodies-collective/Humans#545; new writers must follow the same pattern. The repository-level `UpsertParticipationAsync` is internal to the section.

---

# Part 2 — Profiles


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
- **Duplicate Account Detection** scans for email addresses appearing on multiple accounts (across `User.Email` and `UserEmail.Email`, with gmail/googlemail equivalence). Detection and resolution now live in the **Users** section; admins act on candidates from the unified **Account Merges** page (`/Users/Admin/AccountMerges`).
- **Email Problems** scans every UserEmail invariant violation (multi/zero IsPrimary or IsGoogle, unverified rows, cross-user collisions, orphan rows, ghost AspNetUserLogins). Reads source-of-truth via `IUserService.GetAllUserInfosAsync` (the `UserInfo` cache). Read-only admin surface with deep-links into the per-user `/Profile/{userId}/Admin/Emails` diagnostic for orphan/ghost remediation; the cross-user merge action shares its kernel with `IAccountMergeService.AcceptAsync`.
- **Account Merge** consolidates two accounts into one, transferring all associated data (emails, contact fields, CV entries, role assignments, memberships) to the surviving account.

## Data Model

### User (Identity extension)

User is owned by the **Users/Identity** section; the properties below are the profile-adjacent extensions that Profile consumers read most often. Field-level ownership still belongs here because `CachingUserService` stitches them into `UserInfo`.

#### Google email preference

`User.GoogleEmail` C# property has been removed (issue #635 §15i). The `GoogleEmail` column is kept on disk as an EF shadow property (`UserConfiguration.cs:23`) pending a deferred column-drop PR per `memory/architecture/no-drops-until-prod-verified.md`. The canonical read path is `UserInfo.GoogleEmail` (derived from `UserEmail.IsGoogle` rows). The `GetGoogleServiceEmail()` and `GetEffectiveEmail()` methods have been removed from `User`; callers use `UserInfo.GoogleEmail` and `UserInfo.PrimaryEmail` respectively.

Google sync status is per-address on `UserEmail.GoogleEmailStatus` (`GoogleEmailStatus` enum stored as string, default `Unknown`) — it belongs to the address Google rejected, not the user (#687). Set to `Rejected` on a permanent Google API error for that address; selecting a different Google email is a fresh `Unknown` row, so switching address resets sync naturally. The legacy `User.GoogleEmailStatus` column is `[Obsolete]` and retained on disk pending a deferred drop migration; no live reader/writer.

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
| DietaryPreference | string? (200) | null | Meal preference. Moved here from VolunteerEventProfile. Edited on `/Profile/Me/Edit` + `/Profile/Me/DietaryMedical`. Surfaced on `UserInfo`. |
| Allergies / Intolerances | List&lt;string&gt; (jsonb) | [] | Food allergies / intolerances (+ `AllergyOtherText` / `IntoleranceOtherText` for free-text "Other"). Moved here from VolunteerEventProfile. |
| MedicalConditions | string? (4000) | null | **GDPR Art. 9** health data. On the cached `UserInfo` but gated at every render/serialize surface by the `MedicalDataViewer` policy (Admin / NoInfoAdmin). Owner edits their own on `/Profile/Me/DietaryMedical`. |
| ProfilePictureContentType | string? (100) | null | MIME type of the stored picture. Doubles as the "has custom picture?" predicate (`HasCustomProfilePicture`) and supplies the file extension — the picture bytes themselves live on the filesystem. |
| ContributionInterests | string? | null | Skills / availability statement (publicly visible on profile). |
| BoardNotes | string? | null | Notes from the human intended for the Board (self + Board only). |
| AdminNotes | string? (4000) | null | Admin-only notes (not visible to the human). |
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

Cross-domain nav `Profile.User` is **stripped** per design-rules §15i. Consumers resolve User data via `IUserServiceRead.GetUserInfosAsync`. Aggregate-local navs `ContactFields`, `VolunteerHistory`, and `Languages` are kept.

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
| Email | string (256) | Required. **Mutation has exactly one path:** `IUserEmailService.ReconcileOAuthIdentityAsync(userId, provider, providerKey, claimEmail, claimEmailVerified, ...)`, called only by `ExternalLoginService` (the OAuth sign-in callback's decision service, dispatched to from `AccountController.ExternalLoginCallback`). See [`memory/architecture/email-mutation-paths.md`](../../../../memory/architecture/email-mutation-paths.md). No admin flow, profile UI flow, or sync job rewrites this field; renames self-heal on the user's next Google sign-in |
| IsVerified | bool | Required |
| Provider | string? (64) | OAuth provider that owns this row when the user signed in via OIDC ("Google" today; future Apple/Microsoft). Null when no OAuth identity is linked. Single-row-per-(Provider, ProviderKey) is service-enforced |
| ProviderKey | string? (256) | OAuth subject/key (OIDC `sub`) for the linked identity. Stable across Google Workspace email renames. `(Provider, ProviderKey)` is the only legitimate match key for rewriting `Email` (via `UserEmailRepository.UpdateEmailAsync`) |
| IsGoogle | bool | Canonical Google Workspace identity row (used by Google sync and Workspace admin). Auto-maintained by `EnsureGoogleInvariantAsync` on every UserEmail mutation — precedence is @nobodies.team row > existing IsGoogle row (Id-stable) > most-recent verified. Admin can override via the Profile email grid (`SetGoogle`/`ClearGoogle`). At-most-one-true-per-UserId is service-enforced |
| IsPrimary | bool | Exactly one verified email per user is the system-notification target. Service-enforced via `EnsurePrimaryInvariantAsync`; column persists under legacy name `IsNotificationTarget` per `no-column-drops-for-decoupling.md` |
| Visibility | ContactFieldVisibility? | Stored as string (max 50); null hides the email from profile view |
| VerificationSentAt | Instant? | Last time a verification email was sent (rate limiting) |
| CreatedAt / UpdatedAt | Instant | Maintained by `UserEmailService` |

**Indexes:** `UserId`; **unique partial index** on `Email` filtered to `IsVerified = true` (Postgres `"IsVerified" = true`) — prevents email squatting across accounts.

**Shadow properties (column-only, no C# surface):** `IsOAuth` (bool) was dropped (nobodies-collective/Humans#507), alongside the retirement of the one-shot `UserEmailProviderBackfillService` that was its sole reader. The companion `DisplayOrder` (int) column was dropped (nobodies-collective/Humans#1217); display sorting is alphabetical on `Email`.

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
| UpdateSource | string (100) | "Profile" (signed-in profile UI), "Guest" (signed-in Guest dashboard, profileless), "MagicLink" (anonymous unsubscribe-token endpoints), "OneClick" (RFC 8058 List-Unsubscribe), "Default" (lazy seed), "DataMigration" |
| SubscribedAt | Instant? | Stamped by `CommunicationPreferenceService` on the first opt-in transition or first sync-driven write; never overwritten while non-null |

**Unique constraint:** `(UserId, Category)`. **Indexes:** `UserId`.

Defaults are created lazily by `CommunicationPreferenceService` on first read. All active categories default to opted-in (`OptedOut = false`) except Marketing, which defaults to opted-out. System and CampaignCodes are always on (cannot be opted out).

### VolunteerHistoryEntry (CV Entry)

**Table:** `volunteer_history_entries`

Sub-aggregate of Profile — no separate service. Written through `IProfileEditorService.SaveProfileAsync` (which calls `IUserService.SaveProfileVolunteerHistoryAsync`); read via `UserInfo.VolunteerHistory`.

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

Self-service profile functionality lives under `/Profile`; human administration now lives under `/Users/Admin`:

| Route | Purpose |
|-------|---------|
| `/Profile/Me` | View own profile |
| `/Profile/Me/Edit` | Edit own profile |
| `/Profile/Me/Emails` | Email management |
| `/Profile/Me/Emails/ClearGoogle`, `/Profile/Me/Emails/ClearPrimary` | Self-recovery — drop a single row's `IsGoogle`/`IsPrimary` flag (only surfaced in UI on N>1 violation; auth is self-or-admin) |
| `/Profile/{id}/Admin/Emails/ClearGoogle`, `/Profile/{id}/Admin/Emails/ClearPrimary` | Admin remediation — drop a single row's flag without auto-promoting a successor |
| `/Profile/{id}/Admin/Emails/Verify` | Admin manual verification (`PolicyNames.AdminOnly`) — marks a pending plain UserEmail row verified without consuming a token; creates a merge request when the address is already verified on another account |
| `/Profile/Me/ShiftInfo` | Shift preferences |
| `/Profile/Me/DietaryMedical` | Dietary preference, allergies, intolerances, medical conditions (per spec [#279](features/dietary-medical-nudge.md); dashboard nudge surfaces this when a human has an active 6h+ signup) |
| `/Profile/Me/CommunicationPreferences` | Per-category email/in-app communication preferences |
| `/Profile/Me/Notifications` | Permanent redirect to `/Profile/Me/CommunicationPreferences` |
| `/Profile/Me/Privacy` | Privacy / deletion |
| `/Profile/Me/DownloadData` | GDPR Article 15 JSON export download |
| `/Profile/Me/Outbox` | Own email outbox |
| `/Profile/{id}` | View another human's profile |
| `/Profile/{id}/Popover` | Quick profile popup |
| `/Profile/{id}/SendMessage` | Send facilitated message |
| `/Users/Admin/{id}` | Admin detail view |
| `/Users/Admin/{id}/Outbox` | Admin view of person's outbox |
| `/Users/Admin/{id}/Suspend` | Suspend member |
| `/Users/Admin/{id}/Reject` | Reject signup |
| `/Users/Admin/{id}/Roles/*` | Role management |
| `/Users/Admin` | Admin list of all humans |
| `/Users/Admin/Roles` | System-wide role-assignment roster, filterable by role (HumanAdminBoardOrAdmin). Relocated from `/Governance/Roles` — `role_assignments` is owned by Auth, not Governance; the roster lives beside the per-human role management surface |
| `/Profile/Search` | People search |
| `/Profile/Picture` | Profile picture endpoint |
| `/api/profiles/search` | API search endpoint |
| `/Profile/Admin/Backfill` | Stub-Profile backfill tool — idempotent count-and-bulk-create for profile-less users (`ProfileBackfillAdminController`, `AdminOnly`) |

Admin-only flows for the section's cross-account hygiene (routes pre-date `memory/architecture/no-admin-url-section.md` — not yet moved to `/<Section>/Admin/*`):

| Route | Purpose |
|-------|---------|
| `/Users/Admin/AccountMerges` | Unified account-merge queue — pending merge requests **and** detected duplicate pairs (`UsersAdminAccountMergesController`, **Users** section — see [Part 1 — Users / Identity](#part-1--users--identity). Admin picks the survivor; the other account is folded in and tombstoned. Replaces the retired `/Admin/MergeRequests`, `/Admin/DuplicateAccounts`, and `/Profile/Admin/EmailProblems/{Compare,Merge}` paths. |
| `/Profile/Admin/EmailProblems` | List UserEmail invariant violations across all accounts (`ProfileAdminController`) |
| `/Profile/Admin/EmailProblems/DeleteOrphanEmail` | POST — delete a single orphan UserEmail row |
| `/Profile/Admin/EmailProblems/DeleteGhostLogins` | POST — delete every AspNetUserLogins row for a userId with no UserEmails |
| `/Users/Admin/Debug` | Flat paginated/sortable table of every user, every column derived from the cached `UserInfo` snapshot — no secondary queries (`UsersAdminDebugController`, `AdminOnly`) |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View and edit own profile, manage own emails, manage own contact fields, upload profile picture, set notification and communication preferences, request data export (GDPR Article 15), request account deletion |
| Any active human | View other active humans' profiles (contact fields restricted by per-field visibility). Send facilitated messages to other humans. Search for humans |
| Coordinator (any team) or PrivilegedSignupApprover | On another human's `/Profile/{id}`, view the **Sent messages** panel — a history of in-platform messages sent to that human (`AuditAction.FacilitatedMessageSent`, up to 50 entries), rendered by `<vc:audit-log layout="table">` with `column-labels` carrying the page's own `Common_Date`/`Common_Sender`/`Common_Preview` — the controller decides visibility, AuditLog owns the read, Profile owns the copy. Not shown on own-profile views. |
| HumanAdmin, Board, Admin | View any profile with full detail. Manage humans via admin pages (suspend, unsuspend, approve volunteer, reject signup, view audit log, add or end role assignments). (Membership tier changes go through tier applications in Governance, not the profile admin page.) |
| Admin | Review duplicate-account candidates and approve/reject `AccountMergeRequest`s at the unified `/Users/Admin/AccountMerges` queue (`PolicyNames.AdminOnly`; **Users** section — see [Part 1 — Users / Identity](#part-1--users--identity). |
| Admin (non-production only) | Purge a human and all associated data |

## Invariants

- `Profile.DietaryPreference` is stored as free text (`varchar(200)?`), not a constrained enum. The `/Profile/Me/Edit` and `/Profile/Me/DietaryMedical` radio groups constrain the UI to `DietaryOptions.DietaryPreferences` (Omnivore / Vegetarian / Vegan / Pescatarian), but neither `ProfileController` nor `UserService.SaveDietaryMedicalAsync` re-checks membership on POST — any non-blank string persists. Deliberate: legacy free-text values predating [#279](features/dietary-medical-nudge.md) stay readable without a data migration. Allergies are the exception — the Edit path filters them against `DietaryOptions.AllergyOptions` before saving.
- Every authenticated human can edit their own profile regardless of membership status (available during onboarding).
- Contact field visibility is enforced per-field: a human viewing their own profile sees everything. Board members see everything. Coordinators see CoordinatorsAndBoard-level and below. Shared-team members see MyTeams-level and below. Other active members see only AllActiveProfiles fields.
- Birthday stores month and day only — never year. UI text uses "birthday", not "date of birth".
- Membership tier (Volunteer, Colaborador, Asociado) is tracked on the profile, not as a role assignment.
- Consent check status on the profile gates Volunteer activation: unset until all consents are signed, then Pending, Cleared, or Flagged.
- App access is gated by the stored `User.State` (`UserState`: Bare/Active/DeletePending/Suspended/Rejected/Deleted/Merged/AdminSuspended). The old `profiles.state` column was dropped in nobodies-collective/Humans#844.
- Profile deletion request sets `User.DeletionRequestedAt` and `User.DeletionScheduledFor = now + 30 days` on the User record. Team memberships and governance role assignments are revoked immediately. Actual data purge is deferred to a background job.
- Data export returns all personal data as a JSON download (GDPR Article 15). `AccountMergeService` is this section's own `IUserDataContributor` implementation per design-rules §8a; the profile slices themselves are emitted by `UserService` (Users section) since profile storage was consolidated there in nobodies-collective/Humans#745. `ProfileService` implements no contributor interface. The orchestration lives in `GdprExportService`.
- Profile pictures are stored on the filesystem via `IFileStorage` (key `uploads/profile-pictures/{profileId}{.ext}`) — the only store, since nobodies-collective/Humans#528 dropped the phase-1 `Profile.ProfilePictureData` DB fallback (issue nobodies-collective#527). `GetProfilePictureAsync` checks `ProfilePictureContentType` as the GDPR gate: null → 404, even if a stale file exists on disk. Uploaded images are validated against an allowed-content-type set (JPEG, PNG, WebP, HEIC/HEIF, AVIF) and a 20 MB upload cap, then resized by `ProfilePictureProcessor` to a long-side of 1000 px and re-encoded as JPEG before persistence.
- `CachingUserService` (Singleton) and `IUserInfoInvalidator` must resolve to the **same** instance — both registrations point to the single decorator. Two instances would split the `ConcurrentDictionary<Guid, UserInfo>` cache and silently lose invalidations.
- Purging a human permanently deletes the account and all associated data, including severing the OAuth link so the next Google login creates a fresh account. Purge is disabled in production environments. No one can purge their own account.
- Duplicate account detection applies gmail/googlemail equivalence when scanning for address collisions.
- `AccountMergeService` writes and `DuplicateAccountService` reads go through the Profile section's repositories and `IUserService` — never through cross-section `DbSet` reads.
- `AccountMergeService.AcceptAsync` is the **fold-into-target** orchestrator: it re-FKs every owning section's user-scoped rows from source to target via per-section `Reassign…ToUserAsync` methods, then tombstones the source User row (sets `MergedToUserId` + `MergedAt` via `IUserService.AnonymizeForMergeAsync`) — it does NOT delete or wipe the source. Append-only history (audit log, consent records, budget audit log) stays at source by design and is surfaced via chain-follow reads.
- The preferred-language flag (rendered next to a person's name on `ProfileCard` and `_HumanPopover`) is visible only to `HumanAdmin` / `Board` / `Admin` viewers — general active humans see other people's profile cards and popovers without it. Self-view is unaffected (the flag isn't shown there in the first place; preferred language is editable on `/Profile/Me/Edit`).
- The authenticated `_HumanPopover` (`GET /Profile/{id}/Popover`) also renders the person's active-season camp name + camp roles, sourced from `ICampServiceRead.GetCampUserInfoAsync`. This block is gated to `AnyAdminRole` viewers (admin-only) and is omitted when the person is not an Active member of any camp this season.
- Profile-less users (legacy contact imports whose Stub Profile has not yet been materialized by the `/Profile/Admin/Backfill` tool) render in `GET /Profile/{id}/Popover` as a sparse card showing display name, best-available verified email, and an "imported · no profile yet" badge — the endpoint never returns 404 for a known `User`. Truly unknown user ids still return 404. In the fallback (no-profile) branch the email is derived directly from canonical `UserEmail` rows (verified primary, then any verified) — `User.Email` is not read on that branch because it silently falls back to the legacy Identity column when `UserEmails` is not loaded (see `User.Email` SILENT-FALLBACK FOOTGUN). The full-profile branch still reads `popoverUser.Email` (pre-existing); same footgun applies and is tracked separately.
- Facilitated messaging (`/Profile/{id}/SendMessage`) is a **conduit, not a mailbox** — Humans relays one-off plain-text emails between humans and stores nothing: no message persistence, no threading, no attachments. (Deliberate contrast with Feedback, which *does* persist a conversation thread.) The body is plain text only — HTML tags are stripped server-side, then the renderer HTML-encodes the text and converts newlines to `<br>`; max 2000 chars. The recipient's address is never disclosed to the sender: the email's `Reply-To` is set to the sender's notification email only when the sender ticks "include my contact info"; otherwise it is omitted and the footer states the human chose not to share contact information.

## Negative Access Rules

- A privileged signup approver **cannot** use the dietary redirect-and-replay flow to sign up anyone but themselves. `ProfileController.ReplayShiftSignupAfterDietaryMedicalSaveAsync` always replays for the current `user.Id`, and `ShiftRoleChecks.IsPrivilegedSignupApprover` only relaxes signup validation (auto-confirm) — it never switches the actor. There is no target-user parameter on the form or on the `returnAction` / `shiftId` / `rotaId` carryover.
- Regular humans **cannot** view suspended profiles.
- Regular humans **cannot** edit another human's profile.
- Regular humans **cannot** see contact fields above their access level on other humans' profiles.
- Non-active humans (still onboarding) **cannot** view other humans' profiles or send messages.
- Any Admin **cannot** purge their own account.
- Purge **cannot** run in production environments (gate on `IWebHostEnvironment`).
- Admins **cannot** establish a new OAuth link on a user's behalf — there is no admin `Link` action under `/Profile/{id}/Admin/Emails/*`. Google authenticates whoever is at the keyboard, and admins must never hold user credentials; linking requires the target user's own session (`POST /Profile/Me/Emails/Link/{provider}`). Admins may `Unlink` existing provider-attached rows (that operates on already-stored data, no OAuth flow).

## Triggers

- When all required legal documents are consented to, consent check status transitions to Pending.
- When consent check status is Cleared, the human is auto-approved as a Volunteer and added to the Volunteers system team.
- When a human requests account deletion, team memberships and governance roles are revoked immediately and `User.DeletionScheduledFor` is set to `now + 30 days`. `ProcessAccountDeletionsJob` runs the actual anonymisation via `IAccountDeletionService.AnonymizeExpiredAccountAsync` after the scheduled date passes.
- When a human verifies a pending email that already exists as a verified address on another account, `UserEmailService` creates an `AccountMergeRequest` (status `Pending`) for admin review.
- When an `AccountMergeRequest` is accepted, `IAccountMergeService.AcceptAsync` orchestrates a **fold-into-target** inside one ambient `TransactionScope`: it fans out across every registered `IUserMerge` implementation (`merger.ReassignAsync(source, target, …)`), each owning section re-FKing its own user-keyed rows source→target; then calls `IUserService.AnonymizeForMergeAsync` to tombstone the source User row (sets `MergedToUserId` + `MergedAt`, locks out login). The source row is **not** deleted — it stays as a redirect for chain-follow reads on append-only history.
- When `DuplicateAccountService` flags a candidate, an audit entry is written via `IAuditLogService`.
- When a profile field changes through any owning service, `CachingUserService` reloads the affected `UserInfo` dict entry from the section's repositories.

## Cross-Section Dependencies

- **Consent:** `IConsentService` — consent-check status gating depends on all required document versions having active consent records.
- **Teams:** `ITeamService` — active membership equals membership in the Volunteers system team. Profile activation triggers addition.
- **Onboarding:** none (one-directional — `OnboardingService` consumes `IUserService`, never the reverse). The consent-check threshold (`Profile.ConsentCheckStatus → Pending` + Consent Coordinator notification) is director-level work and lives on `IOnboardingIntake.SetConsentCheckPendingIfEligibleAsync`; controllers call it as a peer call after `IProfileEditorService.SaveProfileAsync`.
- **Google Integration:** `IGoogleWorkspaceUserService` / `IGoogleSyncService` — a human's Google service email determines which email is used for Google Groups and Drive sync.
- **Users/Identity:** `IUserServiceRead.GetUserInfosAsync` — display data for cross-domain nav stitching. `IUserService.AnonymizeForMergeAsync` — invoked by `AccountMergeService.AcceptAsync` to tombstone the source User during fold; AspNetUserLogins + EventParticipation re-FK is handled by the Users section's own `IUserMerge` implementation (`IUserRepository.ReassignLoginsToUserAsync` / `ReassignEventParticipationToUserAsync`).
- **Account merge fold fan-out:** `IAccountMergeService.AcceptAsync` fans out across every registered `IUserMerge` implementation (`ReassignAsync`) inside the shared `TransactionScope`. Each owning section — Tickets, Teams, Shifts, Governance, Campaigns, Camps, Notifications, Feedback, Role assignments, and the Profiles sub-aggregates (Profile / UserEmail / ContactField / CommunicationPreference) — registers its own `IUserMerge` impl and re-FKs its user-scoped rows source→target, rather than the orchestrator naming each service.

## Architecture

**Owning services:** `ProfileService` (now scoped to just `IProfilePictureService` — picture storage only), `ProfileEditorService` (`IProfileEditorService` — validates and orchestrates profile-edit saves, delegating the actual persist to `IUserService`), `ContactFieldService`, `UserEmailService`, `CommunicationPreferenceService`, `EmailProblemsService` (account-merge + duplicate detection moved to the **Users** section — see [Part 1 — Users / Identity](#part-1--users--identity))
**Owned tables:** `profiles`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries`, `profile_languages`
**Status:** (A) Migrated — canonical §15 reference implementation (peterdrier/Humans PR #235, 2026-04-20). `AccountMergeService` / `DuplicateAccountService` now live in the **Users** section (`Humans.Users/Services/`) following the account-merge consolidation — see [Part 1 — Users / Identity](#part-1--users--identity). `IUserEmailRepository`, and `ICommunicationPreferenceRepository` are the only code paths that touch this section's tables via `DbContext`. Repositories are Singleton, using `IDbContextFactory<UsersDbContext>` and short-lived contexts per method.
- **Decorator decision — caching decorator.** The dedicated `CachingProfileService` / `FullProfile` cache was retired: profile field storage (save, dietary/medical, volunteer history, anonymize) moved onto `UserService`, so `FullProfile` consumers now read the single unified `UserInfo` projection served by `CachingUserService` (Singleton, `Humans.Users/Data/`) — see Part 1's caching-decorator bullet. There is no separate profile-only cache or warmup service any more.
- **`UserInfo` is canonical (issue #635 §15i, then fully absorbed the retired `FullProfile` in the account-merge / GDPR consolidation, nobodies-collective/Humans#745).** Derived properties — `PrimaryEmail`, `AllVerifiedEmails`, `GoogleEmail` — replace the old `User.UserEmails` / `User.GetEffectiveEmail()` / `User.GoogleEmail` reader sites. `CachingUserService` populates them from already-loaded `UserEmail` rows (no new repo lookups). The lifecycle marker is the stored `User.State`, surfaced as the non-nullable `UserInfo.State`; `ProfileInfo` carries no state at all.
- **Stub Profile invariant (issue #635 §15i).** Every newly created User materializes a bare Profile inline at the User-creation call site (`ExternalLoginService.CompleteExternalLoginAsync`, `AccountProvisioningService.FindOrCreateUserByEmailAsync`/`CompleteMagicLinkSignupAsync`); the User row carries `UserState.Bare`. `IProfileEditorService.SaveProfileAsync` (via `IUserService.SaveProfileAsync`) promotes `User.State` to `Active` once `BurnerName`/`FirstName`/`LastName` are all populated. Legacy profile-less users (contact imports pre-§15i) are reconciled through the `/Profile/Admin/Backfill` admin tool — idempotent count-and-bulk-create page; no-op when N=0. Until the backfill is run, `GET /Profile/{id}/Popover` (issue #690) renders a sparse fallback card for these users so `<human-link>` hovers don't 404 — see the Invariants section bullet.
- **Names live on `User`, dual-written from `Profile` (nobodies-collective/Humans#1097).** `UserRepository`'s profile `AddAsync`/`UpdateAsync` seam copies `BurnerName`/`FirstName`/`LastName` onto the owning `users` row in the same save; the merge, purge and GDPR-anonymize paths overwrite them with their tombstone labels so no purged name can resolve. `Profile` remains the read source for the legal name; only `UserInfo.BurnerName` reads the new column first. Legacy rows are reconciled through `/Profile/Admin/NameBackfill` — idempotent review → confirm page reporting the unsynced count; retiring the `Profile` columns is #1098 and waits on that page reading 0 in production.
- **`UserEmail.IsPrimary` invariants.** Service-layer guarantee in `UserEmailService.EnsurePrimaryInvariantAsync` (exactly one verified `IsPrimary` per user, recovers from zero/multi states). Account-merge fold preserves target's `IsPrimary` and demotes the source's. No DB unique index — per `memory/architecture/db-enforcement-minimal.md` the service is the contract; a DB partial unique index would push violations to runtime as untyped `DbUpdateException` failures rather than service-layer recovery (column persists under legacy name `IsNotificationTarget` per `no-column-drops-for-decoupling.md`). **`UserEmailService.ClearPrimaryAsync` (issue #650, hardened by issue #686) is the duplicate-flag recovery path** — it drops `IsPrimary` from a single row *without* invoking `EnsurePrimaryInvariantAsync`, but only when at least one other verified `IsPrimary` row exists; it returns `false` otherwise so the user can never end in a zero-verified-primary state via this path. Surface (admin and self): `/Profile/{id}/Admin/Emails/ClearPrimary` and `/Profile/Me/Emails/ClearPrimary` — both UI buttons appear only when ≥ 2 verified `IsPrimary` rows exist, and the service rejects direct form replay below that threshold.
- **Email delete guards (issue nobodies-collective/Humans#758).** `UserEmailService.DeleteEmailAsync` rejects (`ValidationException`) removal of (a) the primary email — the user must promote another verified email to primary first — and (b) any email whose address matches the user's event ticket (order buyer or matched attendee), read via `ITicketServiceRead.GetTicketOrdersAsync` (lazy-resolved through `IServiceProvider` to break the `TicketQueryService → IUserEmailService → ITicketServiceRead` DI cycle). The `/Profile/Me/Emails` grid hides the Delete action for both row kinds; the service re-validates both guards server-side for self and admin paths. Admin recovery for a deleted ticket/primary email uses the existing "Add verified email directly" card (`AdminAddVerifiedEmail`) + `AdminSetPrimary`.
- **Email re-add fix (issue nobodies-collective/Humans#758).** `UserEmailService.AddEmailAsync` only rejects an address with "This is already your sign-in email" when a live **verified** `UserEmail` row holds it. The prior check used `GetPrimaryEmailAsync`, which falls back to the legacy `AspNetUsers.Email` column — so a user who deleted their primary row could never re-add the same address (the original incident). `IsGoogle` is never set or inferred on this path.
- **Unlink and Delete operate on disjoint row sets.** `UserEmailService.DeleteEmailAsync` returns `false` for Provider-attached rows (guards non-UI callers; the grid never routes one there), and `UnlinkAsync` operates only on rows with `Provider`+`ProviderKey`: it removes the `AspNetUserLogins` entry via `UserManager.RemoveLoginAsync` **before** deleting the email row, and hard-fails (no row deletion) if login removal fails so `user_logins` and `user_emails` never diverge. `UnlinkAsync` also throws (`ValidationException`) if no *other* verified email would remain — the guard runs even when the row being unlinked is itself unverified, because an unverified row can still carry the user's only OAuth login (issue nobodies-collective/Humans#731). Admin mirror routes exist for both (`/Profile/{id}/Admin/Emails/Unlink/{emailId}`, `/Delete`).
- **Inner service** is `Humans.Users.Services.UserService`, registered as `AddKeyedScoped` under `CachingUserService.InnerServiceKey` (`"user-inner"`) — the same decorator/inner pair Part 1 describes for the User aggregate; profile writes go through it too since `UserService` absorbed `ProfileService`'s storage-mutation methods. The decorator resolves it per-call via `IServiceScopeFactory`.
- **`IUserInfoInvalidator`** is aliased to the same Singleton `CachingUserService` instance so external sections' writes (Auth, Onboarding, Teams, Google) can invalidate the cache without touching the dict.
- **Cross-domain navs stripped:** `Profile.User`, `UserEmail.User`, `CommunicationPreference.User`. Display stitching routes through `IUserServiceRead.GetUserInfosAsync`.
- **GDPR:** `AccountMergeService` implements `IUserDataContributor` (design-rules §8a) and emits the `AccountMergeRequests` slice. The `Profile`, `ContactFields`, `UserEmails`, `VolunteerHistory`, `Languages` and `CommunicationPreferences` slices are emitted by `UserService` (Users section), which absorbed profile storage in nobodies-collective/Humans#745 — `ProfileService` implements no contributor interface. Section keys are constants on `GdprExportSections`. The `ExpectedContributorTypes` in `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` enforces registration.
- **Account merge & duplicates** — `AccountMergeService` and `DuplicateAccountService` (and `IAccountMergeRepository` / the `account_merge_requests` table) moved to the **Users** section in the account-merge consolidation — see [Part 1 — Users / Identity](#part-1--users--identity).
- **Architecture tests** — `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` (the DI wiring; the orchestrator's own tests moved to `tests/Humans.Gdpr.Tests/`).

### Account deletion cascade

Account deletion cascades (user-requested / admin-initiated / expiry-triggered) are orchestrated by `IAccountDeletionService` (`src/Sections/Humans.Users/Services/AccountDeletionService.cs`). `ProfileService` now owns only profile-picture storage (`IProfilePictureService`); profile-data anonymization is `IUserService.AnonymizeProfileForDeletionAsync`. User-initiated deletion request and cancel both live on `IAccountDeletionService` (`RequestDeletionAsync`, `CancelDeletionAsync`). Both `ProfileController.RequestDeletion` (signed-in users with profiles) and `GuestAccountController.RequestDeletion` (profileless users) call the orchestrator directly — no `IProfileService` deletion methods, no manual `User.DeletionRequestedAt`/`DeletionScheduledFor`/`DeletionEligibleAfter` writes. The orchestrator computes the optional `DeletionEligibleAfter` (post-event hold for current-event ticket holders) inline via `ITicketServiceRead.GetUserTicketHoldingsAsync` so both entry points get the same treatment. Issue nobodies-collective/Humans#685 dropped the previous `Profile↔AccountDeletion` DI cycle (lazy `IServiceProvider` resolve) by removing `RequestDeletionAsync` from `IProfileService` entirely. Reference: peterdrier/Humans#314, nobodies-collective/Humans#582, nobodies-collective/Humans#685.

### Touch-and-clean guidance

- Cross-section reads for `Profile.User` / `UserEmail.User` / `CommunicationPreference.User` must go through `IUserServiceRead.GetUserInfosAsync` — do not re-add nav properties to the entities.
- The token "OAuth" is banned from `IUserEmailService` / `UserEmailRepository` method, parameter, and property names — provider operations are parameterized (`LinkAsync(provider, providerKey, …)`) so new providers add data, not methods. This is documentation, not a pinned test — a test asserting a method-name token is absent is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md). The single allowed exception is `ReconcileOAuthIdentityAsync` (issue nobodies-collective/Humans#697), where "OAuth" is categorical (the OAuth-callback write channel, distinct from user-driven email management).
