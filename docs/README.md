# Nobodies Humans — Documentation

## Feature Specifications

Business requirements, user stories, data model, and workflows for each feature area.

| Document | Description |
|----------|-------------|
| [User Authentication & Accounts](features/01-authentication.md) | Secure, streamlined authentication integrated with Google Workspace and temporal role tracking for governance compliance |
| [Profiles](features/02-profiles.md) | Personal information management distinguishing legal names from public "burner names" with location data for event planning |
| [Tier Applications](features/03-asociado-applications.md) | Application entity for Colaborador and Asociado tier-based membership applications with Board voting workflow |
| [Legal Documents & Consent Management](features/04-legal-documents-consent.md) | GDPR-compliant document version tracking with immutable consent audit trail, team-scoped, multi-language, configurable through admin GUI |
| [Volunteer Status](features/05-volunteer-status.md) | Volunteer status determined by presence in the system-managed Volunteers team requiring consent check clearance and legal document consents |
| [Teams & Working Groups](features/06-teams.md) | Self-organizing working groups with optional department hierarchy and three system-managed teams tracking key organizational roles |
| [Google Integration](features/07-google-integration.md) | Integration with Google Workspace Shared Drives and Google Groups for managing team shared resources |
| [Background Jobs](features/08-background-jobs.md) | Hangfire-scheduled automated operations for syncing, reminders, compliance enforcement, and system team maintenance |
| [Administration](features/09-administration.md) | Admin dashboards and management screens for members, applications, teams, and organizational compliance |
| [Contact Fields with Granular Visibility](features/10-contact-fields.md) | Per-field contact information sharing (Signal, Telegram, WhatsApp, Discord, phone) with per-context privacy levels |
| [Email Management](features/11-preferred-email.md) | Multiple email addresses per user with per-email verification, visibility, and notification targeting |
| [F-12: Audit Log](features/12-audit-log.md) | Structured, queryable audit trail for background job and admin actions beyond Serilog text logs |
| [F-13: Drive Activity Monitoring](features/13-drive-activity-monitoring.md) | Detection and logging of Google Shared Drive permission changes made outside the system |
| [Profile Pictures & Birthday Calendar](features/14-profile-pictures-birthdays.md) | Custom avatar uploads superseding Google OAuth photos, plus a community birthday calendar |
| [Membership Tiers](features/15-membership-tiers.md) | Four-tier membership model (Volunteer / Colaborador / Asociado / Board) with three tiers managed in-system |
| [Onboarding Pipeline](features/16-onboarding-pipeline.md) | End-to-end signup-to-active-membership journey with parallel legal-consent and Consent Coordinator review tracks |
| [Coordinator Roles](features/17-coordinator-roles.md) | Consent Coordinator and Volunteer Coordinator roles adding structured safety and facilitation gates to onboarding |
| [Board Voting](features/18-board-voting.md) | Structured Board vote on Colaborador/Asociado tier applications with individual votes, meeting date, and collective decision |
| [Camps](features/20-camps.md) | Annual camping area ("barrio") registration, admin approval, public listing, and seasonal opt-ins |
| [Feature 21: Email Outbox](features/21-email-outbox.md) | Outbox pattern for reliable transactional email delivery with retry and crash recovery |
| [Feature 22: Campaigns](features/22-campaigns.md) | Bulk individualized code distribution (e.g., presale ticket codes) sent in team-filtered email waves |
| [Membership Status Partition](features/23-membership-status.md) | Six-bucket mutually exclusive status model used by Board dashboard, Admin filters, and Volunteers team sync |
| [Ticket Vendor Integration](features/24-ticket-vendor-integration.md) | Dedicated Tickets section with TicketTailor sync, sales dashboard, revenue metrics, and attendee tracking |
| [Shift Management](features/25-shift-management.md) | Multi-day event shift configuration, signup workflows, urgency scoring, and coordinator tooling |
| [Shift Signup Visibility](features/26-shift-signup-visibility.md) | Visibility rules letting coordinators and admins see who has signed up for upcoming shifts |
| [Feedback System](features/27-feedback-system.md) | In-app feedback page with reporter↔admin conversation threads and FeedbackAdmin triage |
| [Feature 29: Contact Accounts](features/29-contact-accounts.md) | Pre-provisioned Identity users for external mailing-list, ticket-purchase, and admin-entered contacts |
| [Feature 30: Magic Link Authentication](features/30-magic-link-auth.md) | Email-based passwordless login and signup as the foundation for non-Google auth methods |
| [Budget](features/31-budget.md) | Seasonal budget planning, tracking, and transparency replacing the spreadsheet as the financial source of truth |
| [Workspace Account Provisioning](features/32-workspace-account-provisioning.md) | Admin-driven creation of @nobodies.team Google Workspace accounts linked to a human's profile |
| [Shift Preference Wizard](features/33-shift-preference-wizard.md) | Guided 3-step mobile-friendly wizard collecting skills, work style, and languages for shift matching |
| [Dietary & Medical Nudge Modal](features/35-dietary-medical-nudge.md) | Placeholder for a dashboard nudge collecting dietary, allergy, and medical info for 6+ hour cantina-fed shifts |
| [Hidden Teams](features/36-hidden-teams.md) | Privacy-sensitive teams invisible to non-admin users for campaign targeting (e.g., low-income ticket programs) |
| [Notification Inbox](features/37-notification-inbox.md) | Central "what needs my attention" view with shared resolution for group-targeted notifications |
| [City Planning](features/38-city-planning.md) | Real-time collaborative aerial-map polygon tool for camp leads to stake out their barrio before the event |
| [Community Calendar](features/39-community-calendar.md) | Centralized calendar of team-organized events with month/agenda views and recurrence support |
| [In-App Guide](features/39-in-app-guide.md) | Embedded `/Guide` rendering of the `docs/guide/` markdown with role-aware filtering and in-app navigation |
| [Communication Preferences](features/communication-preferences.md) | GDPR/CAN-SPAM-compliant per-category email and in-app alert opt-in/opt-out controls |
| [Event Participation Tracking](features/event-participation.md) | Yearly event participation status per human, including self-service opt-out and ticket-driven auto-tracking |
| [GDPR Data Export](features/gdpr-export.md) | Self-service download fulfilling GDPR Article 15 right to a copy of all personal data held |

