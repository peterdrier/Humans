<!-- freshness:triggers
  src/Sections/Humans.Users/**
  src/Sections/Humans.Users.Contracts/**
  src/Humans.Base/Users/**
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
- **Communication preferences and unsubscribe** moved to Email (`email_communication_preferences`, Users decomposition commit 14): `ICommunicationPreferenceService`, the unsubscribe tokens, `/Email/Unsubscribe/*`, `/Email/Guest/CommunicationPreferences*` and `/Email/CommunicationPreferences*` are Email's. The legacy `communication_preferences` table is dropped.
- **Event Participation** moved to Tickets (`tickets_event_participations`, Users decomposition commit 13). The legacy `event_participations` table is dropped.
- **Account deletion** is a 30-day grace period: `User.DeletionRequestedAt` + `DeletionScheduledFor` are stamped when a user requests deletion (with optional `DeletionEligibleAfter` for ticket-holder event holds). `ProcessAccountDeletionsJob` runs daily and calls `IAccountDeletionService.AnonymizeExpiredAccountAsync` for each due user. That job and `SuspendNonCompliantMembersJob` live in `Humans.Users/Jobs/` and are registered and scheduled by concrete type from Users' own `Section.cs` through the Shell's Hangfire scheduler. Those call sites are inside the section, so `ProcessAccountDeletionsJob` is `internal sealed`; `SuspendNonCompliantMembersJob` is still `public` and has no caller outside Users either.
- Identity sub-tables (renamed to `users`, `user_claims`, `user_logins`, `user_tokens`, `roles`, `user_roles` per Postgres convention in `UsersDbContext.OnModelCreating`) are managed by ASP.NET Identity's `UserManager<User>` / `SignInManager<User>`. Only Users injects those framework services; other sections use `IUserSignInService`.

## Data Model

### User

**Table:** `users` (ASP.NET Identity table, renamed from `AspNetUsers` in `UsersDbContext.OnModelCreating`).

Extends `IdentityUser<Guid>` with project-specific columns.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Email | string | Computed from `UserEmails` (first verified, primary-preferred); falls back to `base.Email` when collection is not loaded. Override — not a plain column. |
| DisplayName | string | `[Obsolete]` legacy Identity column (max 256, required). Nothing writes it on create or profile save (new rows hold `""`); left: merge/GDPR tombstone scrubs and the GDPR export. Column drop is nobodies-collective/Humans#1102. |
| BurnerName | string? | The name we render (max 256), and the sole source for `UserInfo.BurnerName` (nobodies-collective/Humans#1098). Dual-written from `Profile.BurnerName` on every profile save; null until supplied. There is no legacy `DisplayName` fallback. |
| FirstName | string? | Legal given name (max 256). Dual-written from `Profile.FirstName`; `Profile` is still the read source. |
| LastName | string? | Legal family name (max 256). Dual-written from `Profile.LastName`; `Profile` is still the read source. |
| PreferredLanguage | string | UI / email locale, default `"en"` (max 10) |
| ProfilePictureUrl | string? | Google profile picture URL (max 2048) |
| CreatedAt | Instant | Set on insert; immutable (`init`) |
| LastLoginAt | Instant? | Most recent login timestamp — distinguishes imported contacts (`null`) from active users |
| SuppressScheduleChangeEmails | bool | **Dead.** No reader or writer (readers removed in Users decomposition commit 16, except the Art. 15 export); `[Obsolete]`, column kept until its drop PR (peterdrier/Humans#2156). |
| UnsubscribedFromCampaigns | bool | **Dead.** Email's per-category preferences are the source of truth; no reader or writer (readers removed in commit 16, except the Art. 15 export); `[Obsolete]`, column kept until its drop PR (peterdrier/Humans#2156). |
| ContactSource | ContactSource? | Where imported from (`Manual`, `MailerLite`, `TicketTailor`); null for self-registered users. |
| ExternalSourceId | string? (256) | ID in the external source system (e.g., MailerLite subscriber ID). |
| DeletionRequestedAt | Instant? | When the user requested account deletion |
| DeletionScheduledFor | Instant? | `DeletionRequestedAt + 30 days`; the earliest the job will anonymize |
| DeletionEligibleAfter | Instant? | Optional event-hold floor for ticket holders; the job waits past this date too |
| MergedToUserId | Guid? | Self-referential FK → User. Set on this row when `AccountMergeService.AcceptAsync` folds it into the target. Source rows are tombstones — they outlive their target's lifecycle (`OnDelete: Restrict`) so append-only history (audit log, consent records, budget audit log) stays attributable. Filtered index `IX_users_MergedToUserId` (WHERE NOT NULL) backs the chain-follow lookup. |
| MergedAt | Instant? | When the merge tombstone was applied. Null while live. |
| State | UserState | Lifecycle state and the single source of truth for access, suspension included (`Bare`, `Active`, `DeletePending`, `Suspended`, `Rejected`, `Deleted`, `Merged`, `AdminSuspended`). Required string column (`HasConversion<string>()`, physical default `Bare`); written at each transition and never derived on read. The full app is reachable only when `State == Active`. |
| MarkedFakeAt | Instant? | Set once when the account is not a real person (the gate terminal, dev personas); null for every member. Read as `UserInfo.IsFake`. Orthogonal to `State`, so a fake account still moves through the same states. Written only by `IUserWrite.MarkFakeAsync`, idempotent, audited once (`UsersAuditActions.AccountMarkedFake`). Queues and counts of people skip fake accounts by reading `IsFake`. |

**Shadow columns (no C# property):** `users.GoogleEmail` is kept on disk as an EF shadow property (`UserConfiguration.cs`); no production code reads or writes it any more (nobodies-collective/Humans#1102 retired the last reader) and the column drop is deferred to a later PR per `memory/architecture/no-drops-until-prod-verified.md`. The Workspace identity read path is GoogleIntegration's `IGoogleIntegrationService.GetWorkspaceIdentityAsync`. `user_emails.IsGoogle` and `user_emails.GoogleEmailStatus` are also retained as shadow columns, without runtime readers, until their separate drop PR; display sorting is alphabetical on `Email`. Analyzer `HUM0001` (`UserEmailLegacyFieldAnalyzer`) rejects any reference to the legacy properties (`User.GoogleEmail`, `UserEmail.IsOAuth`, `UserEmail.DisplayOrder`).

**Profile-adjacent fields** — `ContactSource`, `ExternalSourceId` — are also described under [Part 2 — Profiles](#user-identity-extension) because `CachingUserService` stitches them into `UserInfo`. The canonical field table is here; Part 2 describes `UserInfo` projection semantics.

Computed: `IsDeletionPending => DeletionRequestedAt.HasValue`.

User-suspension state lives on `User.State` (`UserState.Suspended` / `AdminSuspended`); `UserInfo.IsSuspended` is the canonical predicate. Single-user writes go through the internal `IUserManagement.SetSuspensionAsync` (called by `HumanLifecycleService`), which delegates to `IUserRepository.SetSuspensionAsync` — that call writes `users.State` and nothing else. The suspension **reason** is not stored on the row: `HumanLifecycleService.SuspendAsync` puts it in the audit log (`UsersAuditActions.MemberSuspended`) and in the `AccessSuspended` notification body. `Profile.AdminNotes` is a general-purpose admin field and is never written by the suspension path. The nightly consent-expiry sweep prepares user details and one team-membership snapshot before changing state, so lookup failures leave suspension retryable; downstream cleanup and audits process only the IDs the write actually changes. The bulk path is `IUserManagement.SuspendProfilesForMissingConsentAsync` → `IUserRepository.SuspendManyAsync`, which returns only the users whose state actually moved — a row already `Rejected`/`Merged`/`Deleted` outranks `Suspended` and is left alone. Unsuspending re-classifies from the remaining fields, so a rejected or deletion-pending row does not come back as `Active`. Rejection is written by `IUserWrite.SetRejectedAsync` (Onboarding's reject, after Consent records the rejection in `consent_reviews`; Users decomposition commit 12) and carried forward from the stored `UserState.Rejected` by every later classification. `UserInfo.IsRejected` reads only that stored state. The User entity has no `IsArchived` / `SuspendedAt` / `SuspensionReason` columns; "archive" / "lockout" semantics are achieved by anonymizing identity fields and removing OAuth logins through `IAccountDeletionService.PurgeAsync` / `AnonymizeExpiredAccountAsync`.

`UserStateEvaluator.Classify`'s `isGdprDeleted` (→ `UserState.Deleted`) is keyed on the minted tombstone email (`deleted-<id>@deleted.local`, written by `ApplyExpiredDeletionAnonymizationAsync`), never on a user-editable name field — `DisplayName`/`BurnerName` are things a member can type, and a burner name of "Deleted User" must not read as erasure (nobodies-collective/Humans#1742). The legacy name-shape recognition, BurnerName sentinel fallback and `users.legacy-gdpr-tombstones` registration are retired: production `GET https://humans.nobodies.team/api/data-migrations` reported `{"key":"users.legacy-gdpr-tombstones","pending":0,"done":true}` on 2026-10-22. Names alone never identify erasure, and `UserInfo.BurnerName` reads only the User column (a blank one on a minted-email tombstone resolves to the erasure sentinel). The legacy `DisplayName` column remains scrubbed/exported until its separate approved drop.

`User` stays public for ASP.NET Identity consumers. `UserEmail`, `User.UserEmails`, the entity-taking `UserInfo.Create` factory, and the email reconcile command/result are internal to Base. Users has friend access as their production owner; test assemblies retain access for entity-backed fixtures. Cross-section domain reads use `IUserService` and `UserInfo`.

`User`'s only navigation is its internal `User.UserEmails` (required by the `User.Email` override). Every other relationship is FK-only, configured inverse-side on the owning entity (e.g., `ProfileConfiguration.HasOne<User>().WithOne().HasForeignKey<Profile>(p => p.UserId)`); readers route through `IUserService` / the owning section's service. This is documented, not test-pinned — a test asserting an entity lacks a nav property is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md).

`User.Email` is overridden on the entity to compute from `UserEmails` (first verified, primary-preferred) — application code reads `user.Email` and gets the canonical address without touching the underlying Identity column (or `UserInfo.PrimaryEmail` for canonical reads on a singly-loaded User without `UserEmails` hydrated). `User.NormalizedEmail` is `[Obsolete]` (diagnostic id `HUM_USER_NORMALIZEDEMAIL`); applications must use `user.Email` or `IUserEmailRepository` for canonical lookups. `User.EmailConfirmed` is also overridden (true when any `UserEmail` is verified). Identity's email-lookup APIs (`UserManager.FindByEmailAsync` / `FindByNameAsync`) are forbidden by analyzer `HUM0003` — application code routes through `IUserEmailService.FindByAddressAsync` instead. Identity itself does not call these internally, so no custom `UserStore` is needed.

Every newly created User has a corresponding Profile row materialized inline at the User-creation call site (see [Stub Profile invariant](#part-2--profiles)); until the names are filled the user is `UserState.Bare`. The completed `users.stub-profiles` registration is retired: production `GET https://humans.nobodies.team/api/data-migrations` reported `{"key":"users.stub-profiles","pending":0,"done":true}` on 2026-10-22.

Users no longer loads, caches, exports or folds these rows. Tickets reads and merges only its own participation records.


### AccountMergeRequest

Tracks pending and resolved merges between duplicate accounts. `AccountMergeService` orchestrates the merge; `DuplicateAccountService` is the stateless detector that flags candidates. Admin merge, accept, dismiss and reconcile operations return internal refusal results for their request/account guards. Expected refusals retain operator feedback and log Warning with identifiers; unexpected dependency failures propagate. The fan-out, final tombstone, retry behavior and dismissal transaction retain their existing ordering.

The pending-merge guard for email additions compares literal addresses case-insensitively, including the supplied Gmail/Googlemail alias. Only pending requests targeting that human block an addition; underscores and percent signs are ordinary email characters.

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

These are managed by `UserManager<User>` / `SignInManager<User>` / `RoleManager<IdentityRole<Guid>>` from `Microsoft.AspNetCore.Identity`, registered by Users (`AddHumansIdentity`, which also wires `HumansUserClaimsPrincipalFactory`). Do not write a custom repository over them.

## Routing

The section's controllers (`Controllers/`): `ProfileController` (`/Profile/Me/*`, the member's own profile), `ProfileEmailsController` (`/Profile/Me/Emails/*` and `/Profile/{id}/Admin/Emails/*`), `ProfileViewController` (`/Profile/{id}`, `/Profile/Picture`, popovers, `/Profile/Search`), `ProfileApiController` (`/api/profiles/*`), `UsersAdminController` (`/Users/Admin/*`), `UsersAdminAccountMergesController`, `ProfileAdminController` (`/Profile/Admin/EmailProblems/*`). The section's one-off copies are data migrations (`ISectionDataMigrations`, below), run from Debug's `/Debug/DataMigrations`. The Profile, API and admin routes are tabled under [Part 2 — Routing](#routing-1); Part 1 covers identity.

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
- Member profile, email, search and messaging pages localize their breadcrumb navigation landmark in all supported cultures. The profile’s application contribution prompt uses the shared localized Elsewhere wording.
- The `/Consent/MyData` page (Consent's) is not served by this section; its `Users_ProfilePrivacy_Title` key stays here only for the profile-hub link.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone with a valid email | Sign up via OAuth (`/Account/ExternalLogin`) or magic link (`/Account/MagicLinkRequest`). Use `/Email/Unsubscribe/{token}` (no login required). |
| Import jobs (Tickets, MailerLite) | Call `IAccountProvisioningService.FindOrCreateUserByEmailAsync` to materialize contact-only User rows (no `LastLoginAt`); they get back the user id and whether it was created, never the `User` entity. |
| Authenticated human | Read own User row. Request account deletion. (Self-declaring `NotAttending` is Tickets' `TicketParticipationController`.) |
| UsersAdmin, Board, Admin | Read any User's deletion / login state. Suspension itself is a Profile concern (see [Part 2 — Profiles](#part-2--profiles). |
| Admin | Trigger account merge flows on the unified **Account Merges** page (`/Users/Admin/AccountMerges`, `UsersAdminAccountMergesController`); `AccountMergeService` + `DuplicateAccountService` are Users-section services. Outside Production only: purge a human via `UsersAdminController.PurgeHuman` (`POST /Users/Admin/{id}/Purge`). |

## Invariants

- Profile, medical, email and administrator lifecycle actions honour request
  cancellation during account preflight, including purge's raw target-name lookup.
  These reads finish before mutation handling; started writes retain their existing
  tokens. Medical account-lookup cancellation propagates instead of showing a save
  failure.

- A fake account (`UserInfo.IsFake`) is marked by the section that creates it, through `IUserWrite.MarkFakeAsync`: Tickets' gate-terminal seeder on every password set, Development's persona seeder on every role-persona sign-in. Guests stay unmarked so onboarding can be tested end to end. The marker grants and blocks nothing; the gate terminal's login, route restriction and policies still key on `SystemUserIds.GateTerminal`.

- Base’s shared Human component reads `UserInfo.ProfilePictureUrl`, matching profile summaries: uploaded pictures use the canonical versioned route and humans without uploads retain their fallback picture.

- Section-owned UI resource keys use `Users_` in all supported cultures, including dynamically selected phone labels; reflection-resolved `Enum_` keys retain their type-based names.

- GDPR-erased accounts cannot use current or legacy unsubscribe tokens, or a lingering Guest session, to read or recreate communication preferences. Live merge survivors remain eligible.

- Guest preference tokens minted before an account merge read and update the surviving account, matching the one-click unsubscribe flow.

- Communication preference writes share one channel-update path. Signed-in profile and Guest changes audit their authenticated actor; token/one-click and imported changes retain service attribution. Email-only writes preserve the inbox channel; no-ops preserve timestamps, source and audit history.

- Self-service email visibility accepts only defined visibility values. Unsupported text and out-of-range numeric input use the existing hidden fallback rather than persisting an undefined audience; valid names, case-insensitive names and defined numeric values retain their meaning.

- Email resource authorization resolves the authenticated actor to the surviving account before checking self-access. The target remains the exact owner id supplied by the action: a merged-away cookie can manage the survivor's email rows, but cannot claim self-access to the old account. Missing actors receive no self-access. UsersAdmin/Admin grants and Board's view-only grant remain independent; the identity read honours request cancellation.

- The profile's onsite chip is Tickets' `OnsiteChipViewComponent` on the `UserPartSlots.Profile` slot (Users decomposition commit 13). Member email-grid and unsubscribe GETs retain browser cancellation for their token-aware service reads.

- Profile-card volunteer-history descriptions retain their 80-character excerpt limit without splitting UTF-16 surrogate pairs; the complete history description remains stored and exported.

- Member, guest and read-only admin communication-preference checkboxes expose localized channel/category names to assistive technology, including locked and unchecked controls.

- Guest preference saves disable both editable channels and the matching one-click unsubscribe action until completion. Writes to one category cannot overlap in the page; other categories remain available, and failures restore the edited value before unlocking.

- Member email-preference changes preserve the row’s existing inbox preference when the alert control is not shown; changing email cannot silently re-enable inbox notifications.

- Guest communication-preference GETs carry request cancellation through session/token user lookup, preference reads and ticket holdings. Cancellation propagates without an error flash; paired POST helpers retain their default mutation token.
- Member and guest communication-preference POSTs require both channel flags explicitly. Missing flags and binding errors return HTTP 400 and a Warning after session/token resolution, before writing either channel. Unresolved callers remain HTTP 401, and explicit false channel flags remain valid.

- Member and guest communication-preference category names, descriptions, ticket-year heading and ticket-lock note use Email resources in all supported cultures. Guest one-click banners and legacy marketing unsubscribe pages use the same translated category names; stored category identifiers and preference rules are unchanged.

- Account-status, pending-deletion and communication-preferences GETs retain request cancellation during viewer resolution; cancellation propagates without an error flash. Deletion and preference POSTs keep their existing mutation boundaries.

- The login page’s locked-account error and dismissal label render in all supported cultures. Authentication and lockout behavior are unchanged.

- OAuth login (`ExternalLoginService.CompleteExternalLoginAsync`, dispatched from `AccountController.ExternalLoginCallback`) matches existing accounts through verified `UserEmails` before creating a new account. Selecting an account by email also requires the provider's `email_verified` claim; false, missing or malformed claims cannot link to or sign in that account. The locked-out branch may re-link a stale OAuth login from a merged source to the email-matched target only with the same provider-verification proof, before removing any login. Known provider-key sign-ins and intentional links from an authenticated account retain their existing paths.
- Only Users injects `UserManager<User>` / `SignInManager<User>`. Callers elsewhere sign in, sign out and manage passwords by user id through `IUserSignInService`, and create accounts through `IAccountProvisioningService.CreateAccountAsync`. Analyzer HUM0039 keeps the `User` / `UserEmail` entities out of every project but Base, Users and Users.Contracts.
- **The Google Workspace identity is GoogleIntegration's** (`google_workspace_identities`). The `google.workspace-identities` copy and legacy fallback are retired after production reported `pending: 0, done: true` on 2026-11-05. `UserEmailInfo` carries no Google designation or verdict, and Users' GDPR email slice omits the retired keys; GoogleIntegration exports and erases the current identity. The legacy database columns remain shadow-mapped until a separate drop PR. Every path that removes or renames an address away from an account — `DeleteEmailAsync`, `DeleteByIdAsync`, `UnlinkAsync`, an OAuth tag-move or rename, and a cross-user displacement — calls `IGoogleIntegrationWrite.ClearIfAddressAsync` after it commits.
- `UserService` implements `IUserDataContributor` (design-rules §8) and contributes the User-account slice under `UserService.Account` plus the Profiles-half slices `Profile`, `ContactFields`, `UserEmails`, `VolunteerHistory`, `Languages`. Email owns communication-preference export/erasure; Tickets owns participation export and retention. See [Part 2 — Profiles](#part-2--profiles).
- **The cross-section reads resolve merge chains forward, and Users is the only thing that walks one.** `GetUserInfoAsync`, `GetUserInfosAsync` and `GetAllUserInfosAsync` answer a merge-tombstone id with the surviving row (transitively, so A→B→C resolves to C), and drop tombstones from the load-all read entirely, one entry per living human. Every row they return is stamped with `MergedUserIds`, and `UserInfo.AllUserIds` is that row's own id followed by them: the list a chain-following read queries over, seeded from the record returned and never from the id the caller asked with. `SearchUsersAsync` omits tombstones, including exact-id queries, and answers a merged-away id with its live survivor. A chain ending at an erased or unresolved merged row returns no search result. A cycle or a chain pointing at a row that is no longer there is logged at Warning and resolves to the last row reached, so a bad graph degrades rather than spins.
- **A resolving read hands the caller a different id, and the caller acts on that one.** Whatever a caller does after `GetUserInfoAsync` / `GetUserInfosAsync`, write, look up by id, notify, audit, redirect, it does with `UserInfo.Id` (or `AllUserIds`), not with the id it asked for. A path that must not act for anyone but the id it was handed (the Gate claim, an Asociado ballot stored under an id) compares `info.Id` to the requested id and fails closed. Raw per-id row reads that stamp the owner id (`GetEntitiesByUserIdAsync`, the email-problems page) stay on the raw reads so rows and the id labelling them come from one row.
- **The raw reads are the escape hatch, and there are exactly two.** `IUserService.GetRawUserInfoAsync` and `GetAllRawUserInfosAsync` return the row as stored, tombstones included, still stamped with `MergedUserIds`. They exist for this section's own merge machinery (`AccountMergeService`, `AccountDeletionService`, the admin screens) and for `RoleAssignmentClaimsTransformation`, which must stamp a tombstone principal `Merged` rather than inherit the survivor's Active state. No other section calls them.
- **The merge index is derived state, rebuilt on demand.** `CachingUserService` builds a `Dictionary<Guid, Guid[]>` over its warmed snapshot the first time a read needs it, and drops it on every mutation of the dict (`TrackedCache.OnMutated`). It is never persisted and never invalidated by hand; a merge, an erasure or a cache refresh all mark it dirty through the same hook. Build and drop share one lock, so a mutation that lands mid-rebuild is not overwritten by the stale index.
- A `User` row whose `MergedToUserId` is non-null is a **merge tombstone**: its identity fields are anonymized, its OAuth logins have been re-FK'd to the target, `LockoutEnd` is far-future, but the row itself persists indefinitely so append-only history written under the source id stays resolvable. The self-referential FK is `OnDelete(Restrict)` — deleting a target cannot cascade-delete its source tombstones.
- `UserInfo.IsActive` excludes tombstones (`IsTombstone`: merged, `Deleted`/`Merged` state, GDPR-anonymized, or a `.local` sentinel in the resolved email or the raw Identity column; it covers every row `UserStateEvaluator.IsTombstone` refuses writes for) — a tombstone keeps its anonymized Profile row, so "has a profile" alone doesn't exclude it (peterdrier/Humans#1707).
- **A merge is atomic by ordering, not by transaction.** `AccountMergeService.MergeAsync` (reached from `AcceptAsync`) uses no `TransactionScope` — crossing section DB boundaries in one transaction is disallowed. Instead the steps run in a fixed order and each commits independently: move every section's rows via the `IUserMerge` fan-out, settle the pending email, **tombstone the archived account last**. The tombstone is the single observable commit point and the source of truth; a failure before it leaves the archived account untouched and the request still actionable. Every `IUserMerge.ReassignAsync` must therefore stay idempotent — "move all of archived's X to survivor" is a no-op on re-run — so a partial merge is safely retryable. The pending-email settle is deliberately **non-fatal**: a missing or already-consumed pending email is the desired end state, not an error. The only `TransactionScope` in the service is in `RejectAsync`, wrapping the pending-email delete and the request close so a dangling `PendingEmailId` can never block a later accept.
- **Merge-request state is derived from the tombstone.** A `Pending` `AccountMergeRequest` whose two accounts are already merged into each other (`source.MergedToUserId == request.TargetUserId` or the inverse) is moot and is closed on sight — this is the cleanup mechanism for half-done rows, not a data migration. A side merged into some unrelated third account is a different conflict and is refused rather than silently closed.
- **No merge status beyond `Pending` / `Accepted` / `Rejected`.** Dismiss reuses `Rejected`; a request auto-closed by a completed merge becomes `Accepted` with a note (`CloseRequestsForPairAsync`). Request rows are never deleted, so GDPR export and audit history stay intact.

- User-row interceptor snapshots carry the stored `State`. Warm-cache user-field refreshes reuse the canonical `UserInfo` factory, including that state, while preserving profile, email, participation, login, preference, and merge-ID slices.
- Single and batched `UserInfo` cache misses share guarded loading: a delayed source read cannot replace a refreshed or evicted entry with its older snapshot.
- A failed or cancelled `UserInfo` cache reload evicts the affected user and marks the cache cold before propagating the failure; the next read reloads from source rather than retaining stale fields or omitting a newly created human.
- Contact-field and profile-language saves resolve the owner before committing, so a failed lookup cannot strand committed changes outside cache refresh.
- Profile fields, a supplied CV and the legacy identity name share one database save and the per-user save lock; failures or overlapping submissions cannot separate the experience declaration from its history. A null CV payload leaves history unchanged.

The operator-only user directory renders its sort-control label directly, without a missing resource key.

## Negative Access Rules

- Code outside Users **cannot** inject `UserManager<User>` or `SignInManager<User>` (HUM0039). It goes through `IUserSignInService` / `IUserService`.
- Services in `Humans.Users/Services/` **cannot** inject a DbContext directly — they go through `IUserRepository` (whose `UserEmails` partial carries the `user_emails` surface).
- Other Application-layer services **cannot** read or write the `users` / Identity tables directly — they go through `IUserService` (or, inside this section, `IUserManagement`).
- Regular humans **cannot** purge any account.
- An Admin **cannot** purge their own account — `IAccountDeletionService.PurgeAsync` refuses when the actor is the target, and `UsersAdminController.PurgeHuman` returns the user to the admin-detail page with an error.
- Purge **cannot** run in Production — `IAccountDeletionService.PurgeAsync` refuses when `IHostEnvironment.IsProduction()`, and `UsersAdminController.PurgeHuman` returns `NotFound`. Account anonymization (the GDPR-deletion path through `AnonymizeExpiredAccountAsync`) runs in every environment via `ProcessAccountDeletionsJob`.
- A merge-tombstoned `User` (`MergedToUserId` non-null) **cannot** sign in — `LockoutEnd` is bumped far-future during fold. Application code outside `IAccountMergeService` **cannot** clear `MergedToUserId` / `MergedAt` or revive a tombstone.

## Triggers

- Merge rejection commits pending-email deletion and the rejected request together, then attempts its independently persisted, best-effort audit and refreshes the target user cache. Failed metadata writes emit no rejection audit.

- **On first OAuth login (no matching account):** `ExternalLoginService.CompleteExternalLoginAsync` (dispatched from `AccountController.ExternalLoginCallback`) creates the `User` via `UserManager.CreateAsync`, attaches the external login, and persists a provider-tagged `UserEmail` row via `IUserEmailService.ReconcileOAuthIdentityAsync` (HUM0005 pins `ExternalLoginService` as its sole caller); `AccountController` then signs the user in by id via `IUserSignInService.SignInAsync`. Profile creation happens lazily in the Profile section (see [Part 2 — Profiles](#part-2--profiles).
- **On import (Tickets / MailerLite contact upsert):** `AccountProvisioningService.FindOrCreateUserByEmailAsync` looks up an existing user by `UserEmail` and `User.Email` (with gmail/googlemail equivalence), creates a contact-only `User` + `UserEmail` if no match, layers `User.ContactSource` onto an existing self-registered user when null, and writes an `UsersAuditActions.ContactCreated` audit entry on creation.
- Unverified-address replacement is one Users-owned `ReplaceUnverifiedEmailAndProvisionAsync` operation. It rechecks the planned row, rejects verified/provider-linked/stale rows, and retains ordinary deletion guards. A serializable transaction contains deletion and account/email/stub-profile creation; any provisioning or commit failure restores the original state without success audits. Contact creation or profile-repair audit and the replacement-deletion audit follow the successful outer commit, using AuditLog’s independent best-effort persistence. Ordinary imports retain their own commit-before-audit boundary. The deletion audit identifies the old user and new account. Affected user caches refresh after commit or rollback.
- **On unsubscribe click / RFC 8058 one-click:** Email's `UnsubscribeService` opts the user out of the message category (Email since Users decomposition commit 14). The dead `User.UnsubscribedFromCampaigns` column is never touched — Email's per-category preferences are the source of truth for opt-out.
- **On admin participation backfill (Tickets since decomposition commit 13):** `/Tickets/Participation/Backfill` previews the parsed valid CSV rows before an explicit confirm POST; preview performs no writes. Confirmation requires an admin actor ID. Tickets' `EventParticipationService.BackfillParticipationsAsync` writes records with `Source = AdminBackfill` and audits each changed member’s final status/year after the repository save, before cache refresh. Counts describe unique changed records: protected Attended rows and identical re-imports produce neither changes nor success audits; duplicate input rows retain the existing sequential status rules. CSV parsing skips undefined numeric statuses and unparseable rows; no valid entries means no write. The default-year GET observes request cancellation.
- Email-verification, account-deletion request and erasure-confirmation emails use the recipient's supported saved language, falling back to English for blank, invalid or unsupported preferences. Erasure confirmations retain their direct, non-persisted delivery.
- **On account-deletion request (user-initiated):** `IAccountDeletionService.RequestDeletionAsync` orchestrates the user-initiated path. Internally calls `IUserManagement.SetDeletionPendingAsync` to stamp `DeletionRequestedAt` + `DeletionScheduledFor` on the User row, revokes team memberships and governance roles immediately so the user loses access during the 30-day grace period, and sends a confirmation email. After commit, recipient lookup and confirmation enqueueing finish independently of caller cancellation; failures are logged without changing the successful deletion-request result. Its cancellation link opens Gdpr's `/Gdpr/Deletion` status page, which only shows the signed-in user’s pending request.
- **On scheduled deletion expiry:** `ProcessAccountDeletionsJob` calls `IAccountDeletionService.AnonymizeExpiredAccountAsync` per due user. It captures the identity slice the caller needs for the confirmation email, resolves the merge chain, and calls `IGdprService.EraseForUserAsync` per id — the erasure fan-out lives in Gdpr beside the export one, so the loop over `IUserDataContributor`s and the `UserService.Account`-last ordering are Gdpr's concern. A contributor that throws aborts that account’s cascade with its deletion markers still set. The job attempts the other due accounts, then fails if any account failed so incomplete cascades can retry. Completed anonymizations stay excluded by their cleared deletion schedules; confirmation-email failures remain terminal and do not fail the run. Each archived id's `UserInfo` cache entry is dropped as its erasure completes (interleaved, so a mid-chain failure still invalidates the ids already erased); then the cross-cutting caches (Teams active-teams + member, role-assignment claims, shift authorization, shift view) — the survivor's `UserInfo` entry refreshes automatically as part of the `IUserService` write path. The job writes the `UsersAuditActions.AccountAnonymized` audit entry and sends the confirmation email. Missing or whitespace-only original addresses skip confirmation with a Warning naming only the user ID; anonymization remains successful and audited.
- **On admin purge (non-Production only):** `UsersAdminController.PurgeHuman` (`POST /Users/Admin/{id}/Purge`) calls `IAccountDeletionService.PurgeAsync`, which runs the same Article 17 fan-out as the scheduled expiry path — `IGdprService.EraseForUserAsync` per merge-chain id, survivor last — so an admin purge never erases less than the scheduled job does; the Account contributor inside that fan-out owns the identity collapse (tombstone name/address, external logins, permanent lockout). The orchestrator then drops the Teams active-teams cache, refreshes the cached `UserInfo` entry, and invalidates the per-user role-assignment cache (Shifts' own erasure contributor evicts its view cache).
- **On account merge accept:** `IAccountMergeService.AcceptAsync` (Users section) resolves the request and delegates to the one merge engine, `MergeAsync(survivor, archived, …)` — the admin picks the survivor, so the request's stored direction can be flipped. `MergeAsync` fans out its `IUserMerge` implementations to re-FK source → target (Users' own `IUserMerge.ReassignAsync` calls `IUserRepository.ReassignLoginsToUserAsync` for `AspNetUserLogins`; event participation is Tickets' `IUserMerge`), then `AnonymizeForMergeAsync` to tombstone the source row by setting `MergedToUserId` + `MergedAt` and bumping `LockoutEnd` far-future so the source can no longer sign in. The source `User` row stays in place as a redirect — it is NOT deleted — so append-only history written under the source id (audit log, consent records, budget audit log) remains attributable.
- **On per-user reads of a target after merge:** every `UserInfo` the cross-section reads hand out carries `MergedUserIds`, every id whose `MergedToUserId` chain reaches this row, transitively, sorted. Append-only sections (`IAuditLogService`, `IConsentManagement`, `IBudgetManagement.ContributeForUserAsync`, `IExpenseService.ContributeForUserAsync`) query over `AllUserIds` of the record they got back, so source-tombstoned rows surface for the target and a read through a source id sees the target's own rows. Nobody outside Users walks `MergedToUserId`.

## Cross-Section Dependencies

Outbound (Users → other sections). `Humans.Users.csproj` references, all acyclic — none of the targets names a `Humans.Users` type (they reference `Humans.Users.Contracts` at most):

- **Audit actions:** declared on `UsersAuditActions` (`src/Sections/Humans.Users/UsersAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `MemberSuspended`, `MemberUnsuspended`, `AccountAnonymized`, `MembershipsRevokedOnDeletionRequest`, `AccountMergeAccepted`, `AccountMergeRejected`, `ContactCreated`, `GoogleEmailRenamed`, `AccountPurged`, `UserEmailLinked`, `UserEmailUnlinked`, `UserEmailPrimarySet`, `UserEmailPrimaryCleared`, `UserEmailDeleted`, `UserEmailVisibilityChanged`, `UserEmailAdded`, `UserEmailManuallyVerified`, `OrphanUserEmailDeleted`, `LegacyIdentityEmailBackfilled`, `OAuthRenameCollision`, `OAuthRenameCollisionBlocked`, `UserEmailDisplacedByOAuthRename`, `DuplicateAccountFlagged`, `StubProfileBackfilled`.
- **Notifications:** `INotificationEmitter` (and `INotificationAutoResolve` where a source resolves). Its sources are declared in `UsersNotificationSources` (`src/Sections/Humans.Users/UsersNotificationSources.cs`), one `NotificationSourceDefinition` each: `AccessSuspended` (actionable, to the user: non-compliance suspension).
- **Contracts surfaces** (a `.Contracts` leaf, or a section project for a `Contracts/` folder): `AuditLog` (every write path records an audit entry), `Auth` (role assignment on provisioning and merge), `Camps` (camp name + roles on the admin popover), `Consent` (profile completeness gates on the consent state), `Email` (verification, unsubscribe and merge-notification mail — Users owns its four templates (`email_verification`, `deletion_requested`, `account_deleted`, `access_suspended`) via `UsersEmails` + `UsersEmailPreviews`, keys `Users_Email_*`; Email is the transport only), `Gdpr` (`UserService` and `AccountMergeService` are Article 15 contributors), `Governance` (`IMembershipCalculator` for non-compliant suspension; `IGovernanceService` for membership-tier reads), `GoogleIntegration` (`ITeamResourceService` verifies a coordinator's team Group before team-reply messaging; `IGoogleIntegrationWrite.ClearIfAddressAsync` when an address leaves an account and `IGoogleIntegrationService` for the admin list's `googlerejected` filter; its `<vc:nobodies-email-badge>` and `<vc:workspace-identity>` render on Users' views), `Notifications` (merge requests and email problems notify the human), `Onboarding` (profile editing reports onboarding progress), `Shifts` (participation backfill reads the burn settings; `IShiftSignups` for no-show history), `Cantina` (`ICantinaService` — the medical prompt shows only while dietary is still open), `Teams` (account merge folds team memberships), `Tickets` (participation is derived from ticket sync).
- **Full section projects** — referenced whole because Razor binds a view component's tag helper only from its declaring assembly, so `@addTagHelper` needs the assembly, not its leaf: `Humans.Camps` (`<vc:my-camps />` on `Views/Profile/Index.cshtml`), `Humans.AuditLog` (`IAuditViewerService` in `ProfileController`; `<vc:audit-log>` on `AdminDetail.cshtml`), `Humans.Shifts` (`<vc:shift-signups>` on `Views/Profile/Index.cshtml` and `Views/UsersAdmin/AdminDetail.cshtml`, plus `IStringLocalizer<ShiftsResource>` in `Views/Profile/Edit.cshtml` for the shift-preference link's strings). Each drops out only when the component moves to the leaf.

The deletion cascade is `AccountDeletionService` (`Humans.Users/Services/`), the one Users service that explicitly bridges higher-level sections:
- **Gdpr:** `IGdprService.EraseForUserAsync` — the erasure fan-out lives in Gdpr; `AccountDeletionService` resolves the merge chain, calls it per id, and invalidates each archived id's cache as its erasure completes. It names no section to erase; Gdpr fans out over its own `IUserDataContributor` roster.
- Auth role projections for contact visibility, duplicate detection and profile cards use `IRoleAssignmentService`. Deletion-time revocation goes through `IRoleAssignmentWrite` (Auth evicts its own caches, merge fold included); the admin assign/end pages are Auth's own.
- **Auth:** `IRoleAssignmentWrite.RevokeAllActiveAsync` (grace-period revoke on the deletion *request*).
- **Teams:** `ITeamWrite.RevokeAllMembershipsAsync` (same grace-period revoke); Teams evicts its own team graph on revoke and erasure. Teams resolves member display data live through `IUserService`, including archived ids after the merge tombstone; Users sends no Teams cache flush.
- **Shifts:** nothing direct. Its GDPR contributor erases the Shifts data and evicts Shifts' own view cache.

Communication preferences are Email's. Users holds no preference data.

Inbound (other sections → Users) — the typical direction:

- **Tickets:** owns live participation and its merge, export and retention paths. Users exposes no legacy participation data.
- **Notifications, Email, AuditLog:** call `IUserService.GetUserInfosAsync` (batched `UserInfo` lookup — the caching decorator's dict already carries the full payload, including `UserEmails`) to resolve recipient identity/email without navigating cross-domain navs.
- **Account-deletion job (`ProcessAccountDeletionsJob`, `Humans.Users/Jobs/`):** calls `IUserManagement.GetAccountsDueForAnonymizationAsync` + `IAccountDeletionService.AnonymizeExpiredAccountAsync`.
- **Account merge (`IAccountMergeService.AcceptAsync`, Users section):** the `IUserMerge` fan-out calls Users' own `IUserMerge.ReassignAsync` (which re-FKs `AspNetUserLogins` via `IUserRepository`) and `IUserManagement.AnonymizeForMergeAsync` to fold a source User into a target.
- **Audit Log / Consent / Budget / Expenses:** read `UserInfo.AllUserIds` off the row `GetUserInfoAsync` / `GetUserInfosAsync` already returned, to chain-follow merge tombstones on per-user reads of append-only entities.

## Architecture

Identity store registration stays in the section entry point. Shell calls the contract bridge, which names no EF types; `User : IdentityUser<Guid>` remains the intentional public Identity model.

A lazy per-key cache miss cannot republish its old result after an intervening cache eviction or refresh.

The internal `UserDataExport` owns account/profile export collection, merge-chain traversal, retained merged-profile collection and JSON shaping. `UserService` retains the existing contributor contract, slice-name constants, erasure declaration and deletion paths. Export keys, legacy preference flags and profile-field inclusion remain unchanged.

**Owning services:** `UserService`, `AccountProvisioningService`, `AccountDeletionService`, `AccountMergeService` + `DuplicateAccountService` (the one ordered merge engine and the stateless duplicate detector; `AccountMergeService` is backed by `IAccountMergeRepository` for `account_merge_requests` and contributes the `AccountMergeRequests` GDPR slice), `ExternalLoginService` (the OAuth-callback decision ladder, kept out of `AccountController` per HUM0031; sole caller of `IUserEmailService.ReconcileOAuthIdentityAsync`) — all in `Humans.Users/Services/`.
**Owned tables:** `users`, `user_claims`, `user_logins`, `user_tokens`, `roles` (legacy), `user_roles` (legacy), `role_claims` (legacy), `account_merge_requests`.

- `UserService`, `AccountProvisioningService` live in `Humans.Users/Services/` and never import `Microsoft.EntityFrameworkCore`. `AccountProvisioningService` does inject `UserManager<User>` per the §2a exception (Identity owns the password hash / security stamp surface).
- `IUserRepository` (impl `Humans.Users/Data/Repositories/UserRepository.cs`) owns the SQL surface for `users`. Its `IUserRepository.UserEmails.cs` partial is the surface for `UserEmail` (the Profiles half's table, read/written from the Users half for lookup + OAuth-email lock-step).
- **Caching decorator.** `CachingUserService` (Singleton, `Humans.Users/Data/`) owns a `ConcurrentDictionary<Guid, UserInfo>` of the unified read-model spanning the contributing tables (`users`, `user_emails`, AspNet `user_logins`, `profiles`, `contact_fields`, `profile_languages`, `volunteer_history_entries`). Dict hits are served synchronously; a cache miss refills via the inner Scoped `UserService` (keyed `"user-inner"`); writes through `IUserService` refresh the affected entry (sign-in `LastLoginAt` flows through `IUserWrite.RecordLoginAsync`, whose decorator refreshes the entry directly); Identity-machinery writes that bypass the service surface (`UserManager.UpdateAsync`, OAuth `UserEmail` row creation) are caught by `UserInfoSaveChangesInterceptor` (EF `SaveChangesInterceptor`, registered on both the scoped and factory contexts) and routed through the section-internal `IUserInfoSliceRefresher`. Every contributing table is Users-owned, so no other section signals the cache. `CachingUserService` itself implements `IHostedService` (via `TrackedCache`) and populates the dict at startup; load-all reads call `EnsureWarmed` / `EnsureWarmedAsync` and recover transparently if startup warmup hasn't completed. `UserInfo` is the single cache — there is no separate profile cache (`Docs/health.md`).
- **Read/write interface split.** **Public contract.** `IUserService` (`GetUserInfoAsync`, `GetUserInfosAsync`, `GetAllUserInfosAsync`, `SearchUsersAsync`, `GetByEmailOrAlternateAsync`, `GetRawUserInfoAsync`, `GetAllRawUserInfosAsync`) in `Humans.Base` (`Users/`, namespace `Humans.Users.Contracts`) is the cross-section read surface — only `UserInfo` / `HumanSearchResult` projections plus Guid primitives, no EF entities. `IUserWrite` (on the `Humans.Users.Contracts` leaf) carries only the writes another section calls (onboarding mutations, language, IBAN, login stamp, Google email status, user deletion). The internal `IUserManagement : IUserService, IUserWrite, IUserMerge` in `Humans.Users.Services` adds the section-only half of the write funnel — profile saves, deletion fields, `UserEmail` storage commands, the merge tombstone, the email-problems reads, the accounts-due-for-anonymization read; all three faces resolve to the same `CachingUserService` singleton. Neither interface carries the `IApplicationService` marker; analyzers key on the implementing class. See [`memory/architecture/section-public-contract.md`](../../../../memory/architecture/section-public-contract.md).
- The UserInfo interceptor completes its post-commit cache refresh without request cancellation, so an aborted request cannot turn a committed save into cancellation and skip its following audit.
- **Identity framework surface** — `AccountController` lives in `Humans.Web/Controllers/` (the Shell), not in this section, and signs in through `IUserSignInService`, as do the Development section's dev-login controller and Tickets' gate-terminal seeder. Account-creating seeders use `IAccountProvisioningService.CreateAccountAsync`. There is no `AuthController` or `ManageController` class — magic-link orchestration lives in `IMagicLinkService` (Auth section) and account self-management lives across `AccountController` and Profile views. Non-controller code routes through `IUserService`.
- **GDPR:** `UserService` implements `IUserDataContributor` and contributes the `UserService.Account` slice plus the Profiles-half slices (`Profile`, `ContactFields`, `UserEmails`, `VolunteerHistory`, `Languages`); `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` discovers contributors by reflection — nothing to register in a list (design-rules §8).
- **Resource set:** `UsersResource.{resx,ca,de,es,fr,it,nl}` at the project root (folder and namespace must agree — nobodies-collective/Humans#1365), covering the whole assembly (both parts of this doc). Views bind it as `Localizer` and `SharedResource` as `SharedLocalizer` in `Views/_ViewImports.cshtml`; `UsersResource.cs`'s remarks record the leave-or-duplicate decision for every key that stayed in `SharedResource`. What stayed and why: `Common_`, `Nav_`, `Dashboard_`, `Validation_` and `Privacy_` are generic; `Admin_` is rendered by the Admin shell frame too; `Application*_`/`ApplicationStatus_` are also rendered by Governance's copy of the tier form; `Teams_`/`MyTeams_`/`TeamDetail_` and `Search_` belong to other renderers; `Enum_EmailOutboxStatus_` is Humans.Email's set, reached through `SharedLocalizer.EnumDisplay`. `Views/Profile/Edit.cshtml` keeps `IStringLocalizer<ShiftsResource>` for its shift-preference strings rather than duplicate the copy. `UserArchitectureTests.SectionTypesLocalizeThroughTheSectionsOwnResourceSet` guards the constructor side; `UsersPageRenderTests` asserts no raw key reaches the HTML, in English and in Spanish.
- **Architecture tests:**
  - `tests/Humans.Users.Tests/Architecture/UserArchitectureTests.cs` — `IUserManagement_IUserService_IUserWrite_And_IUserInfoSliceRefresher_ResolveToSameSingleton` (the decorator is the same singleton under every key), and `SectionTypesLocalizeThroughTheSectionsOwnResourceSet`.
  - Analyzer `HUM0001` (`UserEmailLegacyFieldAnalyzer`, tested by `tests/Humans.Analyzers.Tests/UserEmailLegacyFieldAnalyzerTests.cs`) — flags any reference to the legacy shadow properties (`User.GoogleEmail`, `UserEmail.IsOAuth`, `UserEmail.DisplayOrder`).
  - Analyzer `HUM0003` (`IdentityFindByEmailAnalyzer`, tested by `tests/Humans.Analyzers.Tests/IdentityFindByEmailAnalyzerTests.cs`) — flags `UserManager.FindByEmailAsync` / `FindByNameAsync` calls outside the allowed sites; call sites route through `IUserEmailService.FindByAddressAsync` instead.
  - `AccountDeletionService` is covered functionally by `tests/Humans.Users.Tests/Services/AccountDeletionServiceTests.cs`.

### Touch-and-clean guidance

- `User` has no `Profile` / `TeamMemberships` / `RoleAssignments` / `Applications` / `ConsentRecords` / `CommunicationPreferences` navs and no `GetEffectiveEmail()` — readers route through `IUserService` / `ITeamService` / `IRoleAssignmentService` / etc. or use `user.Email` (which overrides via the `UserEmails` collection). When touching `TeamService` / `GoogleWorkspaceSyncService` / `ProfileEmailsController` and the notification jobs, prefer `IUserEmailRepository.GetByUserIdReadOnlyAsync` / `IUserService.GetUserInfosAsync` over reaching into the `UserEmails` nav directly.
- Do **not** inject a DbContext into any service under `Humans.Users/Services/`. Use `IUserRepository`.
- Event-participation writes belong to Tickets (`ITicketWrite`, and Tickets' internal `IEventParticipationManagement`).

---

# Part 2 — Profiles


Per-human personal data that Users keeps: the profile core (names, location, bio, pronouns, birthday, picture, emergency contact, languages, burner CV, medical conditions), contact fields, email addresses, account merge and duplicate detection.

Everything else that once sat on `User`, `Profile` and `UserEmail` has moved to the section that owns it (Users decomposition, commits 1 to 15): IBAN to Finance, dietary answers to Cantina, membership tier to Governance, consent review to Consent, event participation to Tickets, communication preferences to Email, Google Workspace identity to GoogleIntegration. Each old column or table stays on disk as frozen legacy until its replacement's data migration reports done in production and its drop PR merges; see [Frozen legacy](#frozen-legacy-awaiting-drop).

## Concepts

- A **Profile** holds a human's personal information: name, city, country, birthday (month and day only — never year), profile picture, and admin notes.
- **Contact Fields** are per-field contact details (phone, Signal, Telegram, WhatsApp, Discord, custom) with per-field visibility controls.
- **Visibility Levels** determine who can see each contact field: BoardOnly (most restrictive), CoordinatorsAndBoard, MyTeams (shared team members), or AllActiveProfiles (least restrictive).
- **Membership Tier** (Volunteer, Colaborador, Asociado) is Governance's; Users reads it through `IGovernanceService.GetMembershipTierAsync` / `GetMembershipTiersAsync` (popover, human summary, admin detail, duplicate accounts). **Communication Preferences** are Email's (see `Humans.Email/Docs/Email.md`).
- **UserEmail** is a per-user email address record. A user has one "login" email plus zero-or-more verified additional addresses; one of them may be flagged as the notification target.
- **CV Entries** (sub-aggregate of Profile, table `volunteer_history_entries`) record volunteer involvement history.
- **Profile Languages** (sub-aggregate of Profile, table `profile_languages`) record self-assessed proficiency in ISO 639-1 language codes. Profile cards, summaries and both initial and dynamically added language options display language names in the viewer's UI culture across all supported cultures; stored ISO codes and proficiency values stay unchanged.
- **Duplicate Account Detection** scans for email addresses appearing on multiple accounts (across `User.Email` and `UserEmail.Email`, with gmail/googlemail equivalence). Admins act on candidates from the unified **Account Merges** page (`/Users/Admin/AccountMerges`).
- **Email Problems** scans every UserEmail invariant violation (multi/zero IsPrimary, unverified rows, cross-user collisions, orphan rows, ghost AspNetUserLogins). Reads source-of-truth via `IUserService.GetAllUserInfosAsync` (the `UserInfo` cache). Read-only admin surface with deep-links into the per-user `/Profile/{userId}/Admin/Emails` diagnostic for orphan/ghost remediation; the cross-user merge action shares its kernel with `IAccountMergeService.AcceptAsync`.
- **Account Merge** consolidates two accounts into one, transferring all associated data (emails, contact fields, CV entries, role assignments, memberships) to the surviving account.

## Data Model

### User (Identity extension)

User is owned by the **Users/Identity** section; the properties below are the profile-adjacent extensions that Profile consumers read most often. Field-level ownership still belongs here because `CachingUserService` stitches them into `UserInfo`.

#### Google email preference

`User` has no `GoogleEmail` property; the `users.GoogleEmail` column is an EF shadow property (`UserConfiguration.cs`) kept pending a deferred column-drop PR per `memory/architecture/no-drops-until-prod-verified.md`. `UserInfo.GoogleEmail` is gone (Users decomposition commit 15): the Google Workspace address is `IGoogleIntegrationService.GetWorkspaceIdentityAsync`; `UserInfo.PrimaryEmail` is the contact address.

Google sync status is GoogleIntegration's (`google_workspace_identities.Status`). The retired per-address `user_emails.GoogleEmailStatus` column is shadow-mapped using the canonical enum, stored as a string with its existing `Unknown` default, awaiting a separate drop PR. It has no runtime readers or flag-clearing writes.

#### Contact-import properties

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| ContactSource | ContactSource? | null | Where imported from (Manual, MailerLite, TicketTailor); null for self-registered users |
| ExternalSourceId | string?(256) | null | ID in the external source system |

A contact is identified by `ContactSource != null && LastLoginAt == null`. When a contact authenticates, `LastLoginAt` is set and they become a regular user.

#### Dead columns

`UnsubscribedFromCampaigns` and `SuppressScheduleChangeEmails` (see the Part 1 field table) have no writer and no reader beyond the Art. 15 export, which keeps them while the columns exist; `UserInfo` and the cache interceptor do not carry them. `[Obsolete]` guards both, and peterdrier/Humans#2156 tracks their drop, pending Peter's call on whether the opt-outs they hold need carrying into Email's preferences first.

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
| MedicalConditions | string? (4000) | null | **GDPR Art. 9** health data. On the cached `UserInfo`; shown to others only through Shifts' audited reveal (`ShiftsPolicies.MedicalDataViewer`: Admin, Board, ShiftsNoInfo), which records `MedicalConditionsRevealed` against the member. Owner edits their own on `/Profile/Me/Medical` and sees the reveal history there. |
| ProfilePictureContentType | string? (100) | null | MIME type of the stored picture. Doubles as the "has custom picture?" predicate (`HasCustomProfilePicture`) and supplies the file extension — the picture bytes themselves live on the filesystem. |
| ProfilePictureStorageKey | string? (300) | null | Versioned `IFileStorage` key of the current picture (`uploads/profile-pictures/{profileId}-{guid}{.ext}`). Null for pictures still at the legacy deterministic key `{profileId}{.ext}`; readers fall back to it. Cleared with the content type on removal, merge and anonymization. |
| ContributionInterests | string? | null | Skills / availability statement (publicly visible on profile). |
| BoardNotes | string? | null | Notes from the human intended for the Board (self + Board only). |
| AdminNotes | string? (4000) | null | Admin-only notes (not visible to the human). |
| NoPriorBurnExperience | bool | false | When true, CV entries are not required during onboarding. |
| CreatedAt | Instant | — | Set on insert. |
| UpdatedAt | Instant | — | Maintained by services. |

**Indexes:** unique on `UserId`.

`Profile` has no `User` nav. Consumers resolve User data via `IUserService.GetUserInfosAsync`. Aggregate-local navs `ContactFields`, `VolunteerHistory`, and `Languages` are kept.

### ContactField

**Table:** `contact_fields`

Contact fields allow humans to share different types of contact information with per-field visibility controls. Default labels use the viewer's current culture, including legacy email fields; custom Other labels are preserved verbatim. Profile cards and contact lookup details receive the same localized labels.

Contact-field saves apply the same editable-field mapping to new and existing rows. New rows receive a generated ID and creation time; updates preserve both. Historical email rows bypass this mapping and remain read-only. If the post-save UserInfo refresh fails unexpectedly, the save remains successful and the error log retains the exception and owner ID for diagnosis.
Profile-card contact and email visibility badges reuse the email grid's localized privacy labels in all supported cultures; visibility filtering is unchanged.
The private-email label on self-profile cards uses the same localized email label as public addresses.

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

The email-flag diagnostic returns violations without display ordering. Its Google admin consumer resolves names through the existing Users bulk read and orders the page.

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
| ProviderKey | string? (256) | OAuth subject/key (OIDC `sub`) for the linked identity. Stable across Google Workspace email renames. `(Provider, ProviderKey)` is the only legitimate match key for rewriting `Email` (via `UserEmailService.ReconcileOAuthIdentityAsync`) |
| IsGoogle | bool (shadow) | Retained on disk for a separate drop PR; no runtime readers. |
| GoogleEmailStatus | enum as string (50, shadow) | Retains the `Unknown` database default until the separate drop PR; no runtime readers. |
| IsPrimary | bool | Exactly one verified email per user is the system-notification target. Service-enforced via `EnsurePrimaryInvariantAsync`; column persists under legacy name `IsNotificationTarget` per `no-column-drops-for-decoupling.md` |
| Visibility | ContactFieldVisibility? | Stored as string (max 50); null hides the email from profile view |
| VerificationSentAt | Instant? | Last time a verification email was sent (rate limiting) |
| CreatedAt / UpdatedAt | Instant | Maintained by `UserEmailService` |

OAuth reconciliation translates only a collision on `IX_user_emails_Email` into the existing verified-email race exception; unrelated persistence failures retain their original exception and follow the callers’ normal failure handling.

**Indexes:** `UserId`; **unique partial index** on `Email` filtered to `IsVerified = true` (Postgres `"IsVerified" = true`). The cross-account check is service-enforced (`VerifyEmailAsync`, `AdminMarkVerifiedAsync`, the OAuth reconcile, and `AddUserEmailAsync`, which refuses a verified insert with `Users_Profile_EmailVerifiedOnAnotherAccount`); the index is a unique index on an editable string, forbidden by `memory/architecture/unique-constraints-ids-only.md`, and its drop is recorded in `Docs/debt.yml`.

No shadow columns (`IsOAuth` and `DisplayOrder` are gone from the table; `HUM0001` rejects references to either). Display sorting is alphabetical on `Email`.

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

These columns await separate schema drops. A registered data migration's production `done` reading is standing approval under `memory/architecture/no-drops-until-prod-verified.md`. The Workspace identity migration and readers are retired: production reported `google.workspace-identities`, `pending: 0, done: true` on 2026-11-05. Schema drops remain separate feature work under policy D9, outside the debt ledger.

| Legacy object | Replaced by | Migration key |
|---|---|---|
| `user_emails.IsGoogle`, `user_emails.GoogleEmailStatus` | GoogleIntegration `google_workspace_identities` | `google.workspace-identities` |
| `users.UnsubscribedFromCampaigns`, `users.SuppressScheduleChangeEmails` | none (dead, no migration) | none |

`LegacyGoogleEmailStatus` and the legacy `UserEmailInfo` fields are retired.

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
| `/Profile/{id}/Admin/Emails` | Per-user email diagnostic (`UserEmailOperations.View`: self, UsersAdmin, Admin, Board; Board sees it with every edit control hidden) |
| `/Profile/{id}/Admin/Emails/Add`, `/SetPrimary`, `/SetVisibility`, `/Delete`, `/Unlink/{emailId}` | POST — admin mirrors of the self-service email edits (`UserEmailOperations.Edit`: self, UsersAdmin, Admin) |
| `/Profile/{id}/Admin/Emails/ClearPrimary` | POST — admin remediation: drop a single row's flag without auto-promoting a successor. The admin Google column posts to GoogleIntegration's `/Google/Admin/{id}/WorkspaceEmail` |
| `/Profile/{id}/Admin/Emails/AddVerified` | POST (`PolicyNames.AdminOnly`) — add an address already verified |
| `/Profile/{id}/Admin/Emails/Verify` | POST (`PolicyNames.AdminOnly`) — mark a pending plain UserEmail row verified without consuming a token; creates a merge request when the address is already verified on another account |
| `/Shifts/ShiftInfo` | Shift preferences (skills, work style, languages, preferred shift tags) — served by Shifts' `ShiftProfileController`, not this section; the Edit form carries only a link to it |
| `/Profile/Me/Medical` | GET/POST — the member's own medical conditions (Art. 9; trimmed, blank saves null) plus the audit list of who revealed them (`<vc:audit-log>` on `MedicalConditionsRevealed`, entity `User`). The `medical` things-to-do links here while it is empty and Cantina's profile (`GetDietaryProfileAsync`) shows neither a preference nor `DeclinedToAnswer` — Users' own gate ([spec](../../Humans.Cantina/Docs/features/dietary-nudge.md)). Dietary answers are Cantina's `/Cantina/Dietary`. The form’s hint and visibility note are translated into all supported cultures. |
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
| `/Auth/Admin/Roles` | System-wide role-assignment roster, filterable by role (`UsersPolicies.Read`) — served by Auth's `RoleAssignmentsController` (`role_assignments` is Auth's); Auth adds the sidebar entry to the Users admin group |
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

Users' one-off copies are **data migrations** (`ISectionDataMigrations`, implemented by `Section.cs`), listed and applied on Debug's `/Debug/DataMigrations` (`AdminOnly`, review every pending row → confirm → `DataMigrationApplied` audit) and reported, counts only, by the anonymous `/api/data-migrations`. Each resolves Users' own services, so the copy stays here; each is idempotent and its status lists exactly what its apply can fix. The remaining copy replaces the retired `/Profile/Admin/EmailProblems/BackfillLegacyEmails` screen.

| Key | Copies | Per-row audit |
|-----|--------|---------------|
| `users.legacy-identity-emails` | A verified `UserEmail` row from the legacy `User.Email` Identity column, via `IEmailProblemsService.BackfillLegacyIdentityEmailsAsync` (the Email Problems page still lists these rows and links to the migration); a row the add refuses is skipped, logged, and stays listed | `LegacyIdentityEmailBackfilled` |

The legacy-email scan and migration share the same eligibility selection: a non-tombstone with a nonempty legacy address and no case-insensitive matching verified `UserEmail`. Profile-less accounts remain eligible.

Email Problems remediation buttons use the shared `data-confirm` handler: each click prompts once, and cancellation prevents submission.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View and edit own profile, manage own emails, manage own contact fields, upload profile picture, set notification and communication preferences, request data export (GDPR Article 15), request account deletion |
| Any active human | View other active humans' profiles (contact fields restricted by per-field visibility). Send facilitated messages to other humans. Search for humans |
| Coordinator (any team) or ShiftsPolicies.PrivilegedSignupApprover | On another human's `/Profile/{id}`, view the **Sent messages** panel — a history of in-platform messages sent to that human (`EmailAuditActions.FacilitatedMessageSent`, up to 50 entries), rendered by `<vc:audit-log layout="table">` with `column-labels` carrying the page's own `Common_Date`/`Common_Sender`/`Common_Preview` — the controller decides visibility, AuditLog owns the read, Profile owns the copy. Not shown on own-profile views. |
| UsersAdmin, Board, Admin | View any profile with full detail and the human admin pages (`UsersPolicies.Read`). |
| UsersAdmin, Admin | Manage humans via admin pages (suspend, unsuspend, approve volunteer, reject signup, view audit log; role assignments are added and ended on Auth's pages, linked from the admin detail page) — `UsersPolicies.Manage`; Board reads these pages and changes nothing. (Membership tier changes go through tier applications in Governance, not the profile admin page.) |
| Admin | Review duplicate-account candidates and approve/reject `AccountMergeRequest`s at the unified `/Users/Admin/AccountMerges` queue (`PolicyNames.AdminOnly`; **Users** section — see [Part 1 — Users / Identity](#part-1--users--identity). |
| Admin (non-production only) | Purge a human and all associated data |

## Invariants

- Profile summaries and duplicate-account cards derive active/pending status from the account state, never consent-review approval. The member profile does not present Board approval as a prerequisite for Volunteer access.

- The medical things-to-do reuses Cantina’s shared `Todo_DietaryMedical_Action` label, translated into all supported cultures.
- The section’s things-to-do contribution forwards caller cancellation through all data reads. Aborted reads propagate instead of being logged or converted into missing prompts. The shared collector materializes each contributor's entries before appending, so an enumeration failure drops that contributor's whole batch while retaining healthy contributors; request cancellation still propagates.

- The `UserEmails` GDPR export includes each owned address’s Google-selection flag, Google sync status, and UTC creation, update and nullable last-verification-email timestamps. Existing export keys retain their names; other users’ addresses are excluded.

- The live burner-name collision warning invalidates pending responses and hides the previous name’s warning on every input, before debounce; clearing the field cannot revive an older result.

- Member communication-preference saves reject redirects. An expired or revoked session rolls back the changed checkbox and shows the existing failure feedback instead of confirming a save.

- People search evaluates all eligible cached candidates before its safety cap, selecting by relevance score with a stable user-ID tie-breaker. Callers retain display ordering; field flags, visibility filters and GUID merge resolution retain their existing rules.
- Exact verified-email profile lookup returns the resolved survivor's id and uses it for visible contact details, matching lookup by id. Its single-owner and searchable-account guards remain in force.
- People-search bio and contribution-interest excerpts preserve complete Unicode characters at both context boundaries. Built-in match labels (including exact user-id matches and contact-field types) use the caller’s current UI culture in all supported languages; authored custom contact labels remain verbatim. Labels are composed per search, so switching UI culture does not reuse labels from an earlier query.
- Profile API search, burner-name count and user-id lookup carry request cancellation through current-user resolution as well as their subsequent reads.
- Profile shift-history and sent-message visibility checks carry request cancellation through coordinator checks and no-show history reads. The profile controller orders no-shows by review time, newest first and undated entries last. Reviewer IDs pass through to Human rendering without a controller name lookup; absent reviewer IDs remain blank, while unresolved IDs use the shared localized unknown label.
- A profile request resolves the viewer's coordinator status once for both visibility checks. Self-profile requests skip that read and show neither panel; each panel retains its existing privileged-role check.
- Human-picker prefills pair the resolved name with the resolved account ID, so an archived account ID is never reposted as the selected value.

- The medical GET carries request cancellation through its current-user read. An abandoned request propagates cancellation.

- The medical page, its save feedback and the profile's dietary/medical links render in all supported cultures.

- Users no longer writes food answers: `/Profile/Me/Edit` links to Cantina's dietary form instead of embedding it, and no save path touches the legacy food columns ([dietary nudge](../../Humans.Cantina/Docs/features/dietary-nudge.md)).
- Profile-editor cross-field guards return ordinary refusal keys before any profile, picture or CV write. The profile form localizes them using existing messages; refused saves stop language/contact saves and consent checks. Unexpected validation diagnostics are logged and replaced with generic translated feedback.
- Every authenticated human can edit their own profile regardless of membership status (available during onboarding). Profile fields and their nested contact-field, Burner CV, and language forms, medical details, facilitated messages, and email-add forms use shared validation messages in all supported cultures; their input limits are unchanged.
- Facilitated member messages use the recipient’s supported preferred language, with English fallback for blank, malformed or unsupported preferences. Team reply-to, contact sharing, opt-out checks and audit behavior are unchanged.
- Profile contact rows require an explicit supported contact type and visibility before any profile save. Missing visibility cannot default a private contact to AllActiveProfiles, and the retired email contact type cannot be authored. Historical email contacts appear read-only in the owner editor and survive ordinary contact replacement, including stale attempts to relabel their IDs. Only explicitly selected legacy IDs on that owner's profile are removed; other profiles' IDs cannot be deleted. Validation failures reload these rows from the owner's cached profile. Existing visibility, GDPR export and account-deletion paths still apply.
- Contact field visibility is enforced per-field: a human viewing their own profile sees everything. Board members see everything. Coordinators see CoordinatorsAndBoard-level and below. Shared-team members see MyTeams-level and below. Other active members see only AllActiveProfiles fields. Each call derives permissions for its supplied viewer; a reused service cannot carry another viewer’s roles or team membership into the result.
- Birthday stores month and day only — never year. UI text uses "birthday", not "date of birth". Profile birthday displays contain only the localized month and day, with no weekday derived from the storage year; February 29 remains valid. The editor’s month names follow the selected UI language; numeric option values and English input parsing stay unchanged. Displayed location coordinates also follow the UI culture; hidden coordinate form values retain English input formatting.
- Membership tier (Volunteer, Colaborador, Asociado) is owned by Governance, not the profile and not a role assignment. Initial profile setup shows the tier descriptions and links to `/Governance/Applications/Create` (new tab); the profile form carries no tier fields and submits no application.
- The Consent Coordinator review (status, approval, rejection record) is Consent's (`consent_reviews`, Users decomposition commit 12) and gates nothing here: Volunteer admission is name + required consents. Users holds only the resulting `UserState.Rejected`. The admin detail page's consent-check badge and rejection alert are Consent's `AdminDetail` user part.
- App access is gated by the stored `User.State` (`UserState`: Bare/Active/DeletePending/Suspended/Rejected/Deleted/Merged/AdminSuspended). `Profile` carries no state column. `UserStateClassifier.IsWalled` is the one definition of the states shown the account-status wall (Suspended/AdminSuspended/Rejected/Deleted/Merged); `MembershipRequiredFilter` and Gdpr's `/Gdpr/Status` both read it.
- `SuspendNonCompliantMembersJob` owns the suspension sweep's run metrics: every completed invocation records one success, including an empty candidate list or no changed users; failures are recorded and rethrown at that same boundary. `NonCompliantMemberSuspension` records only per-member suspension metrics.
- The consent-expiry sweep attempts an audit for every committed suspension, even when prepared notice metadata is missing. Audit persistence is best-effort and does not change the committed suspension.
- Suspension notices use the recipient’s supported preferred language (English fallback), including the title, action, and consent-expiry email reason. Administrative suspension reasons are excerpted only when needed to fit the 2,000-character notification body; the full original reason remains in the suspension audit entry. Admin and consent-expiry notices reuse the account-status resources; suspension audit entries keep their existing text and actor.
- `HumanLifecycleService` keeps caller cancellation on reads and suspension-state writes. After a successful state write, administrative suspension notices and both manual/consent-recovery notice resolution finish independently of caller cancellation; delivery failures remain best effort.
- Profile editor setup mode follows a missing profile or `UserState.Bare`, with an explicit GET preview override. Consent Coordinator review never hides email management from an active member. POST error redisplays derive the mode from the account rather than trusting the posted flag.
- Profile deletion request sets `User.DeletionRequestedAt` and `User.DeletionScheduledFor = now + 30 days` on the User record. The pending marker and immediate team-membership/role revocations share one ambient transaction through the owning sections’ existing interfaces. Their best-effort audit follows the committed transaction. A failed metadata step rolls the request back, then refreshes Users’ potentially provisional cache entry without caller cancellation; a failed refresh evicts it for reload. An unwritten pending marker returns `NotFound` before revocation or notices. Actual data purge is deferred to a background job. The request result, confirmation email, privacy page and deletion status wall report the later of the grace deadline and any ticket-holder event-hold floor, matching the job’s eligibility check. The existing `UserInfo` read model exposes this derived value as `EffectiveDeletionDate`.
- Data export returns all personal data as a JSON download (GDPR Article 15), using Gdpr’s shared `GdprExportSerializer` for the same document format as the guest route. The `Account` slice includes the stored lifecycle state and nullable post-event deletion hold alongside the existing deletion request and grace-period timestamps. The communication-preference slice includes the stored first-subscription timestamp as UTC ISO 8601, or null when unknown. If the complete export fails, it is logged and Gdpr's `DownloadData` route returns the user to My Data with a generic error instead of emitting a partial file or an unhandled response; a request-abort cancellation propagates instead. `AccountMergeService` is an `IUserDataContributor` per design-rules §8a (the `AccountMergeRequests` slice); the profile slices are emitted by `UserService`. `ProfileService` implements no contributor interface. The orchestration lives in `GdprService`. Users resolves archived request IDs to the survivor before exporting moved rows. The `MergedProfiles` collection includes every archived profile in the transitive merge chain, with its user ID and the existing profile export shape, including retained dietary and medical data; unrelated profiles are excluded. The existing deletion fan-out erases those archived profiles too.
- Profile pictures are stored on the filesystem via `IFileStorage` — the only store; there is no DB fallback column. The completed `users.profile-pictures-to-filesystem` registration, migration snapshot and repository scan are retired: production `GET https://humans.nobodies.team/api/data-migrations` reported `{"key":"users.profile-pictures-to-filesystem","pending":0,"done":true}` on 2026-10-22. `GetProfilePictureAsync` checks `ProfilePictureContentType` as the GDPR gate: null → 404, even if a stale file exists on disk. The edit route permits 21 MB for multipart overhead; uploaded images are validated against an allowed-content-type set (JPEG, PNG, WebP, HEIC/HEIF, AVIF) and a 20 MB file cap, then oriented and resized by `ProfilePictureProcessor` to a long-side of 1000 px and re-encoded as JPEG before persistence. Upload metadata (including EXIF location tags and comments) is stripped after applying orientation; the ICC color profile is preserved. This applies to new uploads; existing pictures are not rewritten. The section-only profile save writes each upload to a new versioned key (`uploads/profile-pictures/{profileId}-{guid}{.ext}`, stored in `ProfilePictureStorageKey`) before committing metadata, so a failed save leaves the previous picture's bytes intact. Cleanup verifies the committed picture reference without request cancellation: only a confirmed unreferenced upload is deleted; committed or unverifiable uploads are retained, and the original save failure propagates. A null stored key means the legacy key `uploads/profile-pictures/{profileId}{.ext}`. GDPR erasure deletes whichever key the profile resolves to. Non-cancellation file-write failures retain the previous picture metadata while allowing other profile edits to save; cancellation aborts the save. The editor deletes the previous file only after the save commits and its key differs from the current one. Account merges also delete the archived profile’s resolved picture file after the profile fold commits, preserving the survivor’s picture. Cleanup ignores request cancellation and logs failures; the content-type gate keeps stale files inaccessible.
- `CachingUserService` (Singleton) and `IUserInfoSliceRefresher` must resolve to the **same** instance — both registrations point to the single decorator. Two instances would split the `ConcurrentDictionary<Guid, UserInfo>` cache and silently lose invalidations.
- Purging a human permanently deletes the account and all associated data, including severing the OAuth link so the next Google login creates a fresh account. Purge is disabled in production environments. No one can purge their own account.
- Batch user-email lookup reads the bounded email directory once and uses `OrdinalIgnoreCase` for literal addresses plus the shared `GmailAliasEmailComparer` for Gmail dot/plus/domain aliases. Exact matches precede aliases, each stored row appears once, and other domains retain their dots and plus suffixes regardless of server culture.
- Duplicate account detection applies gmail/googlemail equivalence when scanning for address collisions. User and user-email lookup services share the section-internal `EmailLookupAliases` for Gmail/Googlemail query variants.
- `AccountMergeService` writes and `DuplicateAccountService` reads go through this section's repositories and `IUserService` — never through cross-section `DbSet` reads.
- `AccountMergeService.AcceptAsync` is the **fold-into-target** orchestrator: it re-FKs every owning section's user-scoped rows from source to target via per-section `Reassign…ToUserAsync` methods, then tombstones the source User row (sets `MergedToUserId` + `MergedAt` via `IUserManagement.AnonymizeForMergeAsync`) — it does NOT delete or wipe the source. Append-only history (audit log, consent records, budget audit log) stays at source by design and is surfaced via chain-follow reads.
- Public profile cards hide hidden team memberships and hidden parent-name prefixes on visible child memberships. Self/admin cards retain both, including the existing self-profile exception for hidden memberships.
- Public-mode profile cards resolve the authenticated viewer before Board, contact-field and email-visibility checks. These permissions use the surviving viewer id, never a merged-away cookie id. Missing or malformed viewer identities render no card and never fall back to the target's self-access. Self/admin cards retain their host-authorized visibility; anonymous coordinator popovers use their separate public summary.
- Audience segmentation includes orders with either a matched buyer or a matched attendee in both ticket counts and available purchase years. Wholly unmatched orders do not add filter years; year filtering uses UTC purchase dates and counts each matched account once.
- Profile cards order visible email addresses alphabetically (ordinal, case-insensitive); the email service owns verification and visibility filtering, while each consumer owns its display order or address selection.
- Profile cards, summaries/popovers, admin details and the profile editor order spoken languages by proficiency descending, then language code using ordinal case-insensitive comparison. Their presentation projections own the order; the profile snapshot preserves the supplied language rows without sorting.
- Profile cards and the editor order volunteer history by date descending. The snapshot keeps the supplied rows without sorting; people search explicitly selects the newest matching entry for its history snippet. The GDPR export retains its existing date/creation-time order.
- Profile cards and both current/legacy contact-field editor projections apply authored `DisplayOrder`. Visible-field DTOs carry that scalar without imposing list order. Search selects the lowest-order matching field within its authorized public/private bucket; snapshot creation does not sort contact fields. Profile API contact selection retains its own type/label/value priority.
- Spoken-language proficiency badges on profile cards and hover/inline popovers use the existing localized `Enum_LanguageProficiency_*` resources, matching the profile editor instead of exposing raw enum names. The initial-setup tier explanation also localizes its Volunteer label in all supported cultures; Colaborador and Asociado retain their legal names.
- Admin summary ordering belongs to presentation: the preferred-language card's view component orders aggregate counts descending, and the audience controller orders available purchase years newest first. The calculators return aggregates without display sorting; their cohorts and filters are unchanged.
- The preferred-language flag (rendered next to a person's name on `ProfileCard` and `_HumanPopover`) is visible only to `UsersAdmin` / `Board` / `Admin` viewers — general active humans see other people's profile cards and popovers without it. Self-view is unaffected (the flag isn't shown there in the first place; preferred language is editable on `/Profile/Me/Edit`). `ProfileCard`'s `nobodies.team` account badge and public email label use the Users resource set. Its profile, permissions, contact fields, email, team, and messaging-preference reads all receive `HttpContext.RequestAborted`; the standalone membership-tier and `nobodies.team` badges do too. The admin-only Users widget gallery passes it to its current-user lookup as well. Profile cards render identity and contact details without loading membership snapshots or consent-review annotations; those do not contribute to the card.
- Public coordinator popovers use the shared coordinator label in all supported cultures, preserving the team name and the existing public-coordinator gate. Profile popover requests reject redirects, so an expired session’s login page is never embedded or cached as profile content. Network/HTTP failures dispose the loading spinner and allow a later hover to retry; 404 continues to suppress the popover without another probe.
- The authenticated `_HumanPopover` (`GET /Profile/{id}/Popover`) also renders the person's active-season camp name + camp roles, sourced from `ICampService.GetCampUserInfoAsync`. This block is gated to `AnyAdminRole` viewers (admin-only) and is omitted when the person is not an Active member of any camp this season.
- `GET /Profile/{id}/Popover` requires a Profile row and returns 404 for an unknown user or a known user without one. The temporary sparse hover-card fallback retired with the completed `users.stub-profiles` copy. Inline `HumanSummary` cards retain their handling for unavailable historical user/profile references.
- Facilitated messaging (`/Email/SendMessage/{id}`) is a **conduit, not a mailbox** — Humans relays one-off emails between humans and stores nothing: no message persistence, no threading, no attachments. The body is edited through the shared `_EmailComposer` (Base) and rendered through `SanitizedMarkdownRenderer` (Markdown, `https://`-only images); max 2000 chars. The recipient's address is never disclosed to the sender. The pages, the relay, the audit entry and the team Reply-To rules (`TeamMessageOptionsProvider`) live in Email (`FacilitatedMessageController`); the profile card here only shows the "Send a message" button, hidden when the recipient opts out. A coordinator may send with `Reply-To` set to a team Google Group only when `TeamMessageOptionsProvider` (in Email, an orchestrator over `ITeamService` and `ITeamResourceService`, no tables) offers the team: the viewer coordinates an active team with a Google Group prefix, and that team has an active `Group` resource with a successful sync, no error, and a `Url` equal to the group's `https://groups.google.com/a/{domain}/g/{prefix}` (the one field both GoogleIntegration link paths write consistently). `FacilitatedMessageController` only looks the requested team up in that list, on GET and on POST alike, and Email's `TeamMessageButtonsViewComponent` (a `Profile` user part) sorts it for display. Personal sends set `Reply-To` to the sender's notification email only when the sender ticks "include my contact info". Every send uses `EmailAuditActions.FacilitatedMessageSent`; team sends name and link the team in the audit entry without copying its address into the human's audit description.
- `<vc:user-parts slot user-id view-mode>` (Base's `UserPartsViewComponent`) collects every active section's `IUserPart` descriptors for a named slot (`UserPartSlots`), orders them by weight, and invokes each component `Type` with a `UserPartArgs(userId, viewMode)`; descriptors are materialized per contributor before appending, so a failure during enumeration logs Error and discards that contributor's whole batch while healthy contributors remain; contributing components own their data and any visibility beyond the host's gate. Hosted at: `user-profile` (own/other profile main column, Events is the reference contributor; Campaigns' `MyCodesViewComponent`, own profile only); `user-profile-sidebar` (profile right sidebar, own-profile only); `user-admin-detail` (AdminDetail main column — Calendar's `UserCalendarViewComponent`, Auth's `RoleAssignmentsCardViewComponent`, Campaigns' `CampaignCodesViewComponent`, Email's `OutboxCardViewComponent` and `CommunicationPreferencesCardViewComponent`); `user-admin-detail-sidebar` (AdminDetail right sidebar — Tickets' `TicketHoldingsViewComponent`, gated to non-Public view modes); `board-vote-applicant` / `consent-review-applicant` (Governance's and Consent's applicant detail pages — Users' own `ProfileCardViewComponent`, Admin view mode); `ticket-transfer-party` (Tickets' transfer admin detail, sender and receiver — Users' `HumanSummaryViewComponent`). Users contributes the last three via `Humans.Users/Section.cs`. Email’s `CommunicationPreferencesPanelViewComponent` uses the Email resource set (`Email_CommPrefs_*`), including its non-editable "Always on" state, and passes the request-abort token to both preference and ticket-holdings reads. `HumanSummaryViewComponent` also passes it through its profile and team reads. Profiled inline summaries and hover popovers share `ProfileSummaryViewModelBuilder.BuildWithProfile` for identity, canonical `UserInfo.ProfilePictureUrl`, membership badges, ordered public/hidden teams and languages; the inline component retains its missing-reference handling.

## Negative Access Rules

- No one **can** read or edit another member's medical conditions through `/Profile/Me/Medical` — it takes the user from the principal and has no target-user parameter. (The dietary form moved to Cantina's `CantinaProfileController`, which keeps the same own-user-only rule.)
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
- **Onboarding:** none (one-directional — `OnboardingService` consumes `IUserService`, never the reverse). The consent-check threshold (Consent's review `Status → Pending`; no notification, the queue shows in the admin sidebar's Review pill) is director-level work and lives in Onboarding; `ProfileController.Edit` reports the save with `IOnboardingIntake.ProfileSavedAsync` after `IProfileEditorService.SaveProfileAsync`, and Onboarding also cancels a pending deletion for a not-yet-approved member.
- **Cantina:** `ICantinaService` (project reference; acyclic — Cantina reaches only `Users.Contracts`) for the medical things-to-do. Cantina owns the food answers, their form and nudge, and its own export/erase/merge.
- **Finance:** none at compile time. The admin detail page's Payment Details card and its reveal (`/Finance/Admin/Members/{id}/RevealIban`) are Finance's, contributed to the `AdminDetailSidebar` user-part slot.
- **Google Integration:** owns the member's Google Workspace identity (`google_workspace_identities`, Users decomposition commit 15) — `IGoogleIntegrationService.GetWorkspaceIdentityAsync` is the address Google Groups and Drive sync target. Users reports addresses leaving an account via `IGoogleIntegrationWrite.ClearIfAddressAsync`; the Emails grid's Google column, the sync-rejected banner, the admin detail's Google Sync / @nobodies.team cards and the @nobodies.team badge are GoogleIntegration view components.
- **Users/Identity:** `IUserService.GetUserInfosAsync` — display data for cross-domain nav stitching. `IUserManagement.AnonymizeForMergeAsync` — invoked by `AccountMergeService.AcceptAsync` to tombstone the source User during fold; AspNetUserLogins re-FK is handled by the Users section's own `IUserMerge` implementation (`IUserRepository.ReassignLoginsToUserAsync`); event participation by Tickets' `IUserMerge`.
- **Account merge fold fan-out:** `IAccountMergeService.AcceptAsync` fans out across every registered `IUserMerge` implementation (`ReassignAsync`), each committing independently and evicting its own affected caches before returning. Preflight reads honor request cancellation; once the section-write phase starts, the remaining fold, pending-email verification, tombstone, audit and merge-request closure finish independently of browser cancellation. Dependency failures still propagate for the existing retry path. Each owning section — Tickets, Teams, Shifts, Governance, Campaigns, Camps, Notifications, Email, Role assignments, and the Profiles sub-aggregates (Profile / UserEmail / ContactField) — registers its own `IUserMerge` impl and re-FKs its user-scoped rows source→target, rather than the orchestrator naming each service.

## Architecture

**Owning services:** `ProfileService` (`IProfilePictureService` — read-only pictures and migration snapshots), `ProfileEditorService` (`IProfileEditorService` — validates and orchestrates profile-edit saves, delegating the actual persist to `IUserService`), `ContactFieldService`, `UserEmailService`, `EmailProblemsService`. Account merge and duplicate detection are Part 1's (`AccountMergeService`, `DuplicateAccountService`).
**Owned tables:** `profiles`, `contact_fields`, `user_emails`, `volunteer_history_entries`, `profile_languages`
`IUserRepository` owns the live persistence paths via `DbContext`. The retired preference repository is removed. Repositories are Singleton, using `IDbContextFactory<UsersDbContext>` and short-lived contexts per method.
- **Caching.** Profile field storage (save, medical conditions, volunteer history, anonymize) is on `UserService`; readers get the single unified `UserInfo` projection served by `CachingUserService` (Singleton, `Humans.Users/Data/`) — see Part 1's caching-decorator bullet. There is no separate profile-only cache or warmup service (`Docs/health.md`).
- **`UserInfo` is canonical.** Cross-section email projections use the resolved snapshot for living humans or the exact raw snapshot for per-account erasure. Derived properties `PrimaryEmail` and `AllVerifiedEmails` are the email read sites (the Google Workspace address is GoogleIntegration's `IGoogleIntegrationService`); `CachingUserService` populates them from already-loaded `UserEmail` rows (no new repo lookups). The lifecycle marker is the stored `User.State`, surfaced as the non-nullable `UserInfo.State`; `ProfileInfo` carries no state at all.
- **Stub Profile invariant.** New imported accounts commit their User, provisioned UserEmail and stub Profile in one local transaction; failures roll back all three so a retry cannot duplicate an orphan account. The creation audit follows the local commit, and UserInfo refresh follows commit or rollback (replacement imports retain their outer transaction’s refresh). Import and magic-link provisioning retries complete a missing Profile before returning an existing account; the idempotent ensure preserves an existing Profile and its names. Each repaired Profile records `StubProfileBackfilled` after save (import service or signup actor), and another failure propagates so the caller can retry. Every newly created User materializes a bare Profile inline at the User-creation call site (`ExternalLoginService.CompleteExternalLoginAsync`, `AccountProvisioningService.FindOrCreateUserByEmailAsync`/`CompleteMagicLinkSignupAsync`); the User row carries `UserState.Bare`. `IProfileEditorService.SaveProfileAsync` (via `IUserManagement.SaveProfileAsync`) promotes `User.State` to `Active` once `BurnerName`/`FirstName`/`LastName` are all populated. The completed `users.stub-profiles` registration and temporary hover-card fallback are retired (production proof above). `EnsureStubProfileAsync` remains the normal provisioning/retry seam; it still refuses tombstones and audits successful repairs.
- Profile-edit POST redisplays restore the avatar user ID and current-picture flag from the signed-in account before validation, preserving editable input on validation or save refusal. Picture removal remains an explicit posted choice. The editor's avatar uses Human rendering; unused picture URLs and the old email projection are not carried in its model.
- The profile page renders another member’s heading through Human using their resolved user ID; its plain-text page title is reserved for the document title. The admin profile heading also renders through Human. Profile and admin shift panels pass only the subject ID and view mode, without a name override.
- Authenticated and public popovers render names through Human by resolved user ID; the public coordinator gate and reduced fields remain unchanged. Popovers initialize on hover or keyboard focus, and public cards without links are focusable. Merge-choice labels and merge-history links use Human’s raw mode so tombstones retain their own identity and admin link; merge-history links keep hover popovers disabled. Missing identities use the shared localized unknown label. Their models and merge/duplicate DTOs carry no copied names.
- Admin and summary view models, merge-request snapshots and duplicate-account DTOs carry no picture URL copies; their views resolve avatars through Human by user ID. Canonical UserInfo and search/API picture URLs remain available to their live consumers.
- Profile cards render their heading, localized message-button label and avatar through Base's shared Human component using the resolved user ID. The card model carries no name or picture copies; the Profile's legal-name and board-note fields retain their existing Public/Self/Admin visibility.
- **Missing display names.** Base’s shared human component resolves missing or empty names through localized `Common_Unknown`; a real BurnerName equal to `Unknown` remains unchanged.
- **Names live on `User`, dual-written from `Profile` (nobodies-collective/Humans#1097).** `UserRepository`'s profile `AddAsync`/`UpdateAsync` seam copies `BurnerName`/`FirstName`/`LastName` onto the owning `users` row in the same save; the merge, purge and GDPR-anonymize paths overwrite them with their tombstone labels so no purged name can resolve. `Profile` remains the read source for the legal name; only `UserInfo.BurnerName` reads the `users` column first. The completed `users.names-onto-user` registration and its migration-only `IUserNameSyncService`/implementation are retired: production `GET https://humans.nobodies.team/api/data-migrations` reported `{"key":"users.names-onto-user","pending":0,"done":true}` on 2026-10-22. Normal profile add/update still mirrors names in the same save. The Profile columns remain for their current readers and writers; this retirement makes no schema change.
- Notification-address reads reuse cached `UserInfo` email facts: delivery targets follow merges and use canonical `Email` (including its fallback), omitting missing or whitespace-only addresses; primary-only lookups use raw account IDs and verified `PrimaryEmail`, without merge forwarding or fallback. Neither projection re-queries the email table.
- Email add/update/remove writes keep the caller's cancellation token until save. After a successful save, their caching decorator finishes its email-slice refresh without request cancellation before returning, so the caller can complete its audit or notification. Read-only refresh entry points remain cancellable.
- **`UserEmail.IsPrimary` invariants.** Service-layer guarantee in `UserEmailService.EnsurePrimaryInvariantAsync` (exactly one verified `IsPrimary` per user, recovers from zero/multi states). Account-merge fold preserves target's `IsPrimary` and demotes the source's. No DB unique index — per `memory/architecture/db-enforcement-minimal.md` the service is the contract; a DB partial unique index would push violations to runtime as untyped `DbUpdateException` failures rather than service-layer recovery (column persists under legacy name `IsNotificationTarget` per `no-column-drops-for-decoupling.md`). **`UserEmailService.ClearPrimaryAsync` is the duplicate-flag recovery path** — it drops `IsPrimary` from a single row *without* invoking `EnsurePrimaryInvariantAsync`, but only when at least one other verified `IsPrimary` row exists; it returns `false` otherwise so the user can never end in a zero-verified-primary state via this path. Surface (admin and self): `/Profile/{id}/Admin/Emails/ClearPrimary` and `/Profile/Me/Emails/ClearPrimary` — both UI buttons appear only when ≥ 2 verified `IsPrimary` rows exist, and the service rejects direct form replay below that threshold.
- **Email delete guards (issue nobodies-collective/Humans#758).** `UserEmailService.DeleteEmailAsync` returns `DeleteEmailResult` refusal keys for removal of (a) the primary email — the user must promote another verified email to primary first — (b) the last verified email, and (c) any email whose address matches the user's event ticket (order buyer or matched attendee), read via `ITicketService.GetTicketOrdersAsync` (lazy-resolved through `IServiceProvider` to break the `TicketQueryService → IUserEmailService → ITicketService` DI cycle). The `/Profile/Me/Emails` grid hides the Delete action for primary and ticket-linked rows (the Linked Accounts card reads `UserEmailEditDto.CanUnlink`, the `UnlinkAsync` invariant); the service re-validates all guards server-side for self and admin paths. Both routes localize refusal keys and audit only successful deletions; missing or foreign rows return the existing localized not-found refusal. Expected refusals log Warning with row/user identifiers; unexpected member/admin deletion dependency failures propagate to normal error handling. Admin recovery for a deleted ticket/primary email uses the existing "Add verified email directly" card (`AdminAddVerifiedEmail`) + `AdminSetPrimary`.
- **Email re-add fix (issue nobodies-collective/Humans#758).** `UserEmailService.AddEmailAsync` rejects an address already held by a live `UserEmail` row on that account. The prior check used `GetPrimaryEmailAsync`, which falls back to the legacy `AspNetUsers.Email` column — so a user who deleted their primary row could never re-add the same address (the original incident). Workspace designation is resolved by GoogleIntegration.
- **Email flag updates return expected refusals.** Missing rows and unverified notification targets return `UpdateEmailResult.ErrorKey`. Member and admin target/visibility routes localize refusals and audit only success; refused updates preserve flags and cached data. Set-primary, visibility and clear-primary refusals log Warning with row/user/actor identifiers. Their member/admin actions propagate unexpected dependency failures instead of showing them as validation feedback. Verification callers check inner results before success or audit.
- **Adding an email returns expected refusals.** Invalid format, an address already on the account, and a pending merge return `AddEmailResult.ErrorKey` before creating a row or token. Member and admin routes localize the key and send no verification email or admin addition audit for refused requests. Both routes share verification link, recipient-culture and message construction. Verification messages explicitly name the account being verified as their outbox owner, so a new unverified address or an address also held by someone else cannot omit or misattribute that account's history, export or erasure. Once an admin addition returns committed, its actor audit precedes verification queueing and both finish independently of request cancellation; a queue failure propagates without erasing the addition audit or claiming success. The owner also returns `UserEmailAddResult.ErrorKey` for invalid syntax or a trimmed address longer than the stored 256-character limit, duplicate additions without `IgnoreExisting`, and missing/deleted/merged accounts; refusals log Warning and perform no insertion or invariant repair. Verified additions expose the same result: an existing address is a successful no-op (`Added=false`, no key), distinct from refusal. Both admin addition actions handle keys and propagate unexpected dependency failures. Signup, import, Workspace and development callers explicitly abort on refusal before later workflow steps; signup retains its user rollback.
- **Overlapping email additions cannot duplicate the address.** The owner serializes its fresh duplicate check, insertion and required flag repairs. A concurrent addition that finds the committed row returns the same expected refusal and creates no verification token or email.
- **Unlink and Delete operate on disjoint row sets.** `UserEmailService.DeleteEmailAsync` returns an unsuccessful result for Provider-attached rows (guards non-UI callers; the grid never routes one there), and `UnlinkAsync` operates only on rows with `Provider`+`ProviderKey`: it removes the `AspNetUserLogins` entry via `UserManager.RemoveLoginAsync` **before** deleting the email row, and hard-fails (no email-row deletion) if login removal fails. After Identity removal succeeds, email-row deletion, invariant repair, cache refresh and the actor audit finish independently of request cancellation, keeping the two records synchronized across browser disconnects. Earlier guard reads remain cancellable. `UnlinkAsync` returns `UnlinkEmailResult` with a localized refusal key if no *other* verified email would remain — the guard runs even when the row being unlinked is itself unverified, because an unverified row can still carry the user's only OAuth login (issue nobodies-collective/Humans#731). Member unlink routes localize that result; expected unlink refusals log Warning with row/user/actor identifiers, while unexpected member/admin unlink dependency failures propagate to normal error handling. Admin mirror routes exist for both (`/Profile/{id}/Admin/Emails/Unlink/{emailId}`, `/Delete`).
- **Inner service** is `Humans.Users.Services.UserService`, registered as `AddKeyedScoped` under `CachingUserService.InnerServiceKey` (`"user-inner"`) — the same decorator/inner pair Part 1 describes for the User aggregate; profile writes go through it too since `UserService` absorbed `ProfileService`'s storage-mutation methods. The decorator resolves it per-call via `IServiceScopeFactory`.
- **No cross-section cache signal.** Every `UserInfo` table is Users-owned: other sections write through Users' services, whose decorator pass-through and `UserInfoSaveChangesInterceptor` refresh the entry. Users' own services that write past the decorator use the section-internal `IUserInfoSliceRefresher`, aliased to the same Singleton.
- **No cross-domain navs:** `Profile` and `UserEmail` carry no `User` nav. Display stitching routes through `IUserService.GetUserInfosAsync`.
- **GDPR:** `AccountMergeService` implements `IUserDataContributor` (design-rules §8a) and emits the `AccountMergeRequests` slice, including each request involving any account in the subject’s resolved merge chain once. Requests under archived IDs use that same chain; unrelated requests are excluded. The `Profile`, `MergedProfiles`, `ContactFields`, `UserEmails`, `VolunteerHistory` and `Languages` slices are emitted by `UserService` — `ProfileService` implements no contributor interface. Each contributor declares its own export keys as constants on its class (`AccountMergeService`, `UserService`). `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` discovers contributors by reflection — nothing to register in a list.
- **Architecture tests** — `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` (the DI wiring; the orchestrator's own tests are in `tests/Humans.Gdpr.Tests/`).

### Account deletion cascade

Account deletion cascades (user-requested / admin-initiated / expiry-triggered) are orchestrated by `IAccountDeletionService` (`src/Sections/Humans.Users/Services/AccountDeletionService.cs`). The interface is declared on the `Humans.Users.Contracts` leaf, not the section's `Contracts/` folder, so Gdpr's deletion screens can inject it; it carries no `IOrchestrator` marker there; `AccountDeletionService` carries it instead. `ProfileService` owns only profile-picture storage (`IProfilePictureService`); profile-data anonymization stays inside `UserService.EraseForUserAsync` through its internal `AnonymizeProfileForDeletionAsync` helper; the deletion orchestrator refreshes the user slice after the GDPR fan-out. User-initiated deletion request and cancel both live on `IAccountDeletionService` (`RequestDeletionAsync`, `CancelDeletionAsync`). Gdpr's `MemberDataController.RequestDeletion` (signed-in users with profiles), `GuestDataController.RequestDeletion` / `CancelDeletion` (profileless users) and Gdpr's `UserController.CancelDeletion` call the orchestrator directly — no manual `User.DeletionRequestedAt`/`DeletionScheduledFor`/`DeletionEligibleAfter` writes anywhere else. The orchestrator computes the optional `DeletionEligibleAfter` (post-event hold for current-event ticket holders) inline via `ITicketService.GetUserTicketHoldingsAsync` so every entry point gets the same treatment.

Deletion requests invalidate the user's shift authorization and view caches after the write attempt. Membership/role cleanup failures still propagate; cache cleanup never reports an incomplete cascade as successful. Confirmation delivery is best-effort after commit. Missing or whitespace-only target/fallback addresses skip confirmation with a Warning without changing the committed deletion request.

### Touch-and-clean guidance

- Cross-section reads for `Profile.User` / `UserEmail.User` must go through `IUserService.GetUserInfosAsync` — do not re-add nav properties to the entities.
- The token "OAuth" is banned from `IUserEmailService` / `IUserRepository` user-email method, parameter, and property names — provider operations are parameterized (`LinkAsync(provider, providerKey, …)`) so new providers add data, not methods. This is documentation, not a pinned test — a test asserting a method-name token is absent is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md). The single allowed exception is `ReconcileOAuthIdentityAsync`, where "OAuth" is categorical (the OAuth-callback write channel, distinct from user-driven email management).

## Issue queue

`Section` also declares this queue’s Area-label resource key and page-path aliases; Issues discovers both with the queue.

Users owns the `Profiles` issue queue: it implements `IIssueQueueOwner` (Issues' `Contracts/`
folder) on its `Section` entry point, declaring the queue key and the roles that handle
issues filed against it — `UsersAdmin`, plus `Admin`, which handles every queue. Issues
discovers the declaration through DI and holds no list of sections; dropping the seam
sends this section's stored issues to the Admin-only queue. The key is `Profiles`, not
`Users`: Profiles merged into this section at nobodies-collective/Humans#866 and stored
rows still carry the old string, so the queue keeps its name (and `/Debug/Sections` lists
it as an unmatched annotation, which is the rename showing rather than hiding).

GDPR profile erasure deletes the resolved legacy or versioned picture file before clearing its metadata. File-deletion failures propagate and retain that metadata for retry; they cannot silently finish the cascade with an orphaned picture.

Deletion confirmations use the no-persistence email path. Delivery failures are terminal and logged by exception type only, without the erased address/name/subject or transport exception contents; they do not turn a completed erasure into a failed deletion.

- Verification links refuse a missing user, missing/already-verified/OAuth email row or invalid token through `VerifyEmailResult.ErrorKey`. All member-facing refusals use the existing localized invalid-link message; owner warnings identify only user/email row ids and a fixed reason, without token or address. Dependency failures propagate instead of being classified as invalid links or exposed as validation text. Row-bound token purposes, conflict merge requests and email update/provisioning behavior remain.

After email insertion, verification, or removal commits, required primary and Google flag repairs finish independently of browser cancellation; initial reads and the first write remain cancellable.

Email insertion and OAuth reconciliation refuse deleted or merged accounts, including legacy tombstones, before changing email rows. OAuth linking likewise refuses terminal targets before adding or moving provider credentials; a merged source may still transfer its existing login to a live survivor. Admin recovery bypasses verification cooldowns only for live accounts.
