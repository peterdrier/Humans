# Nobodies Humans — Documentation

## Feature Specifications

Business requirements, user stories, data model, and workflows for each feature area.

| Document | Description |
|----------|-------------|
| [Event Guide Management](features/26-events.md) | Submission, moderation, and publication of camp and individual events for the digital and print event guide |
| [In-App Guide Browser](features/27-guide-browser.md) | Read-only `/Events/Browse` view letting logged-in humans discover, filter, favourite, and schedule approved events without leaving Humans |
| [Google Group Membership Sync](features/43-google-group-membership-sync.md) | Expected-state reconciliation of Google Group memberships from `IGoogleGroupMembershipSource` plugins, with daily and scoped retry passes |
| [Volunteer Tracking](features/47-volunteer-tracking.md) | `/ShiftDashboard/VolunteerTracking` heatmap surfacing build-period gaps and declared-but-unbooked volunteers for the VC |
| [Active User Metrics](features/active-user-metrics.md) | Distinct authenticated users tracked by trailing window (5m / 1h / 24h), surfaced as Prometheus gauges plus three tiles on `/Admin` |
| [Agent Section](features/agent/agent-section.md) | Conversational helper grounded on docs and user state, with `route_to_issue` handoff and admin spot-check view |
| [F-12: Audit Log](features/audit-log/audit-log.md) | Structured, queryable audit trail for background job and admin actions beyond Serilog text logs |
| [User Authentication & Accounts](features/auth/authentication.md) | Secure, streamlined authentication integrated with Google Workspace and temporal role tracking for governance compliance |
| [Feature 30: Magic Link Authentication](features/auth/magic-link-auth.md) | Email-based passwordless login and signup as the foundation for non-Google auth methods |
| [Budget](features/budget/budget.md) | Seasonal budget planning, tracking, and transparency replacing the spreadsheet as the financial source of truth |
| [Community Calendar](features/calendar/community-calendar.md) | Centralized calendar of team-organized events with month/agenda views and recurrence support |
| [Feature 22: Campaigns](features/campaigns/campaigns.md) | Bulk individualized code distribution (e.g., presale ticket codes) sent in team-filtered email waves |
| [Camps](features/camps/camps.md) | Annual camping area ("barrio") registration, admin approval, public listing, and seasonal opt-ins |
| [Cantina Weekly Roster](features/cantina/daily-roster.md) | Printable per-week roster (and CSV) of who is on site, with dietary preferences, allergies, and intolerances for cantina meal planning |
| [City Planning](features/city-planning/city-planning.md) | Real-time collaborative aerial-map polygon tool for camp leads to stake out their barrio before the event |
| [Client Stats (Debug)](features/debug/client-stats.md) | `/Debug/ClientStats` screen showing, since process start, the OS / browser / device-type mix of visitors, their screen-resolution distribution, and HTTP response status-code tallies — all in-memory, no DB |
| [Email Flag Violations — Admin & Self Remediation](features/email/email-flag-violations-remediation.md) | Recovery surface for stuck `UserEmail` IsGoogle/IsPrimary duplicates with admin scan page and self-service clear actions |
| [Feature 21: Email Outbox](features/email/email-outbox.md) | Outbox pattern for reliable transactional email delivery with retry and crash recovery |
| [`[ExpiresOn]` — Hard removal deadlines](features/expires-on-deadline.md) | Analyzer-enforced removal deadlines that escalate deprecation warnings to errors on a fixed date |
| [Feedback System](features/feedback/feedback-system.md) | In-app feedback page with reporter↔admin conversation threads and FeedbackAdmin triage |
| [Administration](features/global/administration.md) | Admin dashboards and management screens for members, applications, teams, and organizational compliance |
| [Background Jobs](features/global/background-jobs.md) | Hangfire-scheduled automated operations for syncing, reminders, compliance enforcement, and system team maintenance |
| [GDPR Data Export](features/global/gdpr-export.md) | Self-service download fulfilling GDPR Article 15 right to a copy of all personal data held |
| [Global Search (`/Search`)](features/global/global-search.md) | Single-entry magnifying-glass search that fans out across humans, teams, camps, and shifts by name |
| [F-13: Drive Activity Monitoring](features/google-integration/drive-activity-monitoring.md) | Detection and logging of Google Shared Drive permission changes made outside the system |
| [Google Integration](features/google-integration/google-integration.md) | Integration with Google Workspace Shared Drives and Google Groups for managing team shared resources |
| [Google Removal Notifications](features/google-integration/google-removal-notifications.md) | Email notifications to addresses removed from Google Groups or Drive permissions, distinguishing loss-of-access from secondary-email cleanup |
| [Workspace Account Provisioning](features/google-integration/workspace-account-provisioning.md) | Admin-driven creation of @nobodies.team Google Workspace accounts linked to a human's profile |
| [Tier Applications](features/governance/asociado-applications.md) | Application entity for Colaborador and Asociado tier-based membership applications with Board voting workflow |
| [Board Voting](features/governance/board-voting.md) | Structured Board vote on Colaborador/Asociado tier applications with individual votes, meeting date, and collective decision |
| [Membership Status Partition](features/governance/membership-status.md) | Six-bucket mutually exclusive status model used by Board dashboard, Admin filters, and Volunteers team sync |
| [Membership Tiers](features/governance/membership-tiers.md) | Four-tier membership model (Volunteer / Colaborador / Asociado / Board) with three tiers managed in-system |
| [In-App Guide](features/guide/in-app-guide.md) | Embedded `/Guide` rendering of the `docs/guide/` markdown with role-aware filtering and in-app navigation |
| [Issues System](features/issues/issues-system.md) | In-app issue tracker routing bugs/features/questions by section to the right role-holders, with reporter↔handler threads |
| [Legal Documents & Consent Management](features/legal-and-consent/legal-documents-consent.md) | GDPR-compliant document version tracking with immutable consent audit trail, team-scoped, multi-language, configurable through admin GUI |
| [Mailer Audience Debug Screen](features/mailer/audience-debug-screen.md) | Per-audience debug screen previewing exactly what the next MailerLite `Sync` would apply, so admins can spot anomalies before pulling the trigger |
| [Notification Inbox](features/notifications/notification-inbox.md) | Central "what needs my attention" view with shared resolution for group-targeted notifications |
| [Onboarding Pipeline](features/onboarding/onboarding-pipeline.md) | End-to-end signup-to-active-membership journey with parallel legal-consent and Consent Coordinator review tracks |
| [Volunteer Status](features/onboarding/volunteer-status.md) | Volunteer status determined by presence in the system-managed Volunteers team requiring consent check clearance and legal document consents |
| [Communication Preferences](features/profiles/communication-preferences.md) | GDPR/CAN-SPAM-compliant per-category email and in-app alert opt-in/opt-out controls |
| [Feature 29: Contact Accounts](features/profiles/contact-accounts.md) | Pre-provisioned Identity users for external mailing-list, ticket-purchase, and admin-entered contacts |
| [Contact Fields with Granular Visibility](features/profiles/contact-fields.md) | Per-field contact information sharing (Signal, Telegram, WhatsApp, Discord, phone) with per-context privacy levels |
| [Dietary & Medical Nudge Modal](features/profiles/dietary-medical-nudge.md) | Placeholder for a dashboard nudge collecting dietary, allergy, and medical info for 6+ hour cantina-fed shifts |
| [Email Management](features/profiles/preferred-email.md) | Multiple email addresses per user with per-email verification, visibility, and notification targeting |
| [Profile Pictures & Birthday Calendar](features/profiles/profile-pictures-birthdays.md) | Custom avatar uploads superseding Google OAuth photos, plus a community birthday calendar |
| [Profile Search Detail (Picker Row Enrichment)](features/profiles/profile-search-detail.md) | Second-line context plus avatar in the shared human picker so Playa-name collisions can be disambiguated |
| [Profiles](features/profiles/profiles.md) | Personal information management distinguishing legal names from public "burner names" with location data for event planning |
| [Public Coordinator Popover](features/profiles/public-coordinator-popover.md) | Anonymous-visible reduced popover on public team pages surfacing only avatar, BurnerName, and coordinator role labels via an `AllowAnonymous` `/Profile/{id}/PublicPopover` endpoint |
| [Scanner — Barcode (Phase 1)](features/scanner/scanner-barcode.md) | Camera-based in-app barcode/QR decoder for staff to inspect TicketTailor ticket stubs (decode only, no check-in) |
| [Coordinator Roles](features/shifts/coordinator-roles.md) | Consent Coordinator and Volunteer Coordinator roles adding structured safety and facilitation gates to onboarding |
| [Department Coverage Pies](features/shifts/department-coverage-pies.md) | A row of conic-gradient discs above `/Shifts`, one per department, showing percentage-filled and acting as a clickable department filter |
| [Email a Rota](features/shifts/email-a-rota.md) | Bulk-to-rota coordinator messaging that preserves per-recipient personalization (each recipient's own shift list on the rota) over the existing outbox/audit/opt-out infrastructure |
| [Shift Management](features/shifts/shift-management.md) | Multi-day event shift configuration, signup workflows, urgency scoring, and coordinator tooling |
| [Shift Preference Wizard](features/shifts/shift-preference-wizard.md) | Guided 3-step mobile-friendly wizard collecting skills, work style, and languages for shift matching |
| [Shift Signup Visibility](features/shifts/shift-signup-visibility.md) | Visibility rules letting coordinators and admins see who has signed up for upcoming shifts |
| [Workload Dashboard](features/shifts/workload-dashboard.md) | Cross-event "who is doing how much" view sliced three ways to spot burnout candidates, idle volunteers, and under-staffed departments |
| [Store](features/store/store.md) | Per-camp catalog ordering, multi-method payments, and consolidated Holded factura issuance for Camp Lead purchases |
| [Hidden Teams](features/teams/hidden-teams.md) | Privacy-sensitive teams invisible to non-admin users for campaign targeting (e.g., low-income ticket programs) |
| [Teams & Working Groups](features/teams/teams.md) | Self-organizing working groups with optional department hierarchy and three system-managed teams tracking key organizational roles |
| [Test System Reliability](features/test-system-reliability.md) | Multi-phase rebuild of the test setup so CI catches what local sees, integration tests survive concurrent runs, and "pre-existing failures on main" stops being said |
| [Event Participation Tracking](features/tickets/event-participation.md) | Yearly event participation status per human, including self-service opt-out and ticket-driven auto-tracking |
| [Ticket Transfer](features/tickets/ticket-transfer.md) | Sender-initiated transfer of a ticket to another verified member, vendor-voided and reissued under the receiver after admin approval |
| [Ticket Vendor Integration](features/tickets/ticket-vendor-integration.md) | Dedicated Tickets section with TicketTailor sync, sales dashboard, revenue metrics, and attendee tracking |

## Section Invariants

Terse, authoritative invariant docs for each major section: concepts, data model, actors and roles, hard rules, negative-access rules, triggers, cross-section dependencies, and architecture/migration status.

| Document | Description |
|----------|-------------|
| [Admin Shell](sections/admin-shell.md) | Frame-only section providing the shared admin sidebar, breadcrumb, and dashboard skeleton — owns no tables |
| [Agent](sections/Agent.md) | Conversational helper backed by Anthropic Claude, available to authenticated consented users when `AgentSettings.Enabled = true` |
| [Audit Log](sections/AuditLog.md) | Append-only system audit trail capturing actor, action, entity, and timestamp; enforced append-only per design-rules §12 |
| [Auth](sections/Auth.md) | Temporal role assignments, magic-link login/signup, and claims transformation |
| [Budget](sections/Budget.md) | Fiscal-year budgets (Draft/Active/Closed) with groups, categories, line items, and an append-only audit log |
| [Calendar](sections/Calendar.md) | Per-team community calendar with one-off and recurring events plus per-occurrence overrides and cancellations |
| [Campaigns](sections/Campaigns.md) | Bulk code-distribution campaigns: codes imported or generated, assigned to humans, delivered via email waves |
| [Camps](sections/Camps.md) | Themed community camps (Barrios) with per-year season registrations, leads, images, and renaming history |
| [Cantina](sections/Cantina.md) | Read-only weekly roster surface for the food-service team — who is on site each day and what they can/cannot eat; composes over Shifts, owns no tables |
| [City Planning](sections/CityPlanning.md) | Interactive map surface with three screens: read-only overview, barrio polygon editing, and container placement |
| [Containers](sections/Containers.md) | Physical shipping containers managed per-barrio or at org level, placed on the City Planning map |
| [Debug](sections/Debug.md) | Developer/diagnostics section: admin-only pages exposing operational insight (client demographics, request health) that no domain section owns — owns no tables |
| [Email](sections/Email.md) | Transactional email outbox: queue, render, deliver, retry, and pause/resume — backs campaign sends, onboarding, shift, and feedback emails |
| [Events](sections/Events.md) | Event programming: submission, moderation, browsing, export, and preference management for festival events |
| [Expenses](sections/Expenses.md) | Expense reports submitted by members, approved by Finance Admin, paid by SEPA batch, and notified asynchronously to Holded |
| [Feedback](sections/Feedback.md) | In-app feedback reports (bugs, feature requests, questions) with screenshots and reporter↔admin conversation threads |
| [Finance](sections/Finance.md) | Treasurer's reality side of money — actuals, reconciliation, and treasurer-facing operational data sharing keys with Budget |
| [Google Integration](sections/GoogleIntegration.md) | Shared-Drive-only sync for Drive folders, Groups, and Workspace accounts with reconciliation and Drive-activity monitoring |
| [Governance](sections/Governance.md) | Colaborador and Asociado tier applications, Board voting workflow, and term lifecycle (not volunteer onboarding) |
| [Guide](sections/Guide.md) | The in-app `/Guide` renderer for `docs/guide/` markdown with role-scoped block filtering |
| [Holded](sections/Holded.md) | Thin typed-`HttpClient` surface to the Holded accounting API, owned narrowly for Expenses purchase documents |
| [Issues](sections/Issues.md) | In-app issue tracker (bugs, features, questions) with screenshots, role-routed triage, and a reporter↔handler conversation thread |
| [Legal & Consent](sections/LegalAndConsent.md) | GitHub-synced legal documents, per-version append-only consent records, and the Consent Coordinator review gate |
| [Mailer](sections/Mailer.md) | Humans ↔ MailerLite synchronisation: inbound import and outbound audience management |
| [Notifications](sections/Notifications.md) | In-app notification fan-out (stored events plus per-user inbox) and live meter counts (computed) |
| [Onboarding](sections/Onboarding.md) | Pure orchestrator over Profiles, Legal & Consent, Teams, and Governance — owns no tables |
| [Profiles](sections/Profiles.md) | Per-human personal data: profile, contact fields, emails, communication preferences — reference implementation for §15 caching |
| [Scanner](sections/Scanner.md) | In-browser camera tools (currently `/Scanner/Barcode`); no server-side state, no owned tables |
| [Shifts](sections/Shifts.md) | Event shifts, rotas, signups, range blocks, event settings, general availability, and per-event volunteer profiles |
| [Store](sections/Store.md) | Per-camp catalog ordering, multi-method payments, and consolidated Holded factura issuance for Camp Lead purchases |
| [Teams](sections/Teams.md) | Departments and sub-teams, join requests, role definitions, team pages, and linked Google resources |
| [Tickets](sections/Tickets.md) | External ticket vendor sync (orders + attendees), Stripe-fee enrichment, auto-matching by email, event-participation derivation |
| [Users/Identity](sections/Users.md) | The User aggregate, identity-framework extensions, account provisioning, unsubscribe surface, and event participation |

## User Guide

The end-user guide for the Humans app, organized by role within each section.

| Document | Description |
|----------|-------------|
| [Admin](guide/Admin.md) | The global control panel: managing humans, configuring Google sync, reading the audit log, triaging notifications, and running technical operations |
| [Budget](guide/Budget.md) | Plan and track money across a fiscal year with a four-level structure and append-only audit log |
| [Calendar](guide/Calendar.md) | Shared team calendars — view, create, and edit events on any team |
| [Campaigns](guide/Campaigns.md) | Distribute individualised codes to humans via grants and email waves, with per-human profile lookup and unsubscribe |
| [Camps](guide/Camps.md) | Self-organizing themed communities ("barrios") with annual registration, leads, images, and per-year seasons |
| [City Planning](guide/CityPlanning.md) | Interactive aerial map where camps stake out their physical footprint before the event |
| [Email](guide/Email.md) | Personal `@nobodies.team` mailboxes and team group emails: how they work and how to send "as" your team |
| [Events](guide/Events.md) | Browse and submit community events; admins moderate submissions |
| [Expenses](guide/Expenses.md) | Submit expense reports and track reimbursement; FinanceAdmin reviews |
| [Feedback](guide/Feedback.md) | Report a bug, suggest an improvement, or ask a question without leaving the app |
| [Google Integration](guide/GoogleIntegration.md) | Wires teams up to Google Workspace: Group, Shared Drive, Workspace accounts, and Drive activity monitoring |
| [Governance](guide/Governance.md) | Tier applications, Board votes, and coordinator/admin role assignments — not Volunteer onboarding |
| [Legal & Consent](guide/LegalAndConsent.md) | Documents you sign, GDPR Article 15 export, and Article 17 deletion |
| [Onboarding](guide/Onboarding.md) | The path from signing up to becoming an active Volunteer |
| [Profiles](guide/Profiles.md) | Your profile: personal info, contact handles, emails, shift preferences, and communication settings |
| [Shifts](guide/Shifts.md) | Browse and sign up for event shifts across Set-up, Event, and Strike; coordinators manage team-owned rotas |
| [Store](guide/Store.md) | Camp leads order barrio services; StoreAdmin manages the catalog and orders |
| [Teams](guide/Teams.md) | Departments and sub-teams, system teams, and hidden teams |
| [Tickets](guide/Tickets.md) | Mirror of external vendor ticket data with auto-matching to humans by email |

### Common questions

Plain-language pages for the things people ask most.

| Document | Description |
|----------|-------------|
| [Your `@nobodies.team` email](guide/EmailAccount.md) | Your org mailbox: what it is, signing in, and using your team's shared address |
| [Two-step verification (2FA)](guide/TwoStepVerification.md) | The required extra sign-in step on your `@nobodies.team` account, in plain language |
| [Transferring your ticket](guide/TicketTransfers.md) | Hand a ticket you hold to someone else through the app |
| [The in-app AI helper](guide/AiHelper.md) | What the chat helper does, what it can see, and that it's optional |
| [Signing in & getting unstuck](guide/SigningIn.md) | The two ways into the app and what to do when you can't get in |
| [Your data & privacy](guide/YourData.md) | Who can see your details, exporting your data, and deleting your account |

## Operational Guides

| Document | Description |
|----------|-------------|
| [Admin Role Setup](admin-role-setup.md) | Adding initial admin users via SQL |
| [GUID Reservations](guid-reservations.md) | Reserved deterministic GUID blocks for seeded data |
| [Seed Data Strategy](seed-data.md) | When to use `HasData`, migration backfills, and dev-only runtime seeders |
| [Google & External Service Setup](google-service-account-setup.md) | OAuth, service account, Maps, GitHub credentials |

## Repository Metrics

| Document | Description |
|----------|-------------|
| [Development Statistics](development-stats.md) | Historical codebase growth, file counts, and commit cadence |

## Historical Design Records

Design specs and implementation plans preserved for historical context. These document the thinking behind major features at the time they were built.

| Directory | Contents |
|-----------|----------|
| [plans/](plans/) | Early design and implementation plans (semantic versioning, Google Groups) |
| [superpowers/specs/](superpowers/specs/) | Feature design specifications |
| [superpowers/plans/](superpowers/plans/) | Feature implementation plans |

## Architecture

Clean Architecture with four layers:

```
Web             Controllers, Views, ViewModels
Application     Interfaces, DTOs, Services (business logic), Use Cases
Infrastructure  EF Core, Repositories, Stores, Caching Decorators, Jobs, Integrations
Domain          Entities, Enums, Value Objects
```

| Document | Description |
|----------|-------------|
| [Design Rules](architecture/design-rules.md) | Persistence, service ownership, repository / store / decorator pattern, cross-domain join ban, authorization, migration strategy |
| [Conventions](architecture/conventions.md) | Domain invariants, transactions, integration, time/config, rendering (Razor vs fetch), testing, exception rule, smell checklist |
| [Dependency Graph](architecture/dependency-graph.md) | Service-to-service dependency graph, current vs target edges, circular dependency analysis |
| [Data Model](architecture/data-model.md) | Entities, relationships, serialization notes |
| [Project Rules Catalog](../memory/INDEX.md) | Atomic rules (one per file under `memory/<bucket>/`). `architecture/coding-rules.md` is now a stub redirecting here. |
| [Code Review Rules](architecture/code-review-rules.md) | Hard-reject rules for code review |
| [Service / Data Access Map](architecture/service-data-access-map.md) | Per-service table access inventory |
| [Code Analysis](architecture/code-analysis.md) | Analyzers, ReSharper configuration |
| [Maintenance Log](architecture/maintenance-log.md) | Recurring maintenance tasks and last-run dates |

See the [root CLAUDE.md](../CLAUDE.md) for build commands and project overview.
