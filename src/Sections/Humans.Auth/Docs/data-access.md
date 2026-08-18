# Auth — Data Access

## Auth

Folder: `src/Sections/Humans.Auth/Services/`, including `MagicLinkService`
and its two collaborators. **DbContext:** `AuthDbContext`, internal to the
section. `RoleAssignmentRepository` injects `IDbContextFactory<AuthDbContext>`
directly. Owns `RoleAssignments`.

The inner `IRoleAssignmentService` is wrapped by
`Humans.Auth.Services.CachingRoleAssignmentService`
(Singleton decorator inheriting `TrackedCache<Guid, RoleAssignmentRow>`).
The full `role_assignments` row set is held in memory so
cross-section reads (`GetActiveCountsByRoleAsync`, `GetActiveForUserAsync`)
derive at any clock instant without a query. Invalidation is service-level:
the inner service's writes call `IRoleAssignmentCacheInvalidator.InvalidateAll()`
directly (single writer, so no EF interceptor needed).

### RoleAssignmentService (Scoped — wrapped by CachingRoleAssignmentService Singleton decorator)

Repository: `IRoleAssignmentRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R/W |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `Auth.RoleAssignmentRow` TrackedCache (`IRoleAssignmentCacheInvalidator`) | yes (wholesale flush on write) |
| `FeedbackBadgeCount` (`INavBadgeCacheInvalidator`) | yes |
| `claims:{userId}` (`IRoleAssignmentClaimsCacheInvalidator`) | yes |

Cross-section calls via `IUserService`, `ISystemTeamSync`,
`IAuditLogService`. Implements `IUserDataContributor` for GDPR exports
and `IUserMerge` for account merges.

### CachingRoleAssignmentService (Singleton, `Humans.Auth.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, RoleAssignmentRow>` (`Auth.RoleAssignmentRow`, in-process, no `IMemoryCache`) | Per-Entity (warmed on startup) | yes | yes | yes (wholesale, via `IRoleAssignmentCacheInvalidator.InvalidateAll`) |

Implements `IRoleAssignmentService`, `IRoleAssignmentCacheInvalidator`.
Resolves the keyed Scoped inner per-call via `IServiceScopeFactory`.
Surfaced on `/Debug/CacheStats`.

### MagicLinkService (Scoped)

No repository. Uses ASP.NET `UserManager<User>` plus `IUserEmailService`,
`IEmailService`, `IMagicLinkRateLimiter`, `IMagicLinkUrlBuilder`,
`IUnsubscribeTokenProvider`. No direct `IMemoryCache` —
rate-limit/replay sentinels are owned by `IMagicLinkRateLimiter`
(same section, `Services/`) which writes `magic_link_used:{tokenPrefix}` and
`magic_link_signup:{normalizedEmail}` into `IMemoryCache`.

### AdminAuthorizationService (Scoped)

Repository: `IRoleAssignmentRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R |

Read-only — answers "is this user a board member / coordinator / admin"
for cross-section authorization checks. Cycle-safe (does not pull
`IAuthorizationService`). No cache (reads route through the inner repo;
hot reads can migrate to the cached row set incrementally).

---


