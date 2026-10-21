# Auth — Data Access

## Auth

Folder: `src/Sections/Humans.Auth/Services/`, including `MagicLinkService`
and its two collaborators. **DbContext:** `AuthDbContext`, internal to the
section. `RoleAssignmentRepository` injects `IDbContextFactory<AuthDbContext>`
directly. Owns `RoleAssignments`.

`IRoleAssignmentService`, `IRoleAssignmentWrite` and the internal
`IRoleAssignmentManagement` all alias the same singleton.
The inner `IRoleAssignmentManagement` is wrapped by
`Humans.Auth.Services.CachingRoleAssignmentService`
(Singleton decorator inheriting `TrackedCache<Guid, RoleAssignmentRow>`).
The full `role_assignments` row set is held in memory so
cross-section reads (`GetActiveCountsByRoleAsync`, `GetActiveForUserAsync`)
derive at any clock instant without a query. Invalidation is service-level:
the inner service clears the decorator's cache (`CachingRoleAssignmentService.Clear()`,
the concrete owner, no invalidator interface) right after each of its repository writes,
merge fold and erasure included (single writer, so no EF interceptor needed).

### RoleAssignmentsController / RoleAssignmentsCardViewComponent

No repository, no table. Both read `IRoleAssignmentService` and write only through
`IRoleAssignmentWrite` (whose service writes the audit entries); display names come
from `IUserService`.

### RoleAssignmentService (Scoped — wrapped by CachingRoleAssignmentService Singleton decorator)

Repository: `IRoleAssignmentRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R/W |

| Cache | Invalidate |
|-------|------------|
| `Auth.RoleAssignmentRow` TrackedCache (`CachingRoleAssignmentService`, injected concretely) | yes (wholesale `Clear()` right after each repository write, before the Board team sync reads roles back) |

Cross-section calls via `IUserService`, `ISystemTeamSync`,
`IAuditLogService`, `INotificationEmitter`. Implements `IUserDataContributor` for GDPR exports
and `IUserMerge` for account merges.

### CachingRoleAssignmentService (Singleton, `Humans.Auth.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, RoleAssignmentRow>` (`Auth.RoleAssignmentRow`, in-process, no `IMemoryCache`) | Per-Entity (warmed on startup) | yes | yes | yes (wholesale `Clear()`, called by the inner `RoleAssignmentService`) |

Implements `IRoleAssignmentManagement` (hence `IRoleAssignmentService`, `IRoleAssignmentWrite`).
Resolves the keyed Scoped inner per-call via `IServiceScopeFactory`.
Surfaced on `/Debug/CacheStats`.

### MagicLinkService (Scoped)

No repository. Uses ASP.NET `UserManager<User>` plus `IUserEmailService`,
`IUserService`, `IEmailService`, the section's own `AuthEmails` builder,
`IMagicLinkRateLimiter`, `IMagicLinkUrlBuilder`. No direct `IMemoryCache` —
rate-limit/replay sentinels are owned by `IMagicLinkRateLimiter`
(same section, `Services/`) which writes `magic_link_used:{sha256(token)}`,
`magic_link_login:{userId}` and `magic_link_signup:{normalizedEmail}` into `IMemoryCache`. Both link types
redeem through the same `magic_link_used:` sentinel — login tokens on
`VerifyLoginTokenAsync` (returns `bool`, never the `User`), signup tokens on `VerifyAndConsumeSignupTokenAsync`, which
`ReleaseSignupToken` reverses when provisioning fails —
and their strings come from different DataProtection purposes, so they cannot
collide. Survey and unsubscribe tokens have their own providers and never
reach this limiter.

### AdminAuthorizationService (Scoped)

Repository: `IRoleAssignmentRepository`.

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
`IRoleAssignmentManagement.GetActiveCountsByRoleAsync` per scope to feed the
`humans.role_assignments_active` gauge. No cache.

### AuthEmails (Scoped, internal)

No repository. Pure builder — reads `AuthResource` (via
`IStringLocalizer<AuthResource>`), writes nothing. Returns `EmailMessage` values for `MagicLinkService` to pass to
`IEmailService.SendAsync`. Both templates keep their
`TimeSensitiveTemplates` names so the outbox still drains a sign-in link
immediately. No DB access, no cache.

### AuthEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`) —
builds one sample per template via `AuthEmails` for `/Email/EmailPreview`.
No DB access, no cache.

---
