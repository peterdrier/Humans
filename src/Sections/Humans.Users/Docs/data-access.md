# Users — Data Access

## Profiles

Folder: `src/Sections/Humans.Users/Services/` — no separate `Profiles/`
subfolder; `ProfileEditorService`, `ProfileService`, and
`ContactFieldService` sit alongside the Users services listed under
[Users](#users) below. Owns `Profiles`, `ContactFields`,
`ProfileLanguages`, `VolunteerHistoryEntries`, `UserEmails`,
`CommunicationPreferences`. **DbContext:** `UsersDbContext` — the merged
Users+Profiles context.

The account-merge surface (`AccountMergeService`, `DuplicateAccountService`,
`AccountMergeRepository`) lives in the Users section — see
[Users](#users) below; the `AccountMergeRequests` table is owned there, not
by Profiles. `DuplicateAccountService` is detection-only (no repository, no
DB access).

The Profiles section's per-user persistence is consolidated into a single
`IUserRepository`, which owns `Users`, `Profiles`, `UserEmails`,
`ContactFields`, `ProfileLanguages`, `VolunteerHistoryEntries`,
`EventParticipations`, and the ASP.NET-Identity `IdentityUserLogins`
bridge. `IProfileService` is retired (now `IProfilePictureService` only)
and the unified User+Profile read-model lives behind
`IUserService.GetUserInfoAsync`. The Profiles repositories remain
**Singletons** (`IDbContextFactory` pattern).

### ProfileService (Scoped — `IProfilePictureService`)

Repository: `IUserRepository` (read-only profile methods - fetches profile row to resolve
storage paths).

| Table | R/W |
|-------|-----|
| Profiles | R (storage-path lookup) |

Cross-section calls via `IUserService` (delegates the content-type DB write).
Uses `IFileStorage` for the picture bytes. No `IMemoryCache`. The picture
content type is written through `IUserService.SetProfilePictureContentTypeAsync`
so the unified `UserInfo` read-model invalidates as a side effect.

### ProfileEditorService (Scoped)

No repository. Per-user serialization wrapper that fans out to
`IUserService.SaveProfileAsync` for the row writes and `IFileStorage` for
the picture file. No `IMemoryCache`.

### ContactFieldService (Scoped)

Repository: `IUserRepository` (profile and contact-field methods).

| Table | R/W |
|-------|-----|
| ContactFields | R/W |
| Profiles | R |

Cross-section reads via `IUserServiceRead`, `ITeamServiceRead`,
`IRoleAssignmentService` (visibility / coordinator-team lookups). Implements
`IUserMerge`. Invalidates the User+Profile read-model via
`IUserInfoInvalidator`. No `IMemoryCache`.

### CommunicationPreferenceService (Scoped)

Repository: `ICommunicationPreferenceRepository`.

| Table | R/W |
|-------|-----|
| CommunicationPreferences | R/W |

Cross-section calls via `IUserServiceRead`, `IAuditLogService`,
`IUnsubscribeTokenProvider` (same section, `Services/`). Implements
`IUserMerge`. No cache.

### UserEmailService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| UserEmails | R/W |
| Users | R/W (the only direct EF write to `Users.GoogleEmail` / `Users.Email`; also a read for `UserEmailWithUser` lookups). Google sync status is per-address on `UserEmails.GoogleEmailStatus` — `Users.GoogleEmailStatus` is deprecated/unwritten. |

Cross-section calls via `IUserService`, plus ASP.NET `UserManager<User>` and
`IServiceProvider` for lazy resolution. Implements `IUserMerge`. No
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
`CachingUserService` (the person-search entry point on `IUserServiceRead`)
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
`EmailProblemsReport` for the admin email-health screen,
`IsGhostExternalLoginsUserAsync` flags accounts whose only identity is an
external login, and `BackfillLegacyIdentityEmailsAsync` returns the
`(UserId, Email)` pairs still missing a `UserEmail` row. Timestamps come from
`IClock`.

---


## Users

Folder: `src/Sections/Humans.Users/Services/`; repository under
`Data/Repositories/`. **DbContext:** `UsersDbContext` — the merged
Users+Profiles context (`Users` is the Identity-backed `users` table via
`IdentityDbContext<User, …>`, which the context carries directly). Owns
`Users`, `UserEmails` cross-bridge (read-through), `EventParticipations`,
ASP.NET `IdentityUserLogins`, and `AccountMergeRequests` — the
account-merge surface (`AccountMergeService`, `DuplicateAccountService`,
`AccountMergeRepository`) lives here. The inner `IUserService` registration is wrapped
by `Humans.Users.Data.CachingUserService` (Singleton
decorator inheriting `TrackedCache<Guid, UserInfo>`, in the section's
own `Data/` folder) which holds the
canonical `UserInfo` read-model spanning User + Profile sections. The
decorator exposes the budgeted cross-section read surface as
`IUserServiceRead`. Following the User+Profile section merge,
`IUserService` absorbed the legacy `IProfileService` surface; the
interface's `[SurfaceBudget]` remains intentionally suspended.

### UserService (Scoped — wrapped by CachingUserService Singleton decorator)

Repositories: `IUserRepository`, `ICommunicationPreferenceRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| Users | R/W | IUserRepository |
| UserEmails | R | IUserRepository |
| EventParticipations | R/W | IUserRepository |
| IdentityUserLogins | R | IUserRepository |
| Profiles | R/W | IUserRepository |
| ContactFields | R | IUserRepository |
| CommunicationPreferences | R | ICommunicationPreferenceRepository |
| ProfileLanguages | R/W | IUserRepository |
| VolunteerHistoryEntries | R | IUserRepository |

The consolidated `IUserRepository` plus the comms-prefs repo
together compose the `UserInfo` projection inside `CachingUserService` —
a single cached read-model fanning out over the User + Profile section's
unified persistence layer. Implements `IUserDataContributor`,
`IUserMerge`.

Cross-section calls via `IAdminAuthorizationService`. No direct
`IMemoryCache` — caching is in the Singleton decorator.

### CachingUserService (Singleton, `Humans.Users.Data`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserInfo>` (`User.UserInfo`, in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (`IUserInfoInvalidator` — fired by UserService/ProfileService writes, by `IUserMerge` participants, and by `UserInfoSaveChangesInterceptor` for Identity-machinery writes) |

Implements `IUserService`, `IUserServiceRead`, `IUserMerge`,
`IUserInfoInvalidator`, and the Infrastructure-internal
`IUserInfoSliceRefresher` (consumed by `UserInfoSaveChangesInterceptor` to
catch OAuth/`UpdateAsync`/`LastLoginAt` writes that bypass the service
surface). Surfaced on `/Debug/CacheStats`.

`UserEmailService` (`GetUserIdByExactEmailAsync`,
`GetDistinctVerifiedUserIdsAsync`, `GetUserIdByVerifiedEmailAsync`) and
`UserService` (`GetByEmailOrAlternateAsync`) match verified addresses in
memory against the cached `UserInfo` set instead of querying `UserEmails`
directly. `CachingUserService.GetByEmailOrAlternateAsync` overrides the
inner service to scan the warmed snapshot itself (no repeated
`GetAllUserInfosAsync` fan-out per miss); the inner
`UserService.GetByEmailOrAlternateAsync` is legacy-`GoogleEmail`-shadow-column-only,
reached only on a snapshot miss. Gmail/googlemail aliasing is preserved via
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

### AccountDeletionService (Scoped) — `Users/AccountLifecycle/`

No repository. GDPR right-to-deletion orchestrator. Fans out over
`IUserService`, `IUserEmailService`, `ITeamService`,
`IRoleAssignmentService`, `IShiftSignupService`,
`IShiftManagementService`, `ITicketServiceRead`, `IAuditLogService`,
`IEmailService`. Invalidates
`IRoleAssignmentClaimsCacheInvalidator`,
`IShiftAuthorizationInvalidator`, `IShiftViewInvalidator`. Uses
`IFileStorage` for blob cleanup. No cache, no direct DB access — all
writes go through owning services.

### UnsubscribeService (Scoped)

Repositories: `IUserRepository` (read-only — token validation / user guard).

| Table | R/W |
|-------|-----|
| Users | R (existence check via `IUserRepository.GetByIdAsync`) |

Calls `ICommunicationPreferenceService` to validate the token and flip
per-category opt-outs; uses `IDataProtectionProvider` for legacy token
validation. Also injects `IUserServiceRead` to resolve the user's
`BurnerName` for the unsubscribe-confirm display (reads from the
`CachingUserService` TrackedCache — no extra DB round-trip on hit).
No cache on the service itself.

### UserParticipationBackfillService (Scoped)

No repository. Fan-out over `IUserService` and `IShiftManagementService`
to backfill `EventParticipations`. No direct DB access, no cache.

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
`IUserDataContributor`. Cross-section calls via `IUserService`,
`IRoleAssignmentService`, `INotificationService`, `IAuditLogService`,
`IUserInfoInvalidator`, `IActiveTeamsCacheInvalidator`,
`IConsentCacheInvalidator`, plus the `IUserMerge` aggregator.

Cache: per-section `IUserMerge` implementations invalidate their own
caches; the unified read-model is evicted via `IUserInfoInvalidator`.

### DuplicateAccountService (Scoped)

No repository.

| Table | R/W |
|-------|-----|
| _(none — pure read orchestration over service interfaces)_ | — |

Detection-only: loads the cached `UserInfo` read-model via
`IUserService.GetAllUserInfosAsync` (~500 users, in-memory normalize for
gmail/googlemail equivalence), then counts active teams / role assignments
per involved user. Resolution is delegated to
`AccountMergeService.MergeAsync`. **No DB access.** Cross-section calls via
`IUserService`, `ITeamService`, `IRoleAssignmentService`. No cache.

### ExternalLoginService (Scoped)

No repository. OAuth-callback decision ladder (link-while-signed-in →
lockout-relink → verified-email link → create-new-account) per HUM0031.
Uses ASP.NET `UserManager<User>`
directly (framework concern, per design-rules §2a — `AspNetUserLogins` is
the authoritative store for `(Provider, ProviderKey)` → `UserId`) plus
`IUserService` (login-timestamp recording, stub-profile provisioning on
create), `IUserEmailService` (`ReconcileOAuthIdentityAsync` — the sole
caller, pinned by HUM0005), `IMagicLinkService`
(`FindUserByVerifiedEmailAsync`), and `IClock`. No direct DB access, no
`IMemoryCache`. Implements `IExternalLoginService`.

### UserNameSyncService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| Users | R |
| Profiles | R/W (re-persists the Profile row to run the User↔Profile dual-write sync) |

Operator-driven BurnerName/legal-name backfill: finds `User`/`Profile`
pairs where the User row is missing a name the Profile carries, then
re-saves the Profile through the normal dual-write path. Cross-section
calls via `IUserService` (`GetAllUserInfosAsync`, for email display only).
No `IMemoryCache`.

### UsersAudienceService (Scoped)

No repository. Pure read orchestration over `IUserService`
(`GetAllUserInfosAsync`) and Tickets' `ITicketServiceRead`
(`GetTicketOrdersAsync`) to segment accounts by profile-completion /
ticket-purchase status for the admin audience dashboard. No direct DB
access, no cache.

---


