# Section Dependency Graph

Which section projects reference which, read from the `<ProjectReference>` items in
`src/Sections/*/*.csproj`. One node per section; a `Humans.<X>.Contracts` project folds
into section X.

- Solid arrow (`-->`): the section references the other section's implementation project.
- Dashed arrow (`-.->`): it references only the other section's `.Contracts` project.
- Not drawn: `Humans.Base` (every section references it) and `Humans.Web` (the Shell
  references every section).

The block below is generated. Do not edit it by hand; run
`bash docs/scripts/generate-dependency-graph.sh`.

<!-- BEGIN GENERATED: bash docs/scripts/generate-dependency-graph.sh -->

```mermaid
graph LR
    Agent
    AuditLog
    Auth
    Backdoor
    BoxOffice
    Budget
    Calendar
    Campaigns
    Camps
    Cantina
    CityPlanning
    Consent
    Containers
    Debug
    Development
    EarlyEntry
    Email
    Events
    Expenses
    Finance
    Gate
    Gdpr
    Google
    Governance
    Grants
    Guide
    HiEvents
    Holded
    Issues
    LowIncome
    MailerLite
    Monitor
    Notifications
    Onboarding
    Permissions
    Pleo
    Preapprovals
    Pretix
    Rideshare
    Roles
    Scanner
    Search
    Settings
    Shifts
    Store
    Stripe
    Surveys
    Tasks
    Teams
    TicketTailor
    Tickets
    Tour
    Users
    Workgroups
    Agent --> AuditLog
    Agent --> Auth
    Agent --> Consent
    Agent --> Issues
    Agent --> Settings
    Agent --> Shifts
    Agent -.-> Governance
    Agent -.-> Teams
    Agent -.-> Tickets
    Auth -.-> Email
    Auth -.-> Notifications
    Auth -.-> Teams
    Auth -.-> Users
    Backdoor --> Agent
    Backdoor --> Auth
    Backdoor --> Budget
    Backdoor --> Expenses
    Backdoor --> Holded
    Backdoor --> Issues
    Backdoor --> Permissions
    Backdoor --> Store
    Backdoor --> Surveys
    Backdoor --> Tasks
    Backdoor -.-> Finance
    Backdoor -.-> Notifications
    Backdoor -.-> Teams
    BoxOffice --> Stripe
    BoxOffice -.-> Tickets
    Budget --> Permissions
    Budget -.-> Camps
    Budget -.-> Finance
    Budget -.-> Teams
    Budget -.-> Tickets
    Calendar --> AuditLog
    Calendar -.-> Teams
    Campaigns -.-> Email
    Campaigns -.-> Notifications
    Campaigns -.-> Teams
    Campaigns -.-> Tickets
    Campaigns -.-> Users
    Camps --> EarlyEntry
    Camps --> Google
    Camps --> Settings
    Camps --> Shifts
    Camps -.-> CityPlanning
    Camps -.-> Email
    Camps -.-> Notifications
    Camps -.-> Teams
    Camps -.-> Tickets
    Cantina --> Settings
    Cantina --> Shifts
    CityPlanning --> Containers
    CityPlanning --> Settings
    CityPlanning -.-> Camps
    CityPlanning -.-> Teams
    Consent --> Auth
    Consent -.-> Email
    Consent -.-> Governance
    Consent -.-> Notifications
    Consent -.-> Onboarding
    Consent -.-> Teams
    Consent -.-> Users
    Containers --> Settings
    Containers -.-> Camps
    Containers -.-> CityPlanning
    Debug --> Consent
    Debug --> Settings
    Debug --> Shifts
    Debug -.-> Email
    Debug -.-> Tickets
    Development --> Auth
    Development --> Budget
    Development --> Consent
    Development -.-> Camps
    Development -.-> CityPlanning
    Development -.-> Teams
    Development -.-> Users
    Email --> Settings
    Email -.-> Users
    Events --> Calendar
    Events --> Camps
    Events --> Settings
    Events -.-> Email
    Expenses --> AuditLog
    Expenses --> Auth
    Expenses --> Budget
    Expenses --> Holded
    Expenses --> Permissions
    Expenses --> Preapprovals
    Expenses -.-> Email
    Expenses -.-> Finance
    Expenses -.-> Teams
    Finance --> Budget
    Finance --> Expenses
    Finance --> Holded
    Finance --> Pleo
    Finance --> Settings
    Finance --> Stripe
    Finance -.-> Email
    Finance -.-> Tickets
    Gate --> Auth
    Gate --> EarlyEntry
    Gate --> Settings
    Gate --> Shifts
    Gate -.-> Tickets
    Gdpr -.-> Users
    Google --> AuditLog
    Google --> Auth
    Google --> Permissions
    Google --> Settings
    Google -.-> Email
    Google -.-> Notifications
    Google -.-> Teams
    Google -.-> Users
    Governance --> Auth
    Governance --> Consent
    Governance --> Google
    Governance --> Onboarding
    Governance -.-> Email
    Governance -.-> Notifications
    Governance -.-> Teams
    Grants --> Settings
    Grants -.-> Email
    Grants -.-> Notifications
    Grants -.-> Users
    Guide --> Auth
    Guide -.-> Camps
    Guide -.-> Teams
    HiEvents --> Stripe
    HiEvents -.-> Tickets
    Holded -.-> Finance
    Issues --> AuditLog
    Issues --> Auth
    Issues -.-> Email
    Issues -.-> Notifications
    LowIncome --> Grants
    MailerLite --> Shifts
    MailerLite -.-> Email
    MailerLite -.-> Tickets
    MailerLite -.-> Users
    Monitor --> Google
    Monitor --> Settings
    Notifications --> Auth
    Notifications -.-> Email
    Onboarding --> Consent
    Onboarding --> Settings
    Onboarding --> Shifts
    Onboarding -.-> Email
    Onboarding -.-> Governance
    Onboarding -.-> Notifications
    Onboarding -.-> Teams
    Onboarding -.-> Users
    Permissions --> Auth
    Pleo -.-> Finance
    Preapprovals -.-> Email
    Preapprovals -.-> Finance
    Preapprovals -.-> Notifications
    Pretix --> Stripe
    Pretix -.-> Tickets
    Rideshare --> Settings
    Rideshare -.-> Notifications
    Roles --> Auth
    Roles --> Permissions
    Roles -.-> Teams
    Scanner --> Calendar
    Scanner --> Consent
    Scanner --> EarlyEntry
    Scanner --> Events
    Scanner --> Settings
    Scanner --> Tickets
    Shifts --> Auth
    Shifts --> Calendar
    Shifts --> Consent
    Shifts --> EarlyEntry
    Shifts --> Permissions
    Shifts --> Settings
    Shifts --> Teams
    Shifts -.-> Camps
    Shifts -.-> Email
    Shifts -.-> Notifications
    Shifts -.-> Onboarding
    Shifts -.-> Tickets
    Store --> AuditLog
    Store --> Budget
    Store --> Holded
    Store --> Permissions
    Store --> Settings
    Store --> Stripe
    Store -.-> Camps
    Store -.-> Finance
    Store -.-> Teams
    Stripe -.-> Finance
    Surveys --> Google
    Surveys --> Shifts
    Surveys -.-> Email
    Surveys -.-> Governance
    Surveys -.-> Teams
    Surveys -.-> Tickets
    Tasks --> Permissions
    Tasks --> Settings
    Tasks -.-> Email
    Tasks -.-> Teams
    Teams --> AuditLog
    Teams --> Auth
    Teams --> Consent
    Teams --> EarlyEntry
    Teams --> Google
    Teams --> Permissions
    Teams --> Roles
    Teams --> Settings
    Teams -.-> Camps
    Teams -.-> Email
    Teams -.-> Governance
    Teams -.-> Notifications
    Teams -.-> Tickets
    TicketTailor --> Tickets
    Tickets --> AuditLog
    Tickets --> Auth
    Tickets --> Budget
    Tickets --> Campaigns
    Tickets --> EarlyEntry
    Tickets --> Gate
    Tickets --> Grants
    Tickets --> Settings
    Tickets --> Stripe
    Tickets -.-> Camps
    Tickets -.-> Email
    Tickets -.-> Finance
    Tickets -.-> Governance
    Tickets -.-> Notifications
    Tickets -.-> Teams
    Tickets -.-> Users
    Users --> AuditLog
    Users --> Auth
    Users --> Cantina
    Users --> Consent
    Users --> Gdpr
    Users --> Google
    Users --> Shifts
    Users -.-> Camps
    Users -.-> Email
    Users -.-> Governance
    Users -.-> Notifications
    Users -.-> Onboarding
    Users -.-> Teams
    Users -.-> Tickets
    Workgroups --> Auth
    Workgroups --> Calendar
    Workgroups --> Google
    Workgroups --> Holded
    Workgroups --> Settings
    Workgroups --> Surveys
    Workgroups -.-> Email
    Workgroups -.-> Finance
    Workgroups -.-> Governance
    Workgroups -.-> Notifications
    Workgroups -.-> Teams
```

<!-- END GENERATED -->
