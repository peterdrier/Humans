# Onboarding — Data Access

## Onboarding

Folder: `src/Sections/Humans.Onboarding/Services/`. Orchestrator
section — owns no DB tables, holds no `IMemoryCache` injection.

### OnboardingService (Scoped)

No repository injected. Cross-section calls via `IUserService`,
`IApplicationServiceRead`, `IEmailService`, `IEmailMessageFactory`,
`INotificationEmitter`, `ISystemTeamSync`, `IMembershipCalculatorRead`,
`IAuditLogService`, `IHumansMetrics`. No `IMemoryCache`. State changes
flow through the owning services so cache invalidation happens at the
boundary they each own.

`OnboardingWidgetState` is a value DTO with no behavior.

---


## Human Lifecycle

Folder: `src/Sections/Humans.Users/Services/`. Orchestrator —
owns no DB tables. Pairs with `OnboardingService`; the two together
handle suspend/unsuspend/restore state transitions.

### HumanLifecycleService (Scoped)

No repository. Fans out over `IUserService`, `INotificationService`,
`INotificationInboxService`, `IAuditLogService`, `IHumansMetrics`. No
direct DB access, no cache. All `Profile.State` writes go through
`IUserService` (the unified user/profile write surface) which invalidates
the unified User+Profile read-model downstream.

### NonCompliantMemberSuspension (Scoped)

No repository. Implements `SuspendNonCompliantMembersJob`'s body
(`Humans.Users/Jobs/SuspendNonCompliantMembersJob.cs`). Suspends members
who haven't re-consented after the grace period and runs each suspension's
downstream side effects. Cross-section calls via `IUserService`,
`ITeamServiceRead`, `IMembershipCalculatorRead`, `IGoogleSyncService`,
`IEmailService`, `IEmailMessageFactory`, `INotificationEmitter`,
`IAuditLogService`, plus `IActiveTeamsCacheInvalidator` /
`IRoleAssignmentClaimsCacheInvalidator` / `IShiftAuthorizationInvalidator`
for cache eviction. `[CrossSectionWrite]`-marked (suspension removes the
user from their team's Google resources). No direct DB access, no
`IMemoryCache`.

---


