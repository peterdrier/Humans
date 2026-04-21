# Service Dependency Graph

Directed graph of service-to-service dependencies. Edges marked `(target)` don't exist yet — they represent direct DB queries that need to become service calls per DESIGN_RULES.md.

## How to read

- Arrow means "depends on" / "calls"
- `(target)` = dependency that needs to be added when migrating off direct DB access
- Cross-cutting services (AuditLog, Email, Notification, RoleAssignment) are shown separately to reduce noise

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
    classDef admin fill:#64748b,color:#fff
    classDef onboarding fill:#a3e635,color:#000
    classDef feedback fill:#d946ef,color:#fff
    classDef auth fill:#facc15,color:#000
    classDef crosscut fill:#334155,color:#fff

    %% ── Cross-cutting services (hub) ──
    Audit[AuditLogService]:::crosscut
    Email[EmailOutboxService]:::crosscut
    Notif[NotificationService]:::crosscut
    Role[RoleAssignmentService]:::auth

    %% ── Section services ──
    Prof[ProfileService]:::profiles
    CF[ContactFieldService]:::profiles
    UEmail[UserEmailService]:::profiles
    CommPref[CommunicationPreferenceService]:::profiles
    Contact[ContactService]:::profiles

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

    LegalDoc[LegalDocumentService]:::legal
    AdminLegal[AdminLegalDocumentService]:::legal
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

    Onboard[OnboardingService]:::onboarding
    Feedback[FeedbackService]:::feedback
    Budget[BudgetService]:::admin

    Merge[AccountMergeService]:::admin
    DupAcct[DuplicateAccountService]:::admin
    User[UserService]:::admin

    %% ═══════════════════════════════════
    %% CURRENT dependencies (solid lines)
    %% ═══════════════════════════════════

    %% Profiles section
    Prof --> Onboard
    Prof --> Consent
    CF --> Team
    CF --> Role
    Contact --> CommPref

    %% Teams section
    Team --> Role
    Team --> ShiftMgmt
    TPage --> Team
    TPage --> Prof
    TPage --> TRes
    TPage --> ShiftMgmt
    TRes --> Team
    TRes --> Role
    TRes --> GSyncSvc

    %% Camps section
    Camp --> Audit

    %% Shifts section
    ShiftSign --> ShiftMgmt

    %% Governance section
    AppDec --> Audit

    %% Legal section
    AdminLegal --> LegalDoc
    Consent --> Onboard

    %% Tickets section
    TicketQ --> Budget
    TicketSync --> User

    %% Campaigns section
    Campaign --> CommPref

    %% Google section
    GSyncSvc --> SyncSet
    GAdmin --> GUser
    GAdmin --> GSyncSvc
    GAdmin --> UEmail
    EmailProv --> GUser
    EmailProv --> UEmail

    %% Onboarding section
    Onboard --> Audit

    %% Admin section
    Merge --> Prof
    Merge --> Team
    DupAcct --> Prof
    DupAcct --> Team

    %% ═══════════════════════════════════
    %% TARGET dependencies (dashed lines)
    %% Need to be added during migration
    %% ═══════════════════════════════════

    Prof -. "(target)" .-> AppDec
    Prof -. "(target)" .-> Campaign
    Prof -. "(target)" .-> Team
    Prof -. "(target)" .-> Role
    Prof -. "(target)" .-> ShiftMgmt
    Prof -. "(target)" .-> Camp

    Team -. "(target)" .-> User
    TPage -. "(target)" .-> User

    CityPlan --> Camp
    CityPlan --> Team
    CityPlan --> Prof

    ShiftMgmt --> Role
    ShiftMgmt --> Team
    ShiftSign --> Team

    Consent -. "(target)" .-> Prof

    Campaign -. "(target)" .-> Team
    Campaign -. "(target)" .-> User

    GSyncSvc -. "(target)" .-> User
    GSyncSvc -. "(target)" .-> Team
    GAdmin -. "(target)" .-> User
    GAdmin -. "(target)" .-> Team

    TicketQ -. "(target)" .-> User

    UEmail -. "(target)" .-> Merge
    UEmail -. "(target)" .-> User
```

## Potential circular dependencies to watch

Based on the target state:

1. **ProfileService <-> OnboardingService**: ProfileService calls OnboardingService, and OnboardingService will need profile data. Currently resolved because OnboardingService queries profiles via DB. When migrated, may need interface extraction or an orchestration pattern.

2. **ProfileService <-> ConsentService**: ProfileService calls ConsentService, ConsentService needs profile data `(target)`. Same pattern — may need interface extraction.

3. **TeamService <-> ShiftManagementService**: TeamService calls ShiftManagementService, and ShiftManagementService now calls TeamService. Circular dependency resolved via `IServiceProvider` lazy resolution in ShiftManagementService (same pattern as ConsentService and MembershipCalculator).

## Fan-in hotspots (most depended-on services)

| Service | Current dependents | Target additional |
|---------|-------------------|-------------------|
| `AuditLogService` | 12 | 0 |
| `NotificationService` | 7 | 0 |
| `EmailService` | 7 | 0 |
| `RoleAssignmentService` | 4 | +1 (Profiles) |
| `TeamService` | 8 | +2 (Campaigns, Google x2) |
| `UserService` | 1 | +5 (Teams x2, Campaigns, Google x2, Tickets) |
| `ProfileService` | 4 | +1 (Consent) |
| `CampService` | 0 | +2 (CityPlanning, Profiles) |

`UserService` and `TeamService` will become major fan-in points. These should expose efficient batch methods (e.g., `GetUsersByIdsAsync`, `GetTeamCoordinatorIdsAsync`) to avoid N+1 patterns.
