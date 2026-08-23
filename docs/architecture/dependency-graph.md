# Service Dependency Graph

Directed graph of **cross-section** service-to-service dependencies. Intra-section edges are
omitted by design — a section's internal wiring belongs to the section; this map shows the
coupling between sections.

## How to read

- Solid black arrow (`-->`) = ctor-injected dependency, eagerly resolved.
- Dashed orange arrow labelled `(lazy)` = resolved on-demand via `IServiceProvider.GetRequiredService<T>()` / `Lazy<T>`. This pattern breaks DI cycles where two services legitimately call each other. A healthy graph minimizes them.
- Read-split interfaces: edges into a section that read through its `I<Section>ServiceRead` boundary are collapsed onto the owning service node. The node names the full service; the read interface is the cross-section consumption surface.
- Services with zero cross-section service edges don't appear in the diagram; they are listed under "Services with no cross-section edges" below so the verifier can account for every service.
- Fan-out contributor interfaces (`IEnumerable<ICalendarFeedContributor>`, `IEarlyEntryProvider`, `IMailerLiteAudience`, `IUserMerge`, `IUserDataContributor`) are not drawn as edges — each implementation's own deps are.

## Mermaid diagram

```mermaid
graph LR
    %% ── Section colors ──
    classDef profiles fill:#4a9eff,color:#fff
    classDef teams fill:#22c55e,color:#fff
    classDef camps fill:#f59e0b,color:#fff
    classDef cantina fill:#fcd34d,color:#000
    classDef cityplanning fill:#f97316,color:#fff
    classDef shifts fill:#8b5cf6,color:#fff
    classDef governance fill:#ec4899,color:#fff
    classDef legal fill:#6366f1,color:#fff
    classDef consent fill:#818cf8,color:#fff
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
    classDef email fill:#0d9488,color:#fff
    classDef mailerlite fill:#10b981,color:#fff
    classDef search fill:#fb7185,color:#fff
    classDef issues fill:#fbbf24,color:#000
    classDef store fill:#7c3aed,color:#fff
    classDef expenses fill:#9ca3af,color:#000
    classDef finance fill:#475569,color:#fff
    classDef containers fill:#4ade80,color:#000
    classDef events fill:#2dd4bf,color:#000
    classDef earlyentry fill:#fb923c,color:#fff
    classDef settings fill:#71717a,color:#fff
    classDef surveys fill:#0ea5e9,color:#fff
    classDef icalfeed fill:#38bdf8,color:#000
    classDef gate fill:#b45309,color:#fff
    classDef holded fill:#ca8a04,color:#fff
    classDef guide fill:#65a30d,color:#fff
    classDef crosscut fill:#334155,color:#fff
    classDef platform fill:#52525b,color:#fff

    %% ── Cross-cutting services (hub) ──
    Audit[AuditLogService]:::crosscut
    AuditViewer[AuditViewerService]:::crosscut
    Email[IEmailService]:::crosscut
    Notif[NotificationService]:::crosscut
    Role[RoleAssignmentService]:::auth
    Metrics[HumansMetricsService]:::crosscut

    %% ── Section services (only those with cross-section edges) ──
    Prof[ProfileService]:::profiles
    ProfEdit[ProfileEditorService]:::profiles
    CF[ContactFieldService]:::profiles
    UEmail[UserEmailService]:::profiles
    CommPref[CommunicationPreferenceService]:::profiles
    EmailProb[EmailProblemsService]:::profiles

    Team[TeamService]:::teams
    TPage[TeamPageService]:::teams
    TRes[TeamResourceService]:::teams

    Camp[CampService]:::camps
    CampContact[CampContactService]:::camps
    CampRole[CampRoleService]:::camps

    Cantina[CantinaRosterService]:::cantina

    CityPlan[CityPlanningService]:::cityplanning

    ShiftMgmt[ShiftManagementService]:::shifts
    ShiftSign[ShiftSignupService]:::shifts
    VolTrack[VolunteerTrackingService]:::shifts
    VolTrackExport[VolunteerTrackingExportService]:::shifts
    BurnSettings[BurnSettingsService]:::shifts
    ShiftView[ShiftViewService]:::shifts
    RotaMsg[RotaCoordinatorMessageService]:::shifts
    Workload[WorkloadService]:::shifts

    AppDec[ApplicationDecisionService]:::governance
    MembershipCalc[MembershipCalculator]:::governance
    MemQuery[MembershipQuery]:::governance
    GovIndex[GovernanceIndexService]:::governance

    LegalDoc[LegalDocumentService]:::legal
    LegalSync[LegalDocumentSyncService]:::legal
    Consent[ConsentService]:::consent
    LegalSyncRunner[LegalDocumentSyncRunner]:::consent

    TicketQ[TicketQueryService]:::tickets
    TicketSync[TicketSyncService]:::tickets
    TicketBudget[TicketingBudgetService]:::tickets
    TicketTransfer[TicketTransferService]:::tickets
    AttendeeImport[AttendeeContactImportService]:::tickets
    OnsiteRoster[OnsiteRosterService]:::tickets
    TicketVendor[TicketVendorGateway]:::tickets

    Campaign[CampaignService]:::campaigns

    GSyncSvc[GoogleWorkspaceSyncService]:::google
    GGroupSync[GoogleGroupSyncService]:::google
    GAdmin[GoogleAdminService]:::google
    EmailProv[EmailProvisioningService]:::google
    DriveMon[DriveActivityMonitorService]:::google
    GRemoval[GoogleRemovalNotificationService]:::google
    GSyncOutbox[GoogleSyncOutboxService]:::google
    GSyncOutboxProc[GoogleSyncOutboxProcessor]:::google
    GTrans[GoogleTranslationService]:::google

    Onboard[OnboardingService]:::onboarding
    OnboardWidget[OnboardingWidgetState]:::onboarding
    HumanLifecycle[HumanLifecycleService]:::onboarding
    Feedback[FeedbackService]:::feedback
    Budget[BudgetService]:::budget
    Finance[HoldedFinanceService]:::finance
    Holded[HoldedService]:::holded

    User[UserService]:::users
    AcctProv[AccountProvisioningService]:::users
    Unsub[UnsubscribeService]:::users
    AcctDel[AccountDeletionService]:::users
    UserParticipationBackfill[UserParticipationBackfillService]:::users
    UEmailProvBackfill[UserEmailProviderBackfillService]:::users
    Merge[AccountMergeService]:::users
    DupAcct[DuplicateAccountService]:::users
    ExtLogin[ExternalLoginService]:::users

    AdminAuth[AdminAuthorizationService]:::auth
    MagicLink[MagicLinkService]:::auth

    Cal[CalendarService]:::calendar
    ICalFeed[ICalFeedService]:::icalfeed

    AdminDbDiag[AdminDatabaseDiagnosticsService]:::platform
    Dash[DashboardService]:::dashboard
    AdminDash[AdminDashboardService]:::dashboard

    EmailOutbox[EmailOutboxService]:::email
    NotifEmitter[NotificationEmitter]:::notifications
    NotifInbox[NotificationInboxService]:::notifications
    NotifResolver[NotificationRecipientResolver]:::notifications
    NotifMeter[NotificationMeterProvider]:::notifications
    OutboxEmail[OutboxEmailService]:::notifications

    Search[SearchService]:::search
    Issues[IssuesService]:::issues
    Store[StoreService]:::store
    ExpenseReport[ExpenseReportService]:::expenses
    Container[ContainerService]:::containers
    MailerLiteSync[MailerLiteAudienceSyncService]:::mailerlite
    MailerLiteImport[MailerLiteImportService]:::mailerlite

    EventSvc[EventService]:::events
    EarlyEntry[EarlyEntryService]:::earlyentry
    Gate[GateService]:::gate
    Survey[SurveyService]:::surveys
    SettingsSvc[Settings Service]:::settings
    SettingsCarry[EventSettingsCarryService]:::settings
    Guide[GuideRoleResolver]:::guide

    %% ═══════════════════════════════════
    %% Ctor-injected dependencies (solid)
    %% ═══════════════════════════════════

    %% AuditLog (crosscut read+render side)
    Audit --> User
    AuditViewer --> User
    AuditViewer --> Team
    AuditViewer --> TRes

    %% Profiles
    Prof --> User
    ProfEdit --> User
    CF --> User
    CF --> Team
    CF --> Role
    UEmail --> User
    UEmail --> Audit
    CommPref --> User
    CommPref --> Audit
    EmailProb --> User

    %% Teams
    Team --> ShiftMgmt
    Team --> NotifEmitter
    Team --> Audit
    Team --> AdminAuth
    TPage --> ShiftMgmt
    TPage --> User
    TRes --> Audit

    %% Camps
    Camp --> User
    Camp --> NotifEmitter
    Camp --> Audit
    CampContact --> Email
    CampContact --> NotifEmitter
    CampContact --> Audit
    CampRole --> User
    CampRole --> UEmail
    CampRole --> NotifEmitter
    CampRole --> Audit

    %% Cantina
    Cantina --> ShiftMgmt
    Cantina --> User
    Cantina --> BurnSettings

    %% CityPlanning
    CityPlan --> Camp
    CityPlan --> Team
    CityPlan --> User

    %% Shifts
    ShiftMgmt --> Audit
    ShiftMgmt --> AdminAuth
    ShiftSign --> NotifEmitter
    ShiftSign --> Audit
    ShiftSign --> AdminAuth
    VolTrack --> User
    VolTrackExport --> User
    RotaMsg --> Team
    RotaMsg --> User
    RotaMsg --> Email
    RotaMsg --> Audit
    Workload --> Team
    Workload --> User

    %% Governance
    AppDec --> User
    AppDec --> Role
    AppDec --> UEmail
    AppDec --> Email
    AppDec --> NotifEmitter
    AppDec --> Metrics
    AppDec --> Audit
    MembershipCalc --> User
    MembershipCalc --> LegalSync
    MemQuery --> Team
    MemQuery --> Role
    GovIndex --> LegalDoc
    GovIndex --> User

    %% Legal + Consent
    LegalSync --> User
    LegalSync --> Team
    LegalSync --> NotifEmitter
    Consent --> LegalSync
    Consent --> NotifInbox
    Consent --> HumanLifecycle
    Consent --> User
    Consent --> Metrics
    LegalSyncRunner --> Email
    LegalSyncRunner --> Team
    LegalSyncRunner --> User

    %% Tickets
    TicketQ --> Budget
    TicketQ --> Campaign
    TicketQ --> User
    TicketQ --> UEmail
    TicketQ --> Team
    TicketQ --> ShiftMgmt
    TicketSync --> User
    TicketSync --> Campaign
    TicketSync --> ShiftMgmt
    TicketBudget --> Budget
    TicketTransfer --> User
    TicketTransfer --> UEmail
    TicketTransfer --> Email
    TicketTransfer --> Audit
    AttendeeImport --> AcctProv
    AttendeeImport --> User
    AttendeeImport --> UEmail
    AttendeeImport --> ShiftMgmt
    AttendeeImport --> Audit
    OnsiteRoster --> User
    OnsiteRoster --> Camp
    OnsiteRoster --> Team
    OnsiteRoster --> Role

    %% Campaigns
    Campaign --> Team
    Campaign --> User
    Campaign --> UEmail
    Campaign --> CommPref
    Campaign --> NotifEmitter
    Campaign --> Email
    Campaign --> TicketVendor

    %% Google
    GSyncSvc --> Team
    GSyncSvc --> User
    GSyncSvc --> UEmail
    GSyncSvc --> Audit
    GGroupSync --> Team
    GGroupSync --> TRes
    GGroupSync --> User
    GGroupSync --> UEmail
    GGroupSync --> Audit
    GAdmin --> Team
    GAdmin --> TRes
    GAdmin --> User
    GAdmin --> UEmail
    GAdmin --> Audit
    EmailProv --> User
    EmailProv --> UEmail
    EmailProv --> Team
    EmailProv --> Email
    EmailProv --> NotifEmitter
    EmailProv --> Audit
    GRemoval --> UEmail
    GRemoval --> User
    GRemoval --> Email
    DriveMon --> TRes
    DriveMon --> User
    DriveMon --> SettingsSvc
    DriveMon --> Audit
    GSyncOutboxProc --> User
    GSyncOutboxProc --> Team
    GSyncOutboxProc --> Metrics

    %% Onboarding
    Onboard --> User
    Onboard --> AppDec
    Onboard --> MembershipCalc
    Onboard --> Consent
    Onboard --> Email
    Onboard --> NotifEmitter
    Onboard --> Audit
    OnboardWidget --> User
    OnboardWidget --> ShiftView
    OnboardWidget --> MembershipCalc
    OnboardWidget --> ShiftMgmt
    OnboardWidget --> Consent
    HumanLifecycle --> User
    HumanLifecycle --> NotifEmitter
    HumanLifecycle --> NotifInbox
    HumanLifecycle --> Audit
    HumanLifecycle --> Metrics

    %% Feedback
    Feedback --> User
    Feedback --> UEmail
    Feedback --> Team
    Feedback --> Email
    Feedback --> NotifEmitter
    Feedback --> Audit

    %% Budget + Finance + Holded
    Budget --> Team
    Budget --> User
    Finance --> Budget
    Finance --> Holded

    %% Users
    User --> AdminAuth
    AcctProv --> UEmail
    AcctProv --> Audit
    Unsub --> CommPref
    UserParticipationBackfill --> ShiftMgmt
    UEmailProvBackfill --> Audit
    AcctDel --> UEmail
    AcctDel --> Team
    AcctDel --> Role
    AcctDel --> ShiftMgmt
    AcctDel --> ShiftSign
    AcctDel --> TicketQ
    AcctDel --> Audit
    AcctDel --> Email
    Merge --> Role
    Merge --> Notif
    Merge --> Audit
    DupAcct --> Team
    DupAcct --> Role
    ExtLogin --> UEmail
    ExtLogin --> MagicLink

    %% Auth
    Role --> User
    Role --> NotifEmitter
    Role --> Audit
    MagicLink --> UEmail
    MagicLink --> User
    MagicLink --> Email

    %% Calendar
    Cal --> Team
    Cal --> Audit
    ICalFeed --> User

    %% Dashboard
    Dash --> MembershipCalc
    Dash --> AppDec
    Dash --> ShiftMgmt
    Dash --> BurnSettings
    Dash --> ShiftView
    Dash --> TicketQ
    Dash --> User
    Dash --> Team
    AdminDash --> User
    AdminDash --> MembershipCalc
    AdminDash --> AppDec
    AdminDash --> ShiftMgmt
    AdminDash --> ShiftView

    %% Notifications
    Notif --> CommPref
    NotifEmitter --> CommPref
    NotifInbox --> User
    NotifResolver --> Role
    NotifMeter --> User
    NotifMeter --> GSyncSvc
    NotifMeter --> Team
    NotifMeter --> TicketSync
    NotifMeter --> AppDec
    NotifMeter --> Camp
    OutboxEmail --> UEmail
    OutboxEmail --> CommPref
    OutboxEmail --> Metrics

    %% Search / Issues / Store
    Search --> User
    Search --> Team
    Search --> Camp
    Search --> ShiftMgmt
    Search --> EventSvc
    Issues --> User
    Issues --> UEmail
    Issues --> Role
    Issues --> Email
    Issues --> NotifEmitter
    Issues --> NotifInbox
    Issues --> Audit
    Store --> Camp
    Store --> Team
    Store --> ShiftMgmt
    Store --> Holded
    Store --> Audit

    %% Surveys
    Survey --> Team
    Survey --> User
    Survey --> TicketQ
    Survey --> ShiftView
    Survey --> UEmail
    Survey --> Email
    Survey --> Audit
    Survey --> GTrans

    %% Gate
    Gate --> TicketQ
    Gate --> EarlyEntry
    Gate --> BurnSettings
    Gate --> ShiftMgmt
    Gate --> Role
    Gate --> User
    Gate --> Audit

    %% Expenses / Containers / MailerLite / Events
    ExpenseReport --> Budget
    ExpenseReport --> Team
    ExpenseReport --> User
    ExpenseReport --> Finance
    ExpenseReport --> Audit
    Container --> Camp
    Container --> Audit
    MailerLiteSync --> UEmail
    MailerLiteSync --> Audit
    MailerLiteImport --> UEmail
    MailerLiteImport --> User
    MailerLiteImport --> AcctProv
    MailerLiteImport --> CommPref
    MailerLiteImport --> Audit
    EventSvc --> BurnSettings
    EventSvc --> User
    EventSvc --> Email

    %% Email (admin outbox — pause flag lives in Settings)
    EmailOutbox --> SettingsSvc

    %% Settings' carry screen reads the Shifts rows it copies from (#1104).
    %% Temporary: retires with the carry screen.
    SettingsCarry --> BurnSettings

    %% Web platform (diagnostics — moved to Humans.Web/Services at #1369)
    AdminDbDiag --> User
    AdminDbDiag --> TicketQ

    %% Guide
    Guide --> Team

    %% ═══════════════════════════════════
    %% Lazy-resolved (IServiceProvider/Lazy<T>) — break DI cycles
    %% ═══════════════════════════════════

    Team -. "lazy" .-> User
    Team -. "lazy" .-> Role
    Team -. "lazy" .-> Email
    Team -. "lazy" .-> GSyncOutbox
    Team -. "lazy" .-> GSyncSvc
    TRes -. "lazy" .-> Role
    Camp -. "lazy" .-> CityPlan
    Consent -. "lazy" .-> MembershipCalc
    MembershipCalc -. "lazy" .-> Consent
    ShiftMgmt -. "lazy" .-> Team
    ShiftMgmt -. "lazy" .-> Role
    ShiftMgmt -. "lazy" .-> TicketQ
    ShiftMgmt -. "lazy" .-> User
    ShiftMgmt -. "lazy" .-> Camp
    ShiftSign -. "lazy" .-> Team
    UEmail -. "lazy" .-> Merge
    UEmail -. "lazy" .-> TicketQ
    GSyncSvc -. "lazy" .-> TRes

    %% ── Edge styling ──
    %% Lazy edges colored + thickened. Eager count: 269 (indices 0..268);
    %% the 18 lazy edges are indices 269..286. Recompute whenever edges change.
    linkStyle 269,270,271,272,273,274,275,276,277,278,279,280,281,282,283,284,285,286 stroke:#f97316,stroke-width:2.5px
```

