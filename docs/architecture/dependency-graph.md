# Service Dependency Graph

Directed graph of **cross-section** service-to-service dependencies. Intra-section edges are
omitted by design — a section's internal wiring belongs to the section; this map shows the
coupling between sections.

Backdoor is a leaf machine API section. Its controllers also consume Agent
(`IAgentTranscriptRead`), Feedback (`IFeedbackTriage`), Issues (`IIssueTriage`),
and Surveys (`ISurveyAnalysisRead`). Those controller dependencies are outside
this service-to-service diagram; the Backdoor node below represents only
`BackdoorApiKeyService`.

Agent's tool dispatcher and user-snapshot provider (`AgentToolDispatcher`,
`AgentUserSnapshotProvider`) are collaborators of `AgentService` that are not
registered services; their reads of Consent, Governance, Feedback, Tickets, Teams,
Auth and Settings are outside this diagram, so the Agent node shows only
`AgentService`'s own edges.

## How to read

- Solid black arrow (`-->`) = ctor-injected dependency, eagerly resolved.
- Dashed orange arrow labelled `(lazy)` = resolved on-demand via `IServiceProvider.GetRequiredService<T>()` / `Lazy<T>`. This pattern breaks DI cycles where two services legitimately call each other. A healthy graph minimizes them.
- Public contracts: edges into a section that go through its `I<Section>Service` (or `I<Section>Write`) contract are collapsed onto the owning service node. The node names the implementing service; the contract is the cross-section consumption surface.
- Services with zero cross-section service edges don't appear in the diagram; they are listed under "Services with no cross-section edges" below so the verifier can account for every service.
- Fan-out contributor interfaces (`IEnumerable<ICalendarFeedContributor>`, `IEarlyEntryProvider`, `IEventSettingsChangeListener`, `IMailerLiteAudience`, `IUserMerge`, `IUserDataContributor`) are not drawn as edges — each implementation's own deps are.

## Mermaid diagram

