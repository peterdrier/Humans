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
