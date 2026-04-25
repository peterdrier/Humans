# Service Dependency Graph

Directed graph of service-to-service dependencies, reflecting the post-§15 Part 1 migration state (assumes Wave 3 of the 2026-04-23 cleanup plan is complete — see `docs/architecture/tech-debt-2026-04-23.md`).

## How to read

- Solid arrow (`-->`) = ctor-injected dependency, eagerly resolved.
- Dashed arrow labelled `(lazy)` = resolved on-demand via `IServiceProvider.GetRequiredService<T>()`. This pattern breaks DI cycles where two services legitimately call each other.
- Cross-cutting services (AuditLog, Email, Notification, RoleAssignment, HumansMetrics) are shown separately to reduce noise.
- Intra-section edges are omitted when they don't cross a section boundary.

## Mermaid diagram

```mermaid
graph LR
    %% ── Section colors ──
    classDef profiles fill:#4a9eff,color:#fff
    classDef teams fill:#22c55e,color:#fff
    classDef camps fill:#f59e0b,color:#fff
    classDef cityplanning fill:#f97316,color:#fff
    classDef shifts fill:#8b5cf6,color:#fff
    classDef governance fill:#ec4899,color:#fff
    classDef legal fill:#6366f1,color:#fff
    classDef tickets fill:#14b8a6,color:#fff
    classDef campaigns fill:#ef4444,color:#fff
    classDef google fill:#0ea5e9,color:#fff
    classDef onboarding fill:#a3e635,color:#000
    classDef feedback fill:#d946ef,color:#fff
    classDef auth fill:#facc15,color:#000
    classDef users fill:#94a3b8,color:#000
    classDef budget fill:#64748b,color:#fff
    classDef calendar fill:#06b6d4,color:#fff
    classDef dashboard fill:#f43f5e,color:#fff
    classDef notifications fill:#a855f7,color:#fff
    classDef crosscut fill:#334155,color:#fff

    %% ── Cross-cutting services (hub) ──
    Audit[AuditLogService]:::crosscut
    Email[IEmailService]:::crosscut
    Notif[NotificationService]:::crosscut
    Role[RoleAssignmentService]:::auth
    Metrics[HumansMetricsService]:::crosscut

    %% ── Section services ──
    Prof[ProfileService]:::profiles
    CF[ContactFieldService]:::profiles
    UEmail[UserEmailService]:::profiles
    CommPref[CommunicationPreferenceService]:::profiles
    Contact[ContactService]:::profiles
    Merge[AccountMergeService]:::profiles
    DupAcct[DuplicateAccountService]:::profiles

    Team[TeamService]:::teams
    TPage[TeamPageService]:::teams
    TRes[TeamResourceService]:::teams

    Camp[CampService]:::camps
    CampContact[CampContactService]:::camps

    CityPlan[CityPlanningService]:::cityplanning

    ShiftMgmt[ShiftManagementService]:::shifts
    ShiftSign[ShiftSignupService]:::shifts
    GenAvail[GeneralAvailabilityService]:::shifts

    AppDec[ApplicationDecisionService]:::governance
    MembershipCalc[MembershipCalculator]:::governance

    LegalDoc[LegalDocumentService]:::legal
    AdminLegal[AdminLegalDocumentService]:::legal
    LegalSync[LegalDocumentSyncService]:::legal
    Consent[ConsentService]:::legal

    TicketQ[TicketQueryService]:::tickets
    TicketSync[TicketSyncService]:::tickets
    TicketBudget[TicketingBudgetService]:::tickets

    Campaign[CampaignService]:::campaigns

    GSyncSvc[GoogleWorkspaceSyncService]:::google
    GAdmin[GoogleAdminService]:::google
    GUser[GoogleWorkspaceUserService]:::google
    EmailProv[EmailProvisioningService]:::google
    SyncSet[SyncSettingsService]:::google
    DriveMon[DriveActivityMonitorService]:::google

    Onboard[OnboardingService]:::onboarding
    Feedback[FeedbackService]:::feedback
    Budget[BudgetService]:::budget

    User[UserService]:::users
    AcctProv[AccountProvisioningService]:::users
    Unsub[UnsubscribeService]:::users

    Cal[CalendarService]:::calendar

    Dash[DashboardService]:::dashboard

    NotifMeter[NotificationMeterProvider]:::notifications
    OutboxEmail[OutboxEmailService]:::notifications

    %% ═══════════════════════════════════
    %% Ctor-injected dependencies (solid)
    %% ═══════════════════════════════════

    %% Profiles section
    Prof --> User
    Prof --> MembershipCalc
    Prof --> Consent
    Prof --> TicketQ
    Prof --> AppDec
    Prof --> Campaign
    Prof --> Team
    Prof --> Role
    Prof --> Email
    Prof --> Audit
    CF --> Team
    CF --> Role
    Contact --> User
    Contact --> UEmail
    Contact --> CommPref
    Contact --> Audit
    UEmail --> User
    Merge --> Team
    Merge --> Role
    Merge --> Audit
    DupAcct --> Team
    DupAcct --> Role
    DupAcct --> Audit

    %% Teams section
    Team --> ShiftMgmt
    Team --> Notif
    Team --> Audit
    TPage --> Team
    TPage --> Prof
    TPage --> TRes
    TPage --> ShiftMgmt
    TPage --> User
    TRes --> Team
    TRes --> Role
    TRes --> GSyncSvc
    TRes --> Audit

    %% Camps section
    Camp --> User
    Camp --> Audit
    CampContact --> Email
    CampContact --> Audit

    %% CityPlanning section
    CityPlan --> Camp
    CityPlan --> Team
    CityPlan --> Prof
    CityPlan --> User

    %% Shifts section
    ShiftSign --> ShiftMgmt
    ShiftSign --> Notif
    ShiftSign --> Audit

    %% Governance section
    AppDec --> User
    AppDec --> Prof
    AppDec --> Role
    AppDec --> Email
    AppDec --> Notif
    AppDec --> Metrics
    AppDec --> Audit
    MembershipCalc --> Prof
    MembershipCalc --> User
    MembershipCalc --> LegalSync

    %% Legal section
    AdminLegal --> LegalSync
    AdminLegal --> Team
    LegalSync --> Prof
    LegalSync --> Notif
    Consent --> Onboard
    Consent --> LegalSync
    Consent --> Prof
    Consent --> Metrics

    %% Tickets section
    TicketQ --> Budget
    TicketQ --> Campaign
    TicketQ --> User
    TicketQ --> UEmail
    TicketQ --> Prof
    TicketQ --> Team
    TicketQ --> ShiftMgmt
    TicketSync --> User
    TicketSync --> Campaign
    TicketSync --> ShiftMgmt
    TicketBudget --> Budget

    %% Campaigns section
    Campaign --> Team
    Campaign --> User
    Campaign --> UEmail
    Campaign --> CommPref
    Campaign --> Notif
    Campaign --> Email

    %% Google section
    GSyncSvc --> Team
    GSyncSvc --> User
    GSyncSvc --> UEmail
    GSyncSvc --> SyncSet
    GSyncSvc --> Audit
    GAdmin --> GUser
    GAdmin --> GSyncSvc
    GAdmin --> Team
    GAdmin --> TRes
    GAdmin --> User
    GAdmin --> UEmail
    GAdmin --> Audit
    EmailProv --> User
    EmailProv --> Prof
    EmailProv --> GUser
    EmailProv --> UEmail
    EmailProv --> Team
    EmailProv --> Email
    EmailProv --> Notif
    EmailProv --> Audit
    DriveMon --> TRes

    %% Onboarding section
    Onboard --> Prof
    Onboard --> User
    Onboard --> AppDec
    Onboard --> MembershipCalc
    Onboard --> Email
    Onboard --> Notif
    Onboard --> Metrics
    Onboard --> Audit

    %% Feedback section
    Feedback --> User
    Feedback --> UEmail
    Feedback --> Team
    Feedback --> Email
    Feedback --> Notif
    Feedback --> Audit

    %% Budget section
    Budget --> Team

    %% Users section
    User --> Team
    Unsub --> CommPref

    %% Auth section
    Role --> User
    Role --> Notif
    Role --> Audit

    %% Calendar section
    Cal --> Team
    Cal --> Audit

    %% Dashboard section
    Dash --> Prof
    Dash --> MembershipCalc
    Dash --> AppDec
    Dash --> ShiftMgmt
    Dash --> ShiftSign
    Dash --> TicketQ
    Dash --> User

    %% Notifications / Email crosscuts
    NotifMeter --> Prof
    NotifMeter --> User
    NotifMeter --> Team
    NotifMeter --> TicketSync
    NotifMeter --> AppDec
    OutboxEmail --> UEmail
    OutboxEmail --> CommPref
    OutboxEmail --> Metrics

    %% ═══════════════════════════════════
    %% Lazy-resolved (IServiceProvider) —
    %% break DI cycles
    %% ═══════════════════════════════════

    Team -. "lazy" .-> User
    Team -. "lazy" .-> TRes
    User -. "lazy" .-> Prof
    User -. "lazy" .-> Role
    User -. "lazy" .-> ShiftMgmt
    User -. "lazy" .-> ShiftSign
    Consent -. "lazy" .-> MembershipCalc
    MembershipCalc -. "lazy" .-> Consent
    ShiftMgmt -. "lazy" .-> Team
    ShiftMgmt -. "lazy" .-> Role
    ShiftMgmt -. "lazy" .-> TicketQ
    ShiftMgmt -. "lazy" .-> User
    ShiftSign -. "lazy" .-> Team
    UEmail -. "lazy" .-> Merge
```

