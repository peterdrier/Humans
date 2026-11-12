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
    GoogleIntegration
    Governance
    Grants
    Guide
    Holded
    Issues
    LowIncome
    MailerLite
    Monitor
    Notifications
    Onboarding
    Pleo
    Preapprovals
    Rideshare
    Scanner
    Search
    Settings
    Shifts
    Store
    Stripe
    Surveys
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
    Agent -.-> Governance
    Agent -.-> Shifts
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
    Backdoor --> Store
    Backdoor --> Surveys
    Backdoor -.-> Finance
    Backdoor -.-> Notifications
    Backdoor -.-> Users
    Budget --> Issues
    Budget -.-> Camps
    Budget -.-> Finance
    Budget -.-> Teams
    Budget -.-> Tickets
    Calendar --> AuditLog
    Calendar -.-> Teams
    Calendar -.-> Users
    Campaigns -.-> Email
    Campaigns -.-> Notifications
    Campaigns -.-> Teams
    Campaigns -.-> Tickets
    Campaigns -.-> Users
    Camps --> CityPlanning
    Camps --> EarlyEntry
    Camps --> GoogleIntegration
    Camps --> Issues
    Camps --> Settings
    Camps -.-> Email
    Camps -.-> Notifications
    Camps -.-> Shifts
    Camps -.-> Teams
    Camps -.-> Tickets
    Camps -.-> Users
    Cantina --> Settings
    Cantina -.-> Shifts
    Cantina -.-> Users
    CityPlanning --> Containers
    CityPlanning --> Issues
    CityPlanning --> Settings
    CityPlanning -.-> Camps
    CityPlanning -.-> Teams
    Consent --> Auth
    Consent --> Issues
    Consent -.-> Email
    Consent -.-> Governance
    Consent -.-> Notifications
    Consent -.-> Onboarding
    Consent -.-> Teams
    Consent -.-> Users
    Containers -.-> Camps
    Containers -.-> CityPlanning
    Debug --> Consent
    Debug -.-> Email
    Debug -.-> Shifts
    Debug -.-> Tickets
    Development --> Auth
    Development --> Budget
    Development --> Consent
    Development --> Settings
    Development -.-> Camps
    Development -.-> CityPlanning
    Development -.-> Shifts
    Development -.-> Teams
    Development -.-> Users
    EarlyEntry -.-> Shifts
    Email --> Settings
    Email -.-> Users
    Events --> Calendar
    Events --> Camps
    Events --> Settings
    Events -.-> Email
    Events -.-> Users
    Expenses --> AuditLog
    Expenses --> Budget
    Expenses --> Holded
    Expenses --> Preapprovals
    Expenses -.-> Email
    Expenses -.-> Finance
    Expenses -.-> Teams
    Finance --> Budget
    Finance --> Expenses
    Finance --> Holded
    Finance --> Pleo
    Finance -.-> Email
    Finance -.-> Tickets
    Finance -.-> Users
    Gate --> Auth
    Gate --> EarlyEntry
    Gate --> Settings
    Gate -.-> Shifts
    Gate -.-> Tickets
    Gate -.-> Users
    Gdpr -.-> Users
    GoogleIntegration --> AuditLog
    GoogleIntegration --> Auth
    GoogleIntegration --> Settings
    GoogleIntegration -.-> Email
    GoogleIntegration -.-> Notifications
    GoogleIntegration -.-> Teams
    GoogleIntegration -.-> Users
    Governance --> Auth
    Governance --> Consent
    Governance --> GoogleIntegration
    Governance --> Issues
    Governance --> Onboarding
    Governance -.-> Email
    Governance -.-> Notifications
    Governance -.-> Teams
    Governance -.-> Users
    Grants --> Settings
    Grants -.-> Email
    Grants -.-> Notifications
    Grants -.-> Users
    Guide --> Auth
    Guide -.-> Camps
    Guide -.-> Teams
    Holded -.-> Finance
    Issues --> AuditLog
    Issues --> Auth
    Issues -.-> Email
    Issues -.-> Notifications
    LowIncome --> Grants
    MailerLite -.-> Email
    MailerLite -.-> Shifts
    MailerLite -.-> Tickets
    MailerLite -.-> Users
    Monitor --> GoogleIntegration
    Monitor --> Settings
    Notifications --> Auth
    Notifications --> Consent
    Notifications --> GoogleIntegration
    Notifications -.-> Camps
    Notifications -.-> Email
    Notifications -.-> Governance
    Notifications -.-> Shifts
    Notifications -.-> Teams
    Notifications -.-> Tickets
    Notifications -.-> Users
    Onboarding --> Consent
    Onboarding --> Issues
    Onboarding --> Settings
    Onboarding -.-> Email
    Onboarding -.-> Governance
    Onboarding -.-> Notifications
    Onboarding -.-> Shifts
    Onboarding -.-> Teams
    Onboarding -.-> Users
    Pleo -.-> Finance
    Pleo -.-> Users
    Preapprovals -.-> Email
    Preapprovals -.-> Finance
    Preapprovals -.-> Notifications
    Preapprovals -.-> Users
    Rideshare --> Settings
    Rideshare -.-> Notifications
    Rideshare -.-> Users
    Scanner --> Calendar
    Scanner --> Consent
    Scanner --> EarlyEntry
    Scanner --> Events
    Scanner --> Issues
    Scanner --> Settings
    Scanner --> Tickets
    Search --> Camps
    Search --> Events
    Search --> Shifts
    Search --> Teams
    Search --> Users
    Shifts --> Auth
    Shifts --> Calendar
    Shifts --> Cantina
    Shifts --> Consent
    Shifts --> EarlyEntry
    Shifts --> Issues
    Shifts --> Onboarding
    Shifts --> Settings
    Shifts --> Teams
    Shifts -.-> Camps
    Shifts -.-> Email
    Shifts -.-> Notifications
    Shifts -.-> Tickets
    Shifts -.-> Users
    Store --> AuditLog
    Store --> Holded
    Store --> Settings
    Store --> Stripe
    Store -.-> Camps
    Store -.-> Finance
    Store -.-> Teams
    Stripe -.-> Finance
    Stripe -.-> Users
    Surveys --> GoogleIntegration
    Surveys -.-> Email
    Surveys -.-> Governance
    Surveys -.-> Shifts
    Surveys -.-> Teams
    Surveys -.-> Tickets
    Surveys -.-> Users
    Teams --> AuditLog
    Teams --> Auth
    Teams --> Consent
    Teams --> EarlyEntry
    Teams --> GoogleIntegration
    Teams --> Issues
    Teams --> Settings
    Teams -.-> Camps
    Teams -.-> Email
    Teams -.-> Governance
    Teams -.-> Notifications
    Teams -.-> Shifts
    Teams -.-> Tickets
    Teams -.-> Users
    TicketTailor --> Tickets
    Tickets --> AuditLog
    Tickets --> Auth
    Tickets --> Budget
    Tickets --> Campaigns
    Tickets --> Consent
    Tickets --> EarlyEntry
    Tickets --> Issues
    Tickets --> Settings
    Tickets --> Stripe
    Tickets -.-> Camps
    Tickets -.-> Email
    Tickets -.-> Finance
    Tickets -.-> Governance
    Tickets -.-> Teams
    Tickets -.-> Users
    Users --> AuditLog
    Users --> Auth
    Users --> Camps
    Users --> Cantina
    Users --> Consent
    Users --> Gdpr
    Users --> GoogleIntegration
    Users --> Issues
    Users --> Settings
    Users --> Shifts
    Users -.-> Email
    Users -.-> Governance
    Users -.-> Notifications
    Users -.-> Onboarding
    Users -.-> Teams
    Users -.-> Tickets
    Workgroups --> Auth
    Workgroups --> Calendar
    Workgroups --> GoogleIntegration
    Workgroups --> Holded
    Workgroups --> Settings
    Workgroups --> Surveys
    Workgroups -.-> Email
    Workgroups -.-> Finance
    Workgroups -.-> Governance
    Workgroups -.-> Notifications
    Workgroups -.-> Teams
    Workgroups -.-> Users
```

<!-- END GENERATED -->