```mermaid
graph LR
    %% ── Section colors ──
    classDef backdoor fill:#64748b,color:#fff
    classDef agent fill:#7dd3fc,color:#000
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
    classDef monitor fill:#0369a1,color:#fff
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
    classDef rideshare fill:#f472b6,color:#000
    classDef workgroups fill:#e11d48,color:#fff
    classDef preapprovals fill:#0f766e,color:#fff
    classDef crosscut fill:#334155,color:#fff
    classDef platform fill:#52525b,color:#fff

    %% ── Cross-cutting services (hub) ──
    Audit[AuditLogService]:::crosscut
    Email[OutboxEmailService]:::crosscut
    Notif[NotificationService]:::crosscut
    Role[RoleAssignmentService]:::auth
    Metrics[HumansMetricsService]:::crosscut

    %% ── Section services (only those with cross-section edges) ──
    Backdoor[BackdoorApiKeyService]:::backdoor

    AgentSvc[AgentService]:::agent
    AgentSettings[AgentSettingsService]:::agent

    User[UserService]:::users
    UEmail[UserEmailService]:::users
    CF[ContactFieldService]:::users
    AcctProv[AccountProvisioningService]:::users
    AcctDel[AccountDeletionService]:::users
    Merge[AccountMergeService]:::users
    DupAcct[DuplicateAccountService]:::users
    UsersAudience[UsersAudienceService]:::users
    HumanLifecycle[HumanLifecycleService]:::users
    TeamMsgOpts[TeamMessageOptionsProvider]:::teams
    CommPref[CommunicationPreferenceService]:::email
    EmailProblems[EmailProblemsService]:::users
    Unsub[UnsubscribeService]:::email
    UserNameSync[UserNameSyncService]:::users

    AdminAuth[AdminAuthorizationService]:::auth
    MagicLink[MagicLinkService]:::auth

    Team[TeamService]:::teams
    TPage[TeamPageService]:::teams

    Camp[CampService]:::camps
    CampContact[CampContactService]:::camps
    CampRole[CampRoleService]:::camps

    Cantina[CantinaRosterService]:::cantina
    Dietary[DietaryProfileService]:::cantina

    CityPlan[CityPlanningService]:::cityplanning

    ShiftMgmt[ShiftManagementService]:::shifts
    ShiftSign[ShiftSignupService]:::shifts
    VolTrack[VolunteerTrackingService]:::shifts
    VolTrackExport[VolunteerTrackingExportService]:::shifts
    ShiftView[ShiftViewService]:::shifts
    RotaMsg[RotaCoordinatorMessageService]:::shifts
    Workload[WorkloadService]:::shifts

    AppDec[ApplicationDecisionService]:::governance
    MemTier[MembershipTierService]:::governance
    MembershipCalc[MembershipCalculator]:::governance
    MemQuery[MembershipQuery]:::governance
    GovIndex[GovernanceIndexService]:::governance
    AssemblyVote[AssemblyVoteService]:::governance

    LegalDoc[LegalDocumentService]:::legal
    LegalSync[LegalDocumentSyncService]:::legal
    Consent[ConsentService]:::consent
    ConsentReview[ConsentReviewService]:::consent
    LegalSyncRunner[LegalDocumentSyncRunner]:::consent

    TicketQ[TicketQueryService]:::tickets
    TicketSync[TicketSyncService]:::tickets
    TicketTransfer[TicketTransferService]:::tickets
    AttendeeImport[AttendeeContactImportService]:::tickets
    OnsiteRoster[OnsiteRosterService]:::tickets
    EventPart[EventParticipationService]:::tickets
    UserParticipationBackfill[UserParticipationBackfillService]:::tickets
    TicketVendor[TicketVendorGateway]:::tickets

    TicketTailor[TicketTailorService]:::tickets

    Stripe[StripeService]:::store

    Campaign[CampaignService]:::campaigns

    GSyncSvc[GoogleWorkspaceSyncService]:::google
    GGroupSync[GoogleGroupSyncService]:::google
    GAdmin[GoogleAdminService]:::google
    GDriveAccess[GoogleDriveAccessSyncService]:::google
    EmailProv[EmailProvisioningService]:::google
    GRemoval[GoogleRemovalNotificationService]:::google
    GIdentity[WorkspaceIdentityService]:::google
    GSyncOutbox[GoogleSyncOutboxService]:::google
    GSyncOutboxProc[GoogleSyncOutboxProcessor]:::google
    GTrans[GoogleTranslationService]:::google
    GSyncHistMig[GoogleSyncHistoryMigrationService]:::google
    GSyncLog[GoogleSyncLogService]:::google
    TRes[TeamResourceService]:::google
    SyncSettings[SyncSettingsService]:::google

    DriveMon[DriveActivityMonitorService]:::monitor

    Onboard[OnboardingService]:::onboarding
    OnboardWidget[OnboardingWidgetState]:::onboarding

    Feedback[FeedbackService]:::feedback

    Budget[BudgetService]:::budget
    TicketBudget[TicketingBudgetService]:::budget

    Finance[HoldedFinanceManagement]:::finance
    HoldedDocSync[HoldedDocumentSync]:::finance
    SepaBooking[SepaBankBooking]:::finance
    PayDetails[PaymentDetailsService]:::finance

    Holded[HoldedService]:::holded

    Cal[CalendarService]:::calendar
    ICalFeed[ICalFeedService]:::icalfeed
    CalFeedToken[CalendarFeedTokenService]:::calendar

    EmailOutbox[EmailOutboxService]:::email
    EmailOutboxProc[EmailOutboxProcessor]:::email
    ComposerSelfSend[ComposerSelfSendService]:::email
    EmailPreview[EmailPreviewService]:::email

    NotifEmitter[NotificationEmitter]:::notifications
    NotifInbox[NotificationInboxService]:::notifications
    NotifMeter[NotificationMeterProvider]:::notifications

    Gdpr[GdprService]:::crosscut

    Search[SearchService]:::search

    Issues[IssuesService]:::issues

    Store[StoreAccountingRead]:::store
    StoreInvoice[StoreInvoiceIssuer]:::store
    StoreOrders[StoreOrderReader]:::store

    ExpenseReport[ExpenseReportService]:::expenses
    ExpensePublisher[ExpenseHoldedPublisher]:::expenses
    ExpenseBgProc[ExpenseReportBackgroundProcessor]:::expenses
    ExpenseBookings[ExpenseHoldedBookingService]:::expenses

    Container[ContainerService]:::containers

    MailerLiteSync[MailerLiteAudienceSyncService]:::mailerlite
    MailerLiteImport[MailerLiteImportService]:::mailerlite
    MailerLiteGdpr[MailerLiteGdprContributor]:::mailerlite

    EventSvc[EventService]:::events

    EarlyEntry[EarlyEntryService]:::earlyentry

    Gate[GateService]:::gate

    Survey[SurveyService]:::surveys
    SurveyPrevEmail[SurveyPreviewEmailService]:::surveys
    SurveyReminder[SurveyReminderSender]:::surveys

    SettingsSvc[SettingsWriteService]:::settings

    Guide[GuideRoleResolver]:::guide

    Rideshare[RideshareService]:::rideshare

    Workgroup[WorkgroupService]:::workgroups

    Preapproval[PreapprovalService]:::preapprovals

    %% ═══════════════════════════════════
    %% Ctor-injected dependencies (solid)
    %% ═══════════════════════════════════

    %% AuditLog
    Audit --> User

    %% Backdoor
    Backdoor --> Role
    Backdoor --> User
    Backdoor --> Audit

    %% Agent
    AgentSvc --> User
    AgentSettings --> Audit

    %% Users
    User --> AdminAuth
    User --> Audit
    UEmail --> Audit
    CommPref --> Audit
    CommPref --> User
    CommPref --> TicketQ
    EmailProblems --> Audit
    Unsub --> User
    CF --> Team
    CF --> Role
    AcctProv --> Audit
    AcctDel --> Team
    AcctDel --> Role
    AcctDel --> Gdpr
    AcctDel --> TicketQ
    AcctDel --> Audit
    AcctDel --> Email
    Merge --> Audit
    Merge --> Team
    Merge --> Notif
    Merge --> Consent
    DupAcct --> Team
    DupAcct --> Role
    DupAcct --> Audit
    DupAcct --> AppDec
    DupAcct --> Consent
    UsersAudience --> TicketQ
    HumanLifecycle --> NotifEmitter
    HumanLifecycle --> NotifInbox
    HumanLifecycle --> Audit
    HumanLifecycle --> Metrics
    TeamMsgOpts --> TRes
    UserNameSync --> Audit

    %% Auth
    Role --> User
    Role --> Audit
    Role --> NotifEmitter
    MagicLink --> UEmail
    MagicLink --> User
    MagicLink --> Email

    %% Teams
    Team --> Audit
    Team --> NotifEmitter
    Team --> ShiftMgmt
    Team --> AdminAuth
    TPage --> TRes
    TPage --> ShiftMgmt
    TPage --> SettingsSvc
    TPage --> User
    TPage --> Role

    %% Camps
    Camp --> Audit
    Camp --> NotifEmitter
    Camp --> User
    Camp --> SettingsSvc
    Camp --> TicketQ
    CampContact --> Email
    CampContact --> Audit
    CampContact --> NotifEmitter
    CampContact --> User
    CampRole --> GIdentity
    CampRole --> User
    CampRole --> Audit
    CampRole --> NotifEmitter

    %% Cantina
    Cantina --> ShiftMgmt
    Cantina --> SettingsSvc
    Cantina --> User
    Cantina --> Audit
    Dietary --> User
    %% Governance
    MemTier --> User
    %% Consent
    ConsentReview --> User
    %% Finance
    PayDetails --> User
    PayDetails --> Audit

    %% CityPlanning
    CityPlan --> Camp
    CityPlan --> Team
    CityPlan --> User
    CityPlan --> Audit

    %% Shifts
    ShiftMgmt --> Audit
    ShiftMgmt --> AdminAuth
    ShiftSign --> Audit
    ShiftSign --> NotifEmitter
    ShiftSign --> AdminAuth
    ShiftSign --> User
    VolTrack --> User
    VolTrack --> TicketQ
    VolTrack --> Audit
    VolTrackExport --> User
    VolTrackExport --> Audit
    RotaMsg --> Team
    RotaMsg --> User
    RotaMsg --> Email
    RotaMsg --> Audit
    Workload --> Team
    Workload --> User

    %% Governance
    AppDec --> User
    AppDec --> Role
    AppDec --> Audit
    AppDec --> Email
    AppDec --> UEmail
    AppDec --> NotifEmitter
    AppDec --> Metrics
    MembershipCalc --> User
    MembershipCalc --> LegalSync
    MemQuery --> Team
    MemQuery --> Role
    GovIndex --> LegalDoc
    AssemblyVote --> Role
    AssemblyVote --> Team
    AssemblyVote --> User
    AssemblyVote --> UEmail
    AssemblyVote --> Email
    AssemblyVote --> NotifEmitter
    AssemblyVote --> NotifInbox
    AssemblyVote --> Audit
    AssemblyVote --> GTrans

    %% Consent
    LegalSync --> NotifEmitter
    LegalSync --> Team
    LegalSync --> User
    LegalSync --> Audit
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
    TicketQ --> SettingsSvc
    TicketQ --> Audit
    TicketQ --> AppDec
    TicketSync --> TicketTailor
    TicketSync --> Stripe
    TicketSync --> User
    TicketSync --> Campaign
    TicketSync --> SettingsSvc
    TicketTransfer --> TicketTailor
    TicketTransfer --> User
    TicketTransfer --> UEmail
    TicketTransfer --> Email
    TicketTransfer --> Audit
    AttendeeImport --> UEmail
    AttendeeImport --> AcctProv
    AttendeeImport --> User
    AttendeeImport --> SettingsSvc
    AttendeeImport --> Audit
    OnsiteRoster --> Camp
    OnsiteRoster --> Team
    OnsiteRoster --> Role
    TicketVendor --> TicketTailor
    EventPart --> User
    EventPart --> Audit
    UserParticipationBackfill --> SettingsSvc

    %% Campaigns
    Campaign --> Team
    Campaign --> UEmail
    Campaign --> User
    Campaign --> NotifEmitter
    Campaign --> Email
    Campaign --> TicketVendor
    Campaign --> Audit

    %% GoogleIntegration
    GSyncSvc --> Team
    GSyncSvc --> User
    GSyncSvc --> Audit
    GGroupSync --> Team
    GGroupSync --> User
    GGroupSync --> UEmail
    GGroupSync --> Audit
    GAdmin --> Team
    GAdmin --> User
    GAdmin --> UEmail
    GAdmin --> Audit
    GDriveAccess --> User
    GDriveAccess --> UEmail
    GDriveAccess --> Team
    EmailProv --> User
    EmailProv --> UEmail
    EmailProv --> Team
    EmailProv --> Email
    EmailProv --> NotifEmitter
    EmailProv --> Audit
    GRemoval --> UEmail
    GRemoval --> User
    GRemoval --> Email
    GIdentity --> User
    GIdentity --> Audit
    GSyncOutboxProc --> User
    GSyncOutboxProc --> Team
    GSyncOutboxProc --> Metrics
    GSyncHistMig --> Audit
    GSyncLog --> User
    GSyncLog --> UEmail
    TRes --> Team
    TRes --> Audit
    SyncSettings --> Audit

    %% Monitor
    DriveMon --> TRes
    DriveMon --> SettingsSvc
    DriveMon --> User
    DriveMon --> Audit

    %% Onboarding
    Onboard --> User
    Onboard --> AppDec
    Onboard --> Email
    Onboard --> NotifEmitter
    Onboard --> MembershipCalc
    Onboard --> Consent
    Onboard --> ConsentReview
    Onboard --> HumanLifecycle
    Onboard --> Audit
    OnboardWidget --> User
    OnboardWidget --> ShiftView
    OnboardWidget --> MembershipCalc
    OnboardWidget --> SettingsSvc
    OnboardWidget --> Consent

    %% Feedback
    Feedback --> User
    Feedback --> UEmail
    Feedback --> Team
    Feedback --> Email
    Feedback --> NotifEmitter
    Feedback --> Audit

    %% Budget
    Budget --> Team
    Budget --> User
    TicketBudget --> TicketQ

    %% Finance
    Finance --> Holded
    Finance --> Budget
    Finance --> Audit
    Finance --> User
    Finance --> UEmail
    Finance --> Email
    Finance --> ExpenseBookings
    %% Finance -> Expenses is the section project (IExpenseHoldedBookingService, read-only);
    %% Expenses -> Finance is the Finance.Contracts leaf, so the project graph stays acyclic.
    %% ExpenseBookings is not ExpenseReport: ExpenseReport -> Finance, so Finance -> ExpenseReport
    %% was a DI cycle that hung every request (peterdrier/Humans#2034).
    HoldedDocSync --> Holded
    SepaBooking --> Holded
    SepaBooking --> Audit

    %% Calendar
    Cal --> Audit
    Cal --> Team
    ICalFeed --> User
    CalFeedToken --> Audit

    %% Email
    Email --> UEmail
    Email --> Metrics
    EmailOutbox --> SettingsSvc
    EmailOutbox --> Audit
    EmailOutboxProc --> Campaign
    EmailOutboxProc --> Metrics
    ComposerSelfSend --> UEmail
    ComposerSelfSend --> User
    ComposerSelfSend --> Audit

    %% Notifications
    Notif --> Role
    Notif --> CommPref
    NotifEmitter --> CommPref
    NotifInbox --> User
    NotifMeter --> User
    NotifMeter --> GSyncSvc
    NotifMeter --> Team
    NotifMeter --> TicketSync
    NotifMeter --> AppDec
    NotifMeter --> Camp
    NotifMeter --> Consent

    %% Gdpr
    Gdpr --> User

    %% Search
    Search --> User
    Search --> Team
    Search --> Camp
    Search --> ShiftMgmt
    Search --> EventSvc

    %% Issues
    Issues --> User
    Issues --> UEmail
    Issues --> Role
    Issues --> Email
    Issues --> NotifEmitter
    Issues --> NotifInbox
    Issues --> Audit

    %% Store
    Store --> Audit
    Store --> Camp
    Store --> Team
    Store --> SettingsSvc
    Store --> Stripe
    Store --> Holded
    StoreInvoice --> Audit
    StoreInvoice --> Holded
    StoreOrders --> Camp
    StoreOrders --> Team
    StoreOrders --> SettingsSvc

    %% Expenses
    ExpenseReport --> Budget
    ExpenseReport --> Team
    ExpenseReport --> User
    ExpenseReport --> UEmail
    ExpenseReport --> Email
    ExpenseReport --> Audit
    ExpenseReport --> Holded
    ExpenseReport --> Finance
    ExpenseReport --> PayDetails
    ExpenseReport --> Preapproval
    ExpensePublisher --> User
    ExpensePublisher --> Holded
    ExpensePublisher --> Finance
    ExpensePublisher --> Audit
    ExpenseBgProc --> User
    ExpenseBgProc --> Holded
    ExpenseBgProc --> Finance
    ExpenseBgProc --> Audit
    ExpenseBookings --> User

    %% Containers
    Container --> Camp
    Container --> Audit

    %% MailerLite
    MailerLiteSync --> UEmail
    MailerLiteSync --> Audit
    MailerLiteImport --> UEmail
    MailerLiteImport --> User
    MailerLiteImport --> AcctProv
    MailerLiteImport --> CommPref
    MailerLiteImport --> Audit
    MailerLiteGdpr --> UEmail

    %% Events
    EventSvc --> SettingsSvc
    EventSvc --> User
    EventSvc --> Camp
    EventSvc --> Audit
    EventSvc --> Email

    %% Gate
    Gate --> TicketQ
    Gate --> EventPart
    Gate --> EarlyEntry
    Gate --> SettingsSvc
    Gate --> ShiftMgmt
    Gate --> Role
    Gate --> Audit

    %% Surveys
    Survey --> Audit
    Survey --> Team
    Survey --> User
    Survey --> TicketQ
    Survey --> ShiftView
    Survey --> UEmail
    Survey --> Email
    Survey --> GTrans
    Survey --> AppDec
    Survey --> Consent
    SurveyPrevEmail --> UEmail
    SurveyPrevEmail --> User
    SurveyPrevEmail --> Email
    SurveyPrevEmail --> EmailPreview
    SurveyReminder --> Audit
    SurveyReminder --> User
    SurveyReminder --> UEmail
    SurveyReminder --> Email

    %% Settings
    SettingsSvc --> Audit

    %% Guide
    Guide --> Team
    Guide --> Camp

    %% Rideshare
    Rideshare --> SettingsSvc
    Rideshare --> User
    Rideshare --> NotifEmitter
    Rideshare --> Audit

    %% Workgroups
    Workgroup --> User
    Workgroup --> Survey
    Workgroup --> UEmail
    Workgroup --> Role
    Workgroup --> SettingsSvc
    Workgroup --> GSyncSvc
    Workgroup --> Finance
    Workgroup --> Holded
    Workgroup --> Notif
    Workgroup --> Email
    Workgroup --> Audit

    %% Preapprovals
    Preapproval --> User
    Preapproval --> UEmail
    Preapproval --> Finance
    Preapproval --> NotifEmitter
    Preapproval --> NotifInbox
    Preapproval --> Email
    Preapproval --> Audit

    %% ═══════════════════════════════════
    %% Lazy-resolved (IServiceProvider/Lazy<T>) — break DI cycles
    %% ═══════════════════════════════════

    UEmail -. "lazy" .-> TicketQ
    UEmail -. "lazy" .-> GIdentity
    Team -. "lazy" .-> Role
    Team -. "lazy" .-> Email
    Team -. "lazy" .-> GSyncOutbox
    Team -. "lazy" .-> GSyncSvc
    Team -. "lazy" .-> GIdentity
    Team -. "lazy" .-> TRes
    Team -. "lazy" .-> User
    Camp -. "lazy" .-> CityPlan
    ShiftMgmt -. "lazy" .-> Camp
    ShiftMgmt -. "lazy" .-> Role
    ShiftMgmt -. "lazy" .-> Team
    ShiftMgmt -. "lazy" .-> TicketQ
    ShiftMgmt -. "lazy" .-> User
    ShiftSign -. "lazy" .-> Team
    MembershipCalc -. "lazy" .-> Consent
    Consent -. "lazy" .-> MembershipCalc
    TRes -. "lazy" .-> Role
    Cal -. "lazy" .-> Team
    Metrics -. "lazy" .-> User
    Metrics -. "lazy" .-> Consent

    %% ── Edge styling ──
    %% 369 eager edges (indices 0..368) then 22 lazy edges; linkStyle indexes the lazy block by position.
    %% Recompute the indices whenever edges change.
    linkStyle 369,370,371,372,373,374,375,376,377,378,379,380,381,382,383,384,385,386,387,388,389,390 stroke:#f97316,stroke-width:2.5px
```