## Cycles broken by lazy-resolution

The `IServiceProvider` + property-getter lazy-resolution pattern is used at ~7 sites to break otherwise-intractable DI cycles. Each pair below would fail constructor injection if both sides tried to eager-inject the other:

1. **Team ↔ User** — TeamService lazy-resolves `IUserService` for user-slice stitching; UserService eagerly injects `ITeamService` for nav invalidation.
2. **User ↔ Profile** — UserService lazy-resolves `IProfileService` for full-profile reads triggered by user edits; ProfileService eagerly injects `IUserService` for identity lookups.
3. **User ↔ Role** — UserService lazy-resolves `IRoleAssignmentService` for role-claim invalidation; RoleAssignmentService eagerly injects `IUserService`.
4. **User ↔ Shifts (both)** — UserService lazy-resolves `IShiftManagementService` and `IShiftSignupService` for authorization cache invalidation on user changes; both shift services eagerly inject `IUserService` via their own service-provider lazy-resolution.
5. **Shifts ↔ Team** — ShiftManagementService lazy-resolves `ITeamService` (and ShiftSignupService does too); TeamService eagerly injects `IShiftManagementService`.
6. **Shifts ↔ Role** — ShiftManagementService lazy-resolves `IRoleAssignmentService`.
7. **Shifts ↔ TicketQuery** — ShiftManagementService lazy-resolves `ITicketQueryService` (ticket-holder → shift-eligibility lookups).
8. **Consent ↔ MembershipCalculator** — ConsentService lazy-resolves `IMembershipCalculator` for status recomputes; MembershipCalculator lazy-resolves `IConsentService` for required-docs-given checks. Both sides are lazy because this cycle is two-way hot.
9. **UserEmail ↔ AccountMerge** — UserEmailService lazy-resolves `IAccountMergeService` for merge-driven email reparenting; AccountMergeService eagerly injects `IUserEmailRepository` (not the service) to avoid the reverse edge entirely.