## Section Invariants

Terse, authoritative invariant docs for each major section: concepts, data model, actors and roles, hard rules, negative-access rules, triggers, cross-section dependencies, and architecture/migration status.

| Document | Description |
|----------|-------------|
| [Audit Log](sections/AuditLog.md) | Append-only system audit trail capturing actor, action, entity, and timestamp; enforced append-only per design-rules §12 |
| [Auth](sections/Auth.md) | Temporal role assignments, magic-link login/signup, and claims transformation |
| [Budget](sections/Budget.md) | Fiscal-year budgets (Draft/Active/Closed) with groups, categories, line items, and an append-only audit log |
| [Calendar](sections/Calendar.md) | Per-team community calendar with one-off and recurring events plus per-occurrence overrides and cancellations |
| [Campaigns](sections/Campaigns.md) | Bulk code-distribution campaigns: codes imported or generated, assigned to humans, delivered via email waves |
| [Camps](sections/Camps.md) | Themed community camps (Barrios) with per-year season registrations, leads, images, and renaming history |
| [City Planning](sections/CityPlanning.md) | Interactive aerial map for camp barrio placement with polygon editing, placement-phase control, and append-only history |
| [Email](sections/Email.md) | Transactional email outbox: queue, render, deliver, retry, and pause/resume — backs campaign sends, onboarding, shift, and feedback emails |
| [Feedback](sections/Feedback.md) | In-app feedback reports (bugs, feature requests, questions) with screenshots and reporter↔admin conversation threads |
| [Google Integration](sections/GoogleIntegration.md) | Shared-Drive-only sync for Drive folders, Groups, and Workspace accounts with reconciliation and Drive-activity monitoring |
| [Governance](sections/Governance.md) | Colaborador and Asociado tier applications, Board voting workflow, and term lifecycle (not volunteer onboarding) |
| [Guide](sections/Guide.md) | The in-app `/Guide` renderer for `docs/guide/` markdown with role-scoped block filtering |
| [Legal & Consent](sections/LegalAndConsent.md) | GitHub-synced legal documents, per-version append-only consent records, and the Consent Coordinator review gate |
| [Notifications](sections/Notifications.md) | In-app notification fan-out (stored events plus per-user inbox) and live meter counts (computed) |
| [Onboarding](sections/Onboarding.md) | Pure orchestrator over Profiles, Legal & Consent, Teams, and Governance — owns no tables |
| [Profiles](sections/Profiles.md) | Per-human personal data: profile, contact fields, emails, communication preferences — reference implementation for §15 caching |
| [Shifts](sections/Shifts.md) | Event shifts, rotas, signups, range blocks, event settings, general availability, and per-event volunteer profiles |
| [Teams](sections/Teams.md) | Departments and sub-teams, join requests, role definitions, team pages, and linked Google resources |
| [Tickets](sections/Tickets.md) | External ticket vendor sync (orders + attendees), Stripe-fee enrichment, auto-matching by email, event-participation derivation |
| [Users/Identity](sections/Users.md) | The User aggregate, identity-framework extensions, account provisioning, unsubscribe surface, and event participation |