## Services with no cross-section edges

Not drawn above — their collaborators are all section-internal (or fan-out contributor
interfaces / infra connectors, which this graph doesn't chart):

`AgentService`, `AgentAdminStatusService`, `AgentSettingsService`, `AgentAnthropicBalanceProvider`,
`GdprExportService` (fans `IUserDataContributor`), `GoogleWorkspaceUserService`,
`SyncSettingsService`, `GuideContentService`, `MailerLiteService`, `StripeService`,
`TicketVendorService`.

## Cycles broken by lazy-resolution

Each pair below would fail constructor injection if both sides eager-injected the other.

1. **ShiftManagement ↔ Team** — ShiftManagementService lazy-resolves `ITeamService`; TeamService eagerly injects `IShiftManagementService`. (ShiftSignupService also lazy-resolves `ITeamServiceRead`; the reverse edge runs through ShiftManagementService.)
2. **ShiftManagement ↔ Tickets** — ShiftManagementService lazy-resolves `ITicketServiceRead` (ticket-holder → shift-eligibility lookups); TicketQueryService eagerly injects `IShiftManagementService`.
3. **Consent ↔ MembershipCalculator** — ConsentService lazy-resolves `IMembershipCalculator` for status recomputes; MembershipCalculator lazy-resolves `IConsentServiceRead` for required-docs-given checks. Both lazy because the cycle is two-way hot.
4. **GoogleWorkspaceSync ↔ TeamResource** — GoogleWorkspaceSyncService lazy-resolves `ITeamResourceService` inside `ReconcileNobodiesDriveAsync`; the reverse eager edge is gone but the call still needs the live scoped instance.
5. **Camp ↔ CityPlanning** — CampService holds `Lazy<ICityPlanningService>` to delete a camp's polygon/history rows inside the camp-deletion transaction; CityPlanningService eagerly injects `ICampServiceRead`.
6. **UserEmail ↔ Tickets** — UserEmailService lazy-resolves `ITicketServiceRead` for the email delete-guard (nobodies-collective/Humans#758); TicketQueryService eagerly injects `IUserEmailService`.

Other notable one-way lazy edges:

- **Team → User** — user-slice stitching; User no longer reaches back into Team.
- **UserEmail → AccountMerge** — merge-driven email reparenting; the reverse path runs through the `IEnumerable<IUserMerge>` fan-out, not an eager edge.
- **Team → GoogleSyncOutbox** — enqueues transactional-outbox Google-sync events on membership/role changes; one-way.
- **Team → GoogleWorkspaceSync** — ad-hoc Drive/Group reconciliation from team admin actions; lazy because the scoped instance must resolve at call time.
- **ShiftManagement → Role / User / Camp**, **Team → Role / Email**, **TeamResource → Role** — one-way lazy where eager injection would still close a cycle through other paths (notably `ISystemTeamSync`, a job interface outside this graph).

When adding a new cross-service call, default to ctor injection. Reach for the lazy pattern only when ctor injection produces a circular DI error, and document why at the call site.

## Fan-in hotspots

The most depended-on cross-section surfaces (read the counts off the diagram):

- **`UserService`** — largest fan-in by far; nearly every section reads users through `IUserServiceRead`. No outbound edges except `IAdminAuthorizationService`, which is what keeps it foundational.
- **`AuditLogService`** — every write-path service logs audit events (in-service per design-rules §7a, not a decorator).
- **`TeamService`** — second-largest section fan-in; read consumers go through `ITeamServiceRead`; batch methods exist to avoid N+1 at call sites.
- **`UserEmailService`** — email-identity lookups across the system.
- **`ShiftManagementService`** — shift hub; itself lazy-resolves Team/Role/Tickets/User/Camp to break cycles.
- **`NotificationEmitter`** — the enqueue surface almost all notifiers inject; only `AccountMergeService` takes the full `INotificationService`.
- **`IEmailService`** / **`CommunicationPreferenceService`** — outbound mail and its consent/unsubscribe gating.
- **`AdminAuthorizationService`**, **`BurnSettingsService`**, **`ShiftViewService`** — repo-only adapters with zero outbound service edges.

## Pending follow-ups

- **#580 / #581** — `HumansMetricsService` and `NotificationMeterProvider` push-model inversions: sections register their own metrics/meters instead of the hub spidering across sections. Post-inversion, `Metrics` and `NotifMeter` have zero outgoing edges.
- **GoogleIntegration consumer-side gaps (PR #500)** — (1) AuditLog reads `GoogleResource` via a nav + `.Include` instead of `ITeamResourceService.GetResourceNamesByIdsAsync`; (2) Teams still owns the `GoogleResource.Team` cross-domain nav (strip → typed FK); (3) Users/Profiles should expose `IUserEmailService.InvalidateNobodiesTeamEmailsAsync()` so `GoogleController`/`ProfileController` can drop their `IMemoryCache` injection.
