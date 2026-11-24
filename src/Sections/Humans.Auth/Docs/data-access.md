# Auth — Data Access

## Auth

Folder: `src/Sections/Humans.Auth/Services/`, including `SignInLinkService`
and its two collaborators. **DbContext:** `AuthDbContext`, internal to the
section. `SystemRoleRepository` injects `IDbContextFactory<AuthDbContext>`
directly. Owns `RoleAssignments`.

`ISystemRoleService`, `ISystemRoleWrite` and the internal
`ISystemRoleManagement` all alias the same singleton.
The inner `ISystemRoleManagement` is wrapped by
`Humans.Auth.Services.CachingSystemRoleService`
(Singleton decorator inheriting `TrackedCache<Guid, SystemRoleRow>`).
The full `auth_role_assignments` row set is held in memory so
every active-role read (`GetActiveCountsByRoleAsync`, `GetActiveForUserAsync`,
`HasActiveRoleAsync` and its `IsUser*` shorthands, `HasAnyActiveAssignmentAsync`,
`GetActiveUserIdsInRoleAsync`, `GetUserIdsWithActiveAssignmentsAsync`) derives at any
clock instant without a query. Cached rows carry canonical role names. Invalidation is service-level:
the inner service clears the decorator's cache (`CachingSystemRoleService.Clear()`,
the concrete owner, no invalidator interface) right after each of its repository writes,
merge fold and erasure included (single writer, so no EF interceptor needed).

### SystemRolesController / SystemRolesCardViewComponent

No repository, no table. Both read `ISystemRoleService` and write only through
`ISystemRoleWrite` (whose service writes the audit entries); display names come
from `IUserService`.

### SystemRoleService (Scoped — wrapped by CachingSystemRoleService Singleton decorator)

Repository: `ISystemRoleRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R/W |

| Cache | Invalidate |
|-------|------------|
| `Auth.SystemRoleRow` TrackedCache (`CachingSystemRoleService`, injected concretely) | yes (wholesale `Clear()` right after each repository write) |

Cross-section calls via `IUserService`, `ISystemTeamSync`,
`IAuditLogService`, `INotificationEmitter`. Implements `IUserDataContributor` for GDPR exports
and `IUserMerge` for account merges.

### CachingSystemRoleService (Singleton, `Humans.Auth.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, SystemRoleRow>` (`Auth.SystemRoleRow`, in-process, no `IMemoryCache`) | Per-Entity (warmed on startup) | yes | yes | yes (wholesale `Clear()`, called by the inner `SystemRoleService`) |

Implements `ISystemRoleManagement` (hence `ISystemRoleService`, `ISystemRoleWrite`).
Resolves the keyed Scoped inner per-call via `IServiceScopeFactory`.
Surfaced on `/Debug/CacheStats`.

### SignInLinkService (Scoped)

No repository. Uses `IUserEmailService`,
`IUserService` (`GetRawUserInfoAsync` for token-bound identity checks, `GetUserInfoAsync` for display data),
`IEmailService`, the section's own `AuthEmails` builder,
`ISignInLinkRateLimiter`, `ISignInLinkUrlBuilder`. No direct `IMemoryCache` —
rate-limit/replay sentinels are owned by `ISignInLinkRateLimiter`
(same section, `Services/`) which writes `magic_link_used:{sha256(token)}`,
`magic_link_login:{userId}` and `magic_link_signup:{normalizedEmail}` into `IMemoryCache`. Both link types
redeem through the same `magic_link_used:` sentinel — login tokens on
`VerifySignInTokenAsync` (returns `bool`, never the `User`), signup tokens on `VerifyAndConsumeSignupTokenAsync`, which
`ReleaseSignupToken` reverses when provisioning fails —
and their strings come from different DataProtection purposes, so they cannot
collide. Survey and unsubscribe tokens have their own providers and never
reach this limiter.

### AdminAuthorizationService (Scoped)

Repository: `ISystemRoleRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R |

Read-only, and narrower than it sounds: the one method
(`RequireCurrentUserIsAdminAsync`) hardcodes `RoleNames.Admin` and throws
`UnauthorizedAccessException` for everyone else. It is a full-Admin guard in
front of destructive cross-section actions — it answers no Board or
Coordinator question. The caller comes from the Shell's `ICurrentUserContext`.
Cycle-safe (does not pull `IAuthorizationService`). No
cache (reads route through the inner repo; hot reads can migrate to the cached
row set incrementally).

### AuthMetricsService (Singleton, hosted)

No repository. Polled gauge service: reads
`ISystemRoleManagement.GetActiveCountsByRoleAsync` per scope to feed the
`humans.system_roles_active` gauge. No cache.

### AuthEmails (Scoped, internal)

No repository. Pure builder — reads `AuthResource` (via
`IStringLocalizer<AuthResource>`), writes nothing. Returns `EmailMessage` values for `SignInLinkService` to pass to
`IEmailService.SendAsync`. Both set `EmailMessage.Urgent` so the outbox
drains a sign-in link immediately. No DB access, no cache.

### AuthEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`) —
builds one sample per template via `AuthEmails` for `/Email/EmailPreview`.
No DB access, no cache.

---