## User Guide

The end-user guide for the Humans app, organized by role within each section.

| Document | Description |
|----------|-------------|
| [Admin](guide/Admin.md) | The global control panel: managing humans, configuring sync, reading the audit log, and running technical operations |
| [Budget](guide/Budget.md) | Plan and track money across a fiscal year with a four-level structure and append-only audit log |
| [Campaigns](guide/Campaigns.md) | Distribute individualized codes to humans, assign grants, and deliver via email waves |
| [Camps](guide/Camps.md) | Self-organizing themed communities ("barrios") with annual registration, leads, images, and per-year seasons |
| [City Planning](guide/CityPlanning.md) | Interactive aerial map where camps stake out their physical footprint before the event |
| [Email](guide/Email.md) | Personal `@nobodies.team` mailboxes and team group emails: how they work and how to send "as" your team |
| [Feedback](guide/Feedback.md) | Report a bug, suggest an improvement, or ask a question without leaving the app |
| [Google Integration](guide/GoogleIntegration.md) | Wires teams up to Google Workspace: Group, Shared Drive, Workspace accounts, and Drive activity monitoring |
| [Governance](guide/Governance.md) | Tier applications, Board votes, and coordinator/admin role assignments — not Volunteer onboarding |
| [Legal & Consent](guide/LegalAndConsent.md) | Documents you sign, GDPR Article 15 export, and Article 17 deletion |
| [Onboarding](guide/Onboarding.md) | The path from signing up to becoming an active Volunteer |
| [Profiles](guide/Profiles.md) | Your profile: personal info, contact handles, emails, shift preferences, and communication settings |
| [Shifts](guide/Shifts.md) | Browse and sign up for event shifts; coordinators manage rotas for their department |
| [Teams](guide/Teams.md) | Departments and sub-teams, system teams, and hidden teams |
| [Tickets](guide/Tickets.md) | Mirror of external vendor ticket data with auto-matching to humans by email |

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
| [Coding Rules](architecture/coding-rules.md) | NodaTime, JSON, enums, string comparisons, nav, magic strings |
| [Code Review Rules](architecture/code-review-rules.md) | Hard-reject rules for code review |
| [Service / Data Access Map](architecture/service-data-access-map.md) | Per-service table access inventory |
| [Code Analysis](architecture/code-analysis.md) | Analyzers, ReSharper configuration |
| [Maintenance Log](architecture/maintenance-log.md) | Recurring maintenance tasks and last-run dates |

See the [root CLAUDE.md](../CLAUDE.md) for build commands and project overview.
