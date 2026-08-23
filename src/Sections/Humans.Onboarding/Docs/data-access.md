# Onboarding — Data Access

## Onboarding

Folder: `src/Sections/Humans.Onboarding/Services/`. Orchestrator
section — owns no DB tables, holds no `IMemoryCache` injection.

### OnboardingService (Scoped)

No repository injected. Cross-section calls via `IUserService`,
`IApplicationServiceRead`, `IEmailService`, `IEmailMessageFactory`,
`INotificationEmitter`, `ISystemTeamSync`, `IMembershipCalculatorRead`,
`IConsentServiceRead`, `IHumanLifecycleService`, `IAuditLogService`. No
`IMemoryCache`, no `IClock`. State changes flow through the owning
services so cache invalidation happens at the boundary they each own.

### OnboardingWidgetState (Scoped)

Not a DTO — this is where the widget's step-resolution algorithm lives
(`GetCurrentStepAsync`), and it is the section's most-branched method. No
repository: it answers from `IUserServiceRead`, `IShiftView`,
`IMembershipCalculatorRead`, `IBurnSettingsService`, `IConsentServiceRead`
and the session seam `IOnboardingWidgetSessionState`. The step is derived
on every call rather than stored, so there is nothing to invalidate.

### HttpOnboardingWidgetSessionState (Scoped)

The one piece of onboarding state that is not derived: the "skip shifts"
flag, held in `HttpContext.Session` behind an interface so the state
service stays testable without a request. Session-backed, not DB-backed —
skipping does not survive a new session, by design.

---

> The two blocks below document services that live in
> `src/Sections/Humans.Users/Services/` and are Users' to own. They are
> recorded here only because Onboarding is the caller and Users'
> `data-access.md` does not carry them yet; moving them is queued for
> Users' own run.

## Human Lifecycle

Folder: `src/Sections/Humans.Users/Services/`. Orchestrator —
owns no DB tables. Pairs with `OnboardingService`; the two together
handle suspend/unsuspend/restore state transitions.

### HumanLifecycleService (Scoped)

No repository. Fans out over `IUserService`, `INotificationEmitter`,
`INotificationAutoResolve`, `IAuditLogService`, `IHumansMetrics`. No
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
`IAuditLogService`, `IHumansMetrics`, plus `IActiveTeamsCacheInvalidator` /
`IRoleAssignmentClaimsCacheInvalidator` / `IShiftAuthorizationInvalidator`
for cache eviction. `[CrossSectionWrite]`-marked (suspension removes the
user from their team's Google resources). No direct DB access, no
`IMemoryCache`.