When adding a new cross-service call, default to ctor injection. Reach for the lazy pattern only when ctor injection produces a circular DI error, and document why at the call site.

## Fan-in hotspots (most depended-on services)

| Service | Eager dependents | Lazy dependents | Notes |
|---------|-----------------:|----------------:|-------|
| `AuditLogService` | 16 | 0 | Cross-cutting — every write-path service logs audit events. No-op alternative: audit decorator (rejected; audit is in-service per §7a). |
| `UserService` | 15 | 4 | Largest fan-in. Expose efficient batch methods (`GetByIdsAsync`) to avoid N+1 at call sites. |
| `TeamService` | 14 | 2 | Second-largest fan-in. Same batch-method guidance. |
| `NotificationService` | 8 | 0 | Cross-cutting notifications. |
| `RoleAssignmentService` | 6 | 2 | Auth hub. |
| `IEmailService` | 6 | 0 | Abstract over SmtpEmailService + OutboxEmailService. |
| `ProfileService` | 9 | 1 | Biggest Profile consumer is ProfileService itself (full-profile stitching). |
| `HumansMetricsService` | 4 | 0 | Invoked from Application services that emit counter events (ConsentService, OnboardingService, AppDec, OutboxEmail). Scheduled for push-model inversion in #580 — after that, HumansMetricsService becomes zero-section-knowledge infrastructure. |
| `TeamResourceService` | 3 | 0 | Teams-owned Google resources. |
| `ShiftManagementService` | 5 | 1 | Shift hub. |
| `CampService` | 1 | 0 | Only CityPlanning reads it. |

## Notes on architectural follow-ups

- **#580** — `HumansMetricsService` push-model inversion: sections register their own metrics instead of the service spidering across every section. After that lands, the current `Metrics` node becomes pure registry infrastructure with zero outgoing edges.
- **#581** — `NotificationMeterProvider` push-model inversion: same pattern as #580 for the navbar-badge meter counts. Post-inversion, `NotifMeter` has zero outgoing edges.
- **#570** — final slice (Google-writing jobs through service interfaces) doesn't change service→service edges; it affects Job → Service edges, which aren't part of this graph.
- The Profile section owns `FullProfile` and `IFullProfileInvalidator` as its canonical stitched-DTO implementation of §15. Other sections apply §15's caching decorator and `Full<X>` DTO layers selectively (not universally), as stitching demand warrants.
