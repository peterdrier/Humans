# Nobodies Humans — Documentation

## Feature Specifications

Business requirements, user stories, data model, and workflows for each feature area.

| Document | Description |
|----------|-------------|
| [Authentication](features/01-authentication.md) | Google OAuth, user accounts, role assignments |
| [Profiles](features/02-profiles.md) | Personal information, burner names, location, profile pictures |
| [Asociado Applications](features/03-asociado-applications.md) | Tier application workflow and Board voting |
| [Legal Documents & Consent](features/04-legal-documents-consent.md) | GDPR compliance, document versioning, consent tracking |
| [Volunteer Status](features/05-volunteer-status.md) | Volunteer approval, status computation |
| [Teams](features/06-teams.md) | Self-organizing teams, system teams, join requests, role slots |
| [Google Integration](features/07-google-integration.md) | Drive provisioning, Groups via Cloud Identity, sync modes |
| [Background Jobs](features/08-background-jobs.md) | Automated sync, reminders, compliance enforcement |
| [Administration](features/09-administration.md) | Admin dashboard, human management, configuration |
| [Contact Fields](features/10-contact-fields.md) | Granular contact visibility, E.164 phone validation |
| [Preferred Email](features/11-preferred-email.md) | System notification email preferences |
| [Audit Log](features/12-audit-log.md) | Comprehensive audit trail for admin and automated actions |
| [Drive Activity Monitoring](features/13-drive-activity-monitoring.md) | Google Drive permission change tracking |
| [Profile Pictures & Birthdays](features/14-profile-pictures-birthdays.md) | Avatar uploads, birthday calendar |
| [Membership Tiers](features/15-membership-tiers.md) | Volunteer / Colaborador / Asociado tier system |
| [Onboarding Pipeline](features/16-onboarding-pipeline.md) | Signup → consent → review → approval flow |
| [Coordinator Roles](features/17-coordinator-roles.md) | Consent and Volunteer Coordinator workflows |
| [Board Voting](features/18-board-voting.md) | Board voting on tier applications |
| [Camps](features/20-camps.md) | Event camp registration, approval, co-leads, public API |
| [Email Outbox](features/21-email-outbox.md) | Outbox-based email delivery with retry and crash recovery |
| [Campaigns](features/22-campaigns.md) | Campaign system with CSV import and discount codes |
| [Membership Status](features/23-membership-status.md) | 6-bucket membership partition model |
| [Ticket Vendor Integration](features/24-ticket-vendor-integration.md) | TicketTailor sync, sales dashboard, attendee matching |
| [Shift Management](features/25-shift-management.md) | Shift browsing, signup workflows, urgency scoring, coordinator tooling |
| [Shift Signup Visibility](features/26-shift-signup-visibility.md) | Visibility rules for volunteer and admin shift views |
| [Feedback System](features/27-feedback-system.md) | Feedback reports, triage workflow, GitHub issue integration |
| [Communication Preferences](features/28-communication-preferences.md) | Member communication channel and frequency preferences |
| [Contact Accounts](features/29-contact-accounts.md) | External contact records and account linking |
| [Magic Link Auth](features/30-magic-link-auth.md) | Passwordless email-based login and signup |
| [Budget](features/31-budget.md) | Seasonal budget planning, visibility, and approvals |
| [Workspace Account Provisioning](features/32-workspace-account-provisioning.md) | @nobodies.team account provisioning and linking |

## Operational Guides

| Document | Description |
|----------|-------------|
| [Admin Role Setup](admin-role-setup.md) | Adding initial admin users via SQL |
| [GUID Reservations](guid-reservations.md) | Reserved deterministic GUID blocks for seeded data |
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
Application     Interfaces, DTOs, Use Cases
Infrastructure  EF Core, Services, Jobs
Domain          Entities, Enums, Value Objects
```

Primary macro-level guidance lives in [architecture.md](architecture.md).

See the [root CLAUDE.md](../CLAUDE.md) for build commands, coding rules, and project conventions.