## Services with no cross-section edges

Not drawn above — their collaborators are all section-internal (or fan-out contributor
interfaces / infra connectors, which this graph doesn't chart):

`AdminDatabaseDiagnosticsService` (repository only), `AgentAdminStatusService`,
`AgentAnthropicBalanceProvider`, `AuditViewerService` (fans `IEntityNameContributor`),
`BurnSettingsService`, `EventBulkImporter` (Events' own repository only),
`ExternalLoginService`, `GoogleWorkspaceUserService`, `GuideContentService`, `MailerLiteService`,
`NotificationInboxRead` (composes Notifications' own inbox + meter provider), `ProfileService`.

## Cycles broken by lazy-resolution

Each pair below would fail constructor injection if both sides eager-injected the other.

1. **ShiftManagement ↔ Team** — ShiftManagementService lazy-resolves `ITeamService`; TeamService eagerly injects `IShiftManagementService` (implemented by ShiftManagementService). (ShiftSignupService also lazy-resolves `ITeamService`.)
2. **Consent ↔ MembershipCalculator** — ConsentService lazy-resolves `IMembershipCalculator` for status recomputes; MembershipCalculator lazy-resolves `IConsentService` for required-docs-given checks. Both lazy because the cycle is two-way hot.
3. **Team ↔ TeamResource** — TeamService lazy-resolves `ITeamResourceService`; TeamResourceService eagerly injects `ITeamService`.
4. **Team ↔ GoogleWorkspaceSync** — TeamService lazy-resolves `IGoogleSyncWrite` for ad-hoc Drive/Group reconciliation; GoogleWorkspaceSyncService eagerly injects `ITeamService`.
5. **Camp ↔ CityPlanning** — CampService holds `Lazy<ICityPlanningWrite>` to delete a camp's polygon/history rows inside the camp-deletion transaction; CityPlanningService eagerly injects `ICampService`.
6. **UserEmail ↔ Tickets** — UserEmailService lazy-resolves `ITicketService` for the email delete-guard (nobodies-collective/Humans#758); TicketQueryService eagerly injects `IUserEmailService`.

Other notable one-way lazy edges:

- **Team → User** — user-slice stitching; User no longer reaches back into Team.
- **Team → GoogleSyncOutbox** — enqueues transactional-outbox Google-sync events on membership/role changes; one-way.
- **ShiftManagement → Role / User / Camp / Tickets**, **Team → Role / Email**, **TeamResource → Role** — one-way lazy where eager injection would still close a cycle through other paths (notably `ISystemTeamSync`, a job interface outside this graph).
- **Team → WorkspaceIdentity**, **UserEmail → WorkspaceIdentity** — one-way lazy resolution of `IGoogleIntegrationService` / `IGoogleIntegrationWrite`.
- **Calendar → Team**, **HumansMetrics → User / Consent** — Singleton hosts (the `CachingCalendarService` decorator, the polled metrics gauge) resolving a scoped read service per call, not cycle breaks.

When adding a new cross-service call, default to ctor injection. Reach for the lazy pattern only when ctor injection produces a circular DI error, and document why at the call site.

## Fan-in hotspots

The most depended-on cross-section surfaces (read the counts off the diagram):

- **`UserService`** — largest fan-in by far; nearly every section reads users through `IUserService`; the few cross-section writes go through `IUserWrite`; the internal `IUserManagement` joins both with the section-only members. Its only outbound edges are `IAdminAuthorizationService` and the audit crosscut, which is what keeps it foundational.
- **`AuditLogService`** — every write-path service logs audit events (in-service per design-rules §7a, not a decorator).
- **`TeamService`** — second-largest section fan-in; read consumers go through `ITeamService`, cross-section writes through `ITeamWrite` (internal `ITeamManagement` joins them); batch methods exist to avoid N+1 at call sites.
- **`UserEmailService`** — email-identity lookups across the system. (The Google Workspace address is `WorkspaceIdentityService`'s since the Users decomposition.)
- **`OutboxEmailService`** (`IEmailService`) / **`CommunicationPreferenceService`** — outbound mail and its consent/unsubscribe gating.
- **`NotificationEmitter`** — the enqueue surface almost all notifiers inject; only `AccountMergeService` and `WorkgroupService` take the full `INotificationService`.
- **`SettingsWriteService`** (`ISettingsService`) — event/app settings read by most event-facing sections.
- **`ShiftManagementService`** — shift hub; itself lazy-resolves Team/Role/Tickets/User/Camp to break cycles.
- **`AdminAuthorizationService`**, **`ShiftViewService`** — repo-only adapters with zero outbound cross-section edges.

## Pending follow-ups

- **#580 / #581** — `HumansMetricsService` and `NotificationMeterProvider` push-model inversions: sections register their own metrics/meters instead of the hub spidering across sections. Post-inversion, `Metrics` and `NotifMeter` have zero outgoing edges.
- **GoogleIntegration consumer-side gaps (PR #500)** — (1) AuditLog reads `GoogleResource` via a nav + `.Include` instead of `ITeamResourceService.GetResourceNamesByIdsAsync`; (2) Teams still owns the `GoogleResource.Team` cross-domain nav (strip → typed FK); (3) Users/Profiles should expose `IUserEmailService.InvalidateNobodiesTeamEmailsAsync()` so `GoogleController`/`ProfileController` can drop their `IMemoryCache` injection.
