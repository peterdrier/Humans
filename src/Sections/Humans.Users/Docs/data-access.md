# Users — Data Access

## Profiles

Folder: `src/Sections/Humans.Users/Services/` — no separate `Profiles/`
subfolder; `ProfileEditorService`, `ProfileService`, and
`ContactFieldService` sit alongside the Users services listed under
[Users](#users) below. Owns `Profiles`, `ContactFields`,
`ProfileLanguages`, `VolunteerHistoryEntries`, `UserEmails`, and the
frozen legacy `CommunicationPreferences` (Email owns preferences since
Users decomposition commit 14). **DbContext:** `UsersDbContext` — the merged
Users+Profiles context.

The account-merge surface (`AccountMergeService`, `DuplicateAccountService`,
`AccountMergeRepository`) lives in the Users section — see
[Users](#users) below; the `AccountMergeRequests` table is owned there, not
by Profiles. `DuplicateAccountService` is detection-only (no repository, no
DB access; its one write is the `DuplicateAccountFlagged` audit entry through
`IAuditLogService`).

Per-user persistence is a single `IUserRepository`, which owns `Users`,
`Profiles`, `UserEmails`, `ContactFields`, `ProfileLanguages`,
`VolunteerHistoryEntries`, the frozen legacy `EventParticipations` (read-only; Tickets owns participation since Users decomposition commit 13), and the ASP.NET-Identity
`IdentityUserLogins` bridge. `ProfileService` exposes only
`IProfilePictureService`; the unified User+Profile read-model lives behind
`IUserService.GetUserInfoAsync`. Repositories are **Singletons**
(`IDbContextFactory` pattern).

### ProfileService (Scoped — `IProfilePictureService`)

Repository: `IUserRepository` (read-only profile methods - fetches profile row to resolve
storage paths).

| Table | R/W |
|-------|-----|
| Profiles | R (storage-path lookup) |

Uses `IUserManagement` for migration-snapshot user lookups and `IFileStorage`
to read picture bytes. No picture mutations or `IMemoryCache`; writes belong to
the profile-edit flow below.

### ProfileEditorService (Scoped)

No repository or cache. Serializes profile/CV submissions per user and delegates
fields, CV history and picture bytes to `IUserManagement.SaveProfileAsync`.
`UserService` writes picture bytes before publishing metadata, commits fields and
CV together with the legacy identity name, and cleans superseded files after the save.

### ContactFieldService (Scoped)

Repository: `IUserRepository` (profile and contact-field methods).

| Table | R/W |
|-------|-----|
| ContactFields | R/W |
| Profiles | R |

Cross-section reads via `IUserService`, `ITeamService`,
`IRoleAssignmentService` (visibility / coordinator-team lookups). Implements
`IUserMerge`. Refreshes the User+Profile read-model via the section-internal
`IUserInfoSliceRefresher`. No `IMemoryCache`.

### UserEmailService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| UserEmails | R/W |
| Users | R/W (the only direct EF write to `Users.Email`). The `Users.GoogleEmail` shadow column has no reader or writer left (nobodies-collective/Humans#1102); the mapping survives only until the drop PR. `UserEmails.IsGoogle` / `UserEmails.GoogleEmailStatus` and `Users.GoogleEmailStatus` are frozen and unwritten: the Google Workspace identity is GoogleIntegration's `google_workspace_identities` (Users decomposition commit 15). |

Cross-section calls via `IUserService`, plus ASP.NET `UserManager<User>` and
`IAuditLogService` (manual-verify and primary-cleared entries) and `IServiceProvider` for lazy resolution (Tickets' `ITicketService`, Users' `IAccountMergeService`, and GoogleIntegration's `IGoogleIntegrationWrite.ClearIfAddressAsync` after each committed delete, unlink or OAuth tag-move/rename/displacement). Implements `IUserMerge`. No
`IMemoryCache` directly.
**Cross-section design-rule note:** `Users` reads/writes overlap the User
section — `IUserRepository` is the consolidated owner and this is the
audited bridge for Google email status updates, tracked under HUM0025
grandfathering as the read-model boundary.

`AccountMergeService` / `DuplicateAccountService` live in the Users
section — see [Users](#users) below.

### AdminHumanListAssembler / PersonSearchFields / PersonSearchMatcher

Read-only DTO assemblers — no repository, no cache. Fan out over
`IUserService`, `IUserEmailService`, `IRoleAssignmentService`,
`ITeamService`.

`PersonSearchMatcher` is a pure static matcher (no DI, no repository, no
cache) over the cached `UserInfo` read-model. It is consumed by
`CachingUserService` (the person-search entry point on `IUserService`)
so the search runs entirely in-memory against the unified User+Profile
projection — no extra DB read. Each match carries a relevance
`Score` (exact name 100 / name-prefix 85 / token-prefix 80 / contains 60 /
non-name field 40, accent-folded) so every people-search surface ranks by
match quality instead of alphabetically. `PersonSearchFields` is the
accompanying scope-flag enum that doubles as the field-level authorization
model.

### EmailProblemsService (Scoped)

No repository, no cache, no direct DB access. Section-internal diagnostics over
`IUserEmailService` and `IUserService`: `ScanAsync` builds the
`EmailProblemsReport` for the admin email-health screen, including accounts
whose only identity is an external login. `BackfillLegacyIdentityEmailsAsync`
(the `users.legacy-identity-emails` data migration) creates the missing verified
`UserEmail` rows, audits `LegacyIdentityEmailBackfilled` per row against the acting
admin via `IAuditLogService`, and returns the `(UserId, Email)` pairs it wrote.
Timestamps come from `IClock`.

## Human Lifecycle

Folder: `src/Sections/Humans.Users/Services/`. Orchestrator — owns no DB
tables. Pairs with Onboarding's service to handle suspend, unsuspend, and
restore state transitions.

### HumanLifecycleService (Scoped)

No repository. Fans out over `IUserService`, `INotificationEmitter`,
`INotificationAutoResolve`, `IAuditLogService`, `IHumansMetrics`. No direct
DB access or cache. All `Profile.State` writes go through `IUserService`, the
unified user/profile write surface which invalidates its read model downstream.

### NonCompliantMemberSuspension (Scoped)

No repository. Implements `SuspendNonCompliantMembersJob`'s body
(`Humans.Users/Jobs/SuspendNonCompliantMembersJob.cs`). Suspends members who
have not re-consented after the grace period and runs each suspension's
downstream side effects. Cross-section calls via `IUserService`,
`IMembershipCalculator`, `IEmailService`, the section's own `UsersEmails` builder, `INotificationEmitter`,
`IAuditLogService` and `IHumansMetrics`. Google access ends at the next Google reconciliation,
which excludes suspended humans. No direct DB access or `IMemoryCache`.

### UsersEmails (Scoped, internal)

No repository. Pure builder — reads `UsersResource` (via
`IStringLocalizer<UsersResource>`) and `EmailSettings`, writes nothing.
Returns `EmailMessage` values for `ProfileEmailsController`,
`AccountDeletionService`, `NonCompliantMemberSuspension` and
`ProcessAccountDeletionsJob` to pass to `IEmailService.SendAsync`. No DB
access, no cache.

### UsersEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`,
`Section.cs:113`) — builds one sample per template via `UsersEmails` for
`/Email/EmailPreview`. No DB access, no cache.

---


## Users

Folder: `src/Sections/Humans.Users/Services/`; repository under
`Data/Repositories/`. **DbContext:** `UsersDbContext` — the merged
Users+Profiles context (`Users` is the Identity-backed `users` table via
`IdentityDbContext<User, …>`, which the context carries directly). Owns
`Users`, `UserEmails` cross-bridge (read-through), the frozen legacy `EventParticipations` (read-only),
ASP.NET `IdentityUserLogins`, and `AccountMergeRequests` — the
account-merge surface (`AccountMergeService`, `DuplicateAccountService`,
`AccountMergeRepository`) lives here. The inner `IUserService` registration is wrapped
by `Humans.Users.Data.CachingUserService` (Singleton
decorator inheriting `TrackedCache<Guid, UserInfo>`, in the section's
own `Data/` folder) which holds the
canonical `UserInfo` read-model spanning User + Profile sections. The
decorator exposes the budgeted cross-section read surface as
`IUserService`. `IUserWrite` carries the writes other sections call;
the section-only half of the write funnel (profile saves, deletion fields,
`UserEmail` storage commands, merge tombstone) is the internal
`IUserManagement : IUserService, IUserWrite, IUserMerge`, registered as the same singleton.
`IUserService`'s `[SurfaceBudget]` is intentionally suspended.

### UserService (Scoped — wrapped by CachingUserService Singleton decorator)

Repositories: `IUserRepository`, `ICommunicationPreferenceRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| Users | R/W | IUserRepository |
| UserEmails | R | IUserRepository |
| EventParticipations | R (frozen legacy rows: `UserInfo.EventParticipations` for Tickets' fallback, and the `LegacyEventParticipations` export slice) | IUserRepository |
| IdentityUserLogins | R | IUserRepository |
| Profiles | R/W | IUserRepository |
| ContactFields | R | IUserRepository |
| CommunicationPreferences | R/D (frozen legacy rows: `UserInfo.CommunicationPreferences` for Email's fallback, the `LegacyCommunicationPreferences` export slice, and erasure) | ICommunicationPreferenceRepository |
| ProfileLanguages | R/W | IUserRepository |
| VolunteerHistoryEntries | R | IUserRepository |

The consolidated `IUserRepository` plus the comms-prefs repo
together compose the `UserInfo` projection inside `CachingUserService` —
a single cached read-model fanning out over the User + Profile section's
unified persistence layer. Implements `IUserDataContributor`,
`IUserMerge`.

Cross-section calls via `IAdminAuthorizationService` and `IAuditLogService` (admin stub-profile creation after save). No direct
`IMemoryCache` — caching is in the Singleton decorator.

### CachingUserService (Singleton, `Humans.Users.Data`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserInfo>` (`User.UserInfo`, in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (decorator write pass-through; the section-internal `IUserInfoSliceRefresher` for `UserInfoSaveChangesInterceptor` (Identity-machinery writes) and for Users' own services that write past the decorator. Every contributing table is Users-owned, so no other section signals it) |

Implements `IUserManagement` (hence `IUserService`, `IUserWrite`, `IUserMerge`)
and the section-internal
`IUserInfoSliceRefresher` (consumed by `UserInfoSaveChangesInterceptor` to
catch OAuth/`UpdateAsync`/`LastLoginAt` writes that bypass the service
surface, and by Users' own services that write past the decorator). Surfaced on `/Debug/CacheStats`.

The decorator also owns the **merge index**: a `Dictionary<Guid, Guid[]>` built over
the warmed snapshot mapping each row to every id whose `MergedToUserId` chain reaches
it, transitively. It is rebuilt lazily on first use after any dict mutation
(`TrackedCache.OnMutated`), never persisted, and reads the snapshot through
`AsReadOnlyDictionary` so a rebuild leaves the hit/miss counters alone. The
rebuild and the drop share one lock, so a mutation landing mid-rebuild cannot
be overwritten by the stale index that rebuild was producing. Every read
stamps its rows' `MergedUserIds` from it, and the cross-section reads resolve a
tombstone id forward to the surviving row before stamping; `GetRawUserInfoAsync` /
`GetAllRawUserInfosAsync` stamp without resolving.

`UserEmailService.FindByAddressAsync` (the one address → rows lookup; callers
pick aliasing, verification and cardinality) is the general-purpose lookup.
`CachingUserService.GetByEmailOrAlternateAsync` scans the warmed `UserInfo`
snapshot for a verified match first (no repeated `GetAllUserInfosAsync`
fan-out per miss); on a miss it falls through to the inner
`UserService.GetByEmailOrAlternateAsync`, which runs one targeted verified
`UserEmails` query (`IUserRepository.GetUserEmailsByAddressAsync`) — canonical,
not a legacy fallback. Gmail/googlemail aliasing is preserved via
`EmailNormalization.EmailsMatch` on the alias-aware methods; exact-match
methods keep their no-aliasing contract.

### AccountProvisioningService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| Users | R/W |

Idempotent provisioning for import jobs. Uses ASP.NET `UserManager<User>`
for password / identity primitives. Cross-section calls via
`IUserEmailService`, `IUserService`, `IAuditLogService`. No cache.
Results carry the user id, never the `User` entity: `AccountProvisioningResult(Guid UserId, bool Created)`
and `MagicLinkSignupCompletionResult(Outcome, Guid? UserId)`.

New import account creation wraps User, provisioned UserEmail and Profile in one transaction, audits after local commit, then refreshes UserInfo after commit/rollback. Unverified replacements retain their existing outer transaction and refresh.

Import and magic-link retry matches run the idempotent profile ensure before returning the existing account; successful repairs emit `StubProfileBackfilled` without duplicating accounts or overwriting an existing Profile.

Unverified import replacements run deletion and provisioning in one serializable ambient transaction, through existing owned services/repositories. `IUserInfoSliceRefresher` refreshes all affected users after commit or rollback.

### AccountDeletionService (Scoped)

No repository. GDPR right-to-deletion orchestrator. Fans out over
`IUserManagement`, `IUserEmailService`, `ITeamWrite`,
`IRoleAssignmentWrite`, `IGdprService` (runs every section's
`IUserDataContributor` erasure), `ITicketService`, `IAuditLogService`,
`IEmailService` (messages built by `UsersEmails`). Refreshes `UserInfo` via
`IUserInfoSliceRefresher`. No cache, no
direct DB access — all writes go through owning services.

### AccountMergeService (Scoped)

Repositories: `IAccountMergeRepository`, `IUserRepository`.

| Table | R/W |
|-------|-----|
| AccountMergeRequests | R/W (via `IAccountMergeRepository`) |
| UserEmails | R/W (via `IUserRepository` — pending-email verify / remove during merge) |

The merge fan-out happens through the `IEnumerable<IUserMerge>` aggregator —
each section's service implements `IUserMerge` and handles its own
owned-table reassignment, so `AccountMergeService` does not inject other
sections' repositories. Both tables it touches are **owned by the Users
section** (no cross-section table access). Implements `IAccountMergeService`,
`IUserDataContributor`. Calls the section's own `IUserManagement`; cross-section
calls via `IRoleAssignmentWrite`, `ITeamWrite`, `INotificationService`, `IAuditLogService`,
`IConsentCacheInvalidator`, plus the `IUserMerge` aggregator.

Cache: per-section `IUserMerge` implementations invalidate their own
caches; the unified read-model is refreshed via the section-internal
`IUserInfoSliceRefresher`.

### DuplicateAccountService (Scoped)

No repository.

| Table | R/W |
|-------|-----|
| _(none — pure read orchestration over service interfaces)_ | — |

Detection-only: loads the cached `UserInfo` read-model via
`IUserService.GetAllUserInfosAsync` (full user base in memory, normalize for
gmail/googlemail equivalence), then counts active teams / role assignments
per involved user, with each user's membership tier and consent review. Resolution is delegated to
`AccountMergeService.MergeAsync`. **No DB access.** Cross-section calls via
`IUserService`, `ITeamService`, `IRoleAssignmentService`, `IGovernanceService`,
`IConsentService`, `IAuditLogService`. No cache.

### ExternalLoginService (Scoped)

No repository. OAuth-callback decision ladder (link-while-signed-in →
lockout-relink → verified-email link → create-new-account) per HUM0031.
Uses ASP.NET `UserManager<User>`
directly (framework concern, per design-rules §2a — `AspNetUserLogins` is
the authoritative store for `(Provider, ProviderKey)` → `UserId`) plus
`IUserService` (login-timestamp recording, stub-profile provisioning on
create), `IUserEmailService` (`ReconcileOAuthIdentityAsync` — the sole
caller, pinned by HUM0005; `FindByAddressAsync` for the verified-email
owner), and `IClock`. No direct DB access, no
`IMemoryCache`. Implements `IExternalLoginService`.

### UserNameSyncService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| Users | R |
| Profiles | R/W (re-persists the Profile row to run the User↔Profile dual-write sync) |

Operator-driven BurnerName/legal-name backfill (the `users.names-onto-user` data
migration on `/Debug/DataMigrations`): finds `User`/`Profile`
pairs where the User row is missing a name the Profile carries, then
re-saves the Profile through the normal dual-write path. Cross-section
calls via `IUserService` (`GetAllUserInfosAsync`, for email display only) and `IAuditLogService` (actor-attributed `UserNamesBackfilled` per saved member).
No `IMemoryCache`.

### UsersAudienceService (Scoped)

No repository. Pure read orchestration over `IUserService`
(`GetAllUserInfosAsync`) and Tickets' `ITicketService`
(`GetTicketOrdersAsync`) to segment accounts by profile-completion /
ticket-purchase status for the admin audience dashboard. No direct DB
access, no cache.

---

