# Nobodies Humans — Documentation

## Feature Specifications

Business requirements, user stories, data model, and workflows for each feature area.

| Document | Description |
|----------|-------------|
| [Event Guide Management](../src/Sections/Humans.Events/Docs/features/Events-feature.md) | Submission, moderation, and publication of camp and individual events for the digital and print event guide |
| [In-App Guide Browser](../src/Sections/Humans.Guide/Docs/features/27-guide-browser.md) | Read-only `/Events/Browse` view letting logged-in humans discover, filter, favourite, and schedule approved events without leaving Humans |
| [Google Group Membership Sync](../src/Sections/Humans.GoogleIntegration/Docs/features/43-google-group-membership-sync.md) | Expected-state reconciliation of Google Group memberships from `IGoogleGroupMembershipSource` plugins, with daily and scoped retry passes |
| [Volunteer Tracking](../src/Sections/Humans.Shifts/Docs/features/47-volunteer-tracking.md) | `/Shifts/Dashboard/VolunteerTracking` heatmap surfacing build-period gaps and declared-but-unbooked volunteers for the VC |
| [Active User Metrics](features/global/active-user-metrics.md) | Distinct authenticated users tracked by trailing window (5m / 1h / 24h), surfaced as Prometheus gauges plus three tiles on `/Admin` |
| [Agent Section](../src/Sections/Humans.Agent/Docs/features/Agent-feature.md) | Conversational helper grounded on docs and user state, with `route_to_issue` handoff and admin spot-check view |
| [F-12: Audit Log](../src/Sections/Humans.AuditLog/Docs/features/audit-log.md) | Structured, queryable audit trail for background job and admin actions beyond Serilog text logs |
| [User Authentication & Accounts](../src/Sections/Humans.Auth/Docs/features/authentication.md) | Secure, streamlined authentication integrated with Google Workspace and temporal role tracking for governance compliance |
| [Feature 30: Magic Link Authentication](../src/Sections/Humans.Auth/Docs/features/magic-link-auth.md) | Email-based passwordless login and signup as the foundation for non-Google auth methods |
| [Budget](../src/Sections/Humans.Budget/Docs/features/Budget-feature.md) | Seasonal budget planning, tracking, and transparency replacing the spreadsheet as the financial source of truth |
| [Community Calendar](../src/Sections/Humans.Calendar/Docs/features/community-calendar.md) | Centralized calendar of team-organized events with month/agenda views and recurrence support |
| [Feature 22: Campaigns](../src/Sections/Humans.Campaigns/Docs/features/Campaigns-feature.md) | Bulk individualized code distribution (e.g., presale ticket codes) sent in team-filtered email waves |
| [Camps](../src/Sections/Humans.Camps/Docs/features/Camps-feature.md) | Annual camping area ("barrio") registration, admin approval, public listing, and seasonal opt-ins |
| [Cantina Weekly Roster](../src/Sections/Humans.Cantina/Docs/features/daily-roster.md) | Printable per-week roster (and CSV) of who is on site, with dietary preferences, allergies, and intolerances for cantina meal planning |
| [City Planning](../src/Sections/Humans.CityPlanning/Docs/features/city-planning.md) | Real-time collaborative aerial-map polygon tool for camp leads to stake out their barrio before the event |
| [Client Stats (Debug)](../src/Sections/Humans.Debug/Docs/features/client-stats.md) | `/Debug/ClientStats` screen showing, since process start, the OS / browser / device-type mix of visitors, their screen-resolution distribution, and HTTP response status-code tallies — all in-memory, no DB |
| [HTTP Errors (Debug)](../src/Sections/Humans.Debug/Docs/features/http-errors.md) | `/Debug/HttpErrors` screen showing the last 1000 error responses (status > 399) with per-request detail: when, code, method, URL, IP, authenticated user, and classified User-Agent — all in-memory, no DB |
| [Email Flag Violations — Admin & Self Remediation](../src/Sections/Humans.Email/Docs/features/email-flag-violations-remediation.md) | Recovery surface for stuck `UserEmail` IsGoogle/IsPrimary duplicates with admin scan page and self-service clear actions |
| [Feature 21: Email Outbox](../src/Sections/Humans.Email/Docs/features/email-outbox.md) | Outbox pattern for reliable transactional email delivery with retry and crash recovery |
| [`[ExpiresOn]` — Hard removal deadlines](features/global/expires-on-deadline.md) | Analyzer-enforced removal deadlines that escalate deprecation warnings to errors on a fixed date |
| [Feedback System](../src/Sections/Humans.Feedback/Docs/features/feedback-system.md) | Retired (nobodies-collective/Humans#977) — closed to new reports and Admin-only; superseded by Issues |
| [Gate Admissions](../src/Sections/Humans.Gate/Docs/features/gate-admissions.md) | Gate QR scanning on rugged tablets deciding entry against ticket validity, photo-ID name check, and Early Entry grants — shipped design draft; `src/Sections/Humans.Gate/Docs/Gate.md` is the authoritative current-state doc |
| [Administration](features/global/administration.md) | Admin dashboards and management screens for members, applications, teams, and organizational compliance |
| [Background Jobs](features/global/background-jobs.md) | Hangfire-scheduled automated operations for syncing, reminders, compliance enforcement, and system team maintenance |
| [GDPR Data Export](features/global/gdpr-export.md) | Self-service download fulfilling GDPR Article 15 right to a copy of all personal data held |
| [Global Search (`/Search`)](features/global/global-search.md) | Single-entry magnifying-glass search that fans out across humans, teams, camps, shifts, and (when `Features:Events` is on) approved events |
| [Section Activation](features/global/section-activation.md) | `ISection.IsActive` defaults to true so every section assembly ships active by default; a section opts itself out by overriding it, with no configuration key or name list |
| [F-13: Drive Activity Monitoring](../src/Sections/Humans.GoogleIntegration/Docs/features/drive-activity-monitoring.md) | Detection and logging of Google Shared Drive permission changes made outside the system |
| [Google Integration](../src/Sections/Humans.GoogleIntegration/Docs/features/google-integration.md) | Integration with Google Workspace Shared Drives and Google Groups for managing team shared resources |
| [Google Removal Notifications](../src/Sections/Humans.GoogleIntegration/Docs/features/google-removal-notifications.md) | Email notifications to addresses removed from Google Groups or Drive permissions, distinguishing loss-of-access from secondary-email cleanup |
| [Workspace Account Provisioning](../src/Sections/Humans.GoogleIntegration/Docs/features/workspace-account-provisioning.md) | Admin-driven creation of @nobodies.team Google Workspace accounts linked to a human's profile |
| [Tier Applications](../src/Sections/Humans.Governance/Docs/features/asociado-applications.md) | Application entity for Colaborador and Asociado tier-based membership applications with Board voting workflow |
| [Board Voting](../src/Sections/Humans.Governance/Docs/features/board-voting.md) | Structured Board vote on Colaborador/Asociado tier applications with individual votes, meeting date, and collective decision |
| [Membership Status Partition](../src/Sections/Humans.Governance/Docs/features/membership-status.md) | Six-bucket mutually exclusive status model computed by `PartitionUsersAsync` and used by the Admin dashboard — the Admin /Humans list and Volunteers team sync each compute their own status buckets independently now |
| [Membership Tiers](../src/Sections/Humans.Governance/Docs/features/membership-tiers.md) | Four-tier membership model (Volunteer / Colaborador / Asociado / Board) with three tiers managed in-system |
| [In-App Guide](../src/Sections/Humans.Guide/Docs/features/in-app-guide.md) | Embedded `/Guide` rendering of the `docs/guide/` markdown with role-aware filtering and in-app navigation |
| [Issues System](../src/Sections/Humans.Issues/Docs/features/issues-system.md) | In-app issue tracker routing bugs/features/questions by section to the right role-holders, with reporter↔handler threads |
| [Legal Documents & Consent Management](../src/Sections/Humans.Consent/Docs/features/legal-documents-consent.md) | GDPR-compliant document version tracking with immutable consent audit trail, team-scoped, multi-language, configurable through admin GUI |
| [MailerLite Audience Debug Screen](../src/Sections/Humans.MailerLite/Docs/features/audience-debug-screen.md) | Per-audience debug screen previewing exactly what the next MailerLite `Sync` would apply, so admins can spot anomalies before pulling the trigger |
| [Notification Inbox](../src/Sections/Humans.Notifications/Docs/features/notification-inbox.md) | Central "what needs my attention" view with shared resolution for group-targeted notifications |
| [Onboarding Pipeline](../src/Sections/Humans.Onboarding/Docs/features/onboarding-pipeline.md) | End-to-end signup-to-active-membership journey with parallel legal-consent and Consent Coordinator review tracks |
| [Volunteer Status](../src/Sections/Humans.Onboarding/Docs/features/volunteer-status.md) | App access is the stored `UserState` (set by entering a legal name); the system-managed Volunteers team is reconciled separately on name + consents, with the consent check an audit annotation, not an access gate |
| [Burner-Name Collision Warning](../src/Sections/Humans.Users/Docs/features/burner-name-collision-warning.md) | Live edit-profile warning telling a user how many other humans already use the burner name they are typing, so they can pick a more distinguishable one |
| [Communication Preferences](../src/Sections/Humans.Users/Docs/features/communication-preferences.md) | GDPR/CAN-SPAM-compliant per-category email and in-app alert opt-in/opt-out controls |
| [Feature 29: Contact Accounts](../src/Sections/Humans.Users/Docs/features/contact-accounts.md) | Pre-provisioned Identity users for external mailing-list, ticket-purchase, and admin-entered contacts |
| [Contact Fields with Granular Visibility](../src/Sections/Humans.Users/Docs/features/contact-fields.md) | Per-field contact information sharing (Signal, Telegram, WhatsApp, Discord, phone) with per-context privacy levels |
| [Dietary & Medical Nudge Modal](../src/Sections/Humans.Users/Docs/features/dietary-medical-nudge.md) | Dashboard nudge collecting dietary, allergy, and medical info once a human has a qualifying 6+ hour cantina-fed shift signup |
| [Email Management](../src/Sections/Humans.Users/Docs/features/preferred-email.md) | Multiple email addresses per user with per-email verification, visibility, and notification targeting |
| [Profile Pictures & Birthday Calendar](../src/Sections/Humans.Users/Docs/features/profile-pictures-birthdays.md) | Custom avatar uploads superseding Google OAuth photos, plus a community birthday calendar |
| [Profile Search Detail (Picker Row Enrichment)](../src/Sections/Humans.Users/Docs/features/profile-search-detail.md) | Second-line context plus avatar in the shared human picker so Playa-name collisions can be disambiguated |
| [Profiles](../src/Sections/Humans.Users/Docs/features/profiles.md) | Personal information management distinguishing legal names from public "burner names" with location data for event planning |
| [Public Coordinator Popover](../src/Sections/Humans.Users/Docs/features/public-coordinator-popover.md) | Anonymous-visible reduced popover on public team pages surfacing only avatar, BurnerName, and coordinator role labels via an `AllowAnonymous` `/Profile/{id}/PublicPopover` endpoint |
| [Scanner — Barcode (Phase 1)](../src/Sections/Humans.Scanner/Docs/features/scanner-barcode.md) | Camera-based in-app barcode/QR decoder for staff to inspect TicketTailor ticket stubs (decode only, no check-in) |
| [Scanner — Gate Terminal Login](../src/Sections/Humans.Scanner/Docs/features/gate-terminal-login.md) | Shared gate-terminal account (well-known GUID, no email or roles) so any shift volunteer can operate the ticket-lookup kiosk without tying the device to a personal login or granting admin powers |
| [Coordinator Roles](../src/Sections/Humans.Shifts/Docs/features/coordinator-roles.md) | Consent Coordinator and Volunteer Coordinator roles adding structured safety and facilitation gates to onboarding |
| [Day Filter](../src/Sections/Humans.Shifts/Docs/features/day-filter.md) | Dropdown on `/Shifts` letting volunteers jump to a specific calendar day, filtering the shift list and surfacing open shifts first |
| [Department Coverage Pies](../src/Sections/Humans.Shifts/Docs/features/department-coverage-pies.md) | A row of conic-gradient discs above `/Shifts`, one per department, showing percentage-filled and acting as a clickable department filter |
| [Email a Rota](../src/Sections/Humans.Shifts/Docs/features/email-a-rota.md) | Bulk-to-rota coordinator messaging that preserves per-recipient personalization (each recipient's own shift list on the rota) over the existing outbox/audit/opt-out infrastructure |
| [Post-Event Stats Dashboard](../src/Sections/Humans.Shifts/Docs/features/post-event-stats.md) | Post-event no-show and completion-rate breakdown by department and period for coordinators and admins |
| [Shift Management](../src/Sections/Humans.Shifts/Docs/features/shift-management.md) | Multi-day event shift configuration, signup workflows, urgency scoring, and coordinator tooling |
| [Shift Preference Wizard](../src/Sections/Humans.Shifts/Docs/features/shift-preference-wizard.md) | Guided 3-step mobile-friendly wizard collecting skills, work style, and languages for shift matching |
| [Shift Signup Visibility](../src/Sections/Humans.Shifts/Docs/features/shift-signup-visibility.md) | Visibility rules letting coordinators and admins see who has signed up for upcoming shifts |
| [Workload Dashboard](../src/Sections/Humans.Shifts/Docs/features/workload-dashboard.md) | Cross-event "who is doing how much" view sliced three ways to spot burnout candidates, idle volunteers, and under-staffed departments |
| [Store](../src/Sections/Humans.Store/Docs/features/Store-feature.md) | Per-camp catalog ordering, multi-method payments, and consolidated Holded factura issuance for Camp Lead purchases |
| [Grid Survey Questions](../src/Sections/Humans.Surveys/Docs/features/grid-questions.md) | Grid question type letting authors ask one compact question across labelled rows and a shared set of labelled columns, with single- or multi-select per row |
| [Survey Information Blocks](../src/Sections/Humans.Surveys/Docs/features/survey-information-blocks.md) | Non-question Information items authors can place inline in a survey to show respondents supporting context (e.g. forecasts) right before the question that needs it |
| [Survey Intro Markdown](../src/Sections/Humans.Surveys/Docs/features/survey-intro-markdown.md) | Markdown-rendered respondent intro copy so authored paragraphs, emphasis, links, and lists survive instead of collapsing to a single HTML-encoded line |
| [Custom Survey Invitation Email Copy](../src/Sections/Humans.Surveys/Docs/features/survey-invitation-email-copy.md) | Optional per-survey, per-language invitation email subject and Markdown message authored in the builder, layered inside the standard branded invitation frame |
| [Survey Preview and Preview Email](../src/Sections/Humans.Surveys/Docs/features/survey-preview.md) | In-browser preview of the respondent experience and a preview invitation email, so authors can verify a survey before opening it or sending to a real audience |
| [Hidden Teams](../src/Sections/Humans.Teams/Docs/features/hidden-teams.md) | Privacy-sensitive teams invisible to non-admin users for campaign targeting (e.g., low-income ticket programs) |
| [Teams & Working Groups](../src/Sections/Humans.Teams/Docs/features/Teams-feature.md) | Self-organizing working groups with optional department hierarchy and three system-managed teams tracking key organizational roles |
| [Test System Reliability](testing/test-system-reliability.md) | Multi-phase rebuild of the test setup so CI catches what local sees, integration tests survive concurrent runs, and "pre-existing failures on main" stops being said |
| [Event Participation Tracking](../src/Sections/Humans.Tickets/Docs/features/event-participation.md) | Yearly event participation status per human, including self-service opt-out and ticket-driven auto-tracking |
| [Ticket Transfer](../src/Sections/Humans.Tickets/Docs/features/ticket-transfer.md) | Sender-initiated transfer of a ticket to another verified member, processed by the ticket team either via automated void+reissue through the TicketTailor API or manually in the vendor dashboard |
| [Ticket Vendor Integration](../src/Sections/Humans.Tickets/Docs/features/ticket-vendor-integration.md) | Dedicated Tickets section with TicketTailor sync, sales dashboard, revenue metrics, and attendee tracking |
| [User Search Overhaul](../src/Sections/Humans.Users/Docs/features/user-search-overhaul.md) | Rework of human-name matching so search hits resolved burner names and legal names with accent folding and token splitting, while excluding board/private profiles |

## Section Invariants

Terse, authoritative invariant docs for each major section: concepts, data model, actors and roles, hard rules, negative-access rules, triggers, cross-section dependencies, and architecture/migration status.

| Document | Description |
|----------|-------------|
| [Admin Shell](sections/admin-shell.md) | Frame-only section providing the shared admin sidebar, breadcrumb, and dashboard skeleton — owns no tables |
| [Agent](../src/Sections/Humans.Agent/Docs/Agent.md) | Conversational helper backed by Anthropic Claude, available to authenticated consented users when `AgentSettings.Enabled = true` |
| [Audit Log](../src/Sections/Humans.AuditLog/Docs/AuditLog.md) | Append-only system audit trail capturing actor, action, entity, and timestamp; enforced append-only per design-rules §12 |
| [Auth](../src/Sections/Humans.Auth/Docs/Auth.md) | Temporal role assignments, magic-link login/signup, and claims transformation |
| [Budget](../src/Sections/Humans.Budget/Docs/Budget.md) | Fiscal-year budgets (Draft/Active/Closed) with groups, categories, line items, and an append-only audit log |
| [Calendar](../src/Sections/Humans.Calendar/Docs/Calendar.md) | Per-team community calendar with one-off and recurring events plus per-occurrence overrides and cancellations |
| [Campaigns](../src/Sections/Humans.Campaigns/Docs/Campaigns.md) | Bulk code-distribution campaigns: codes imported or generated, assigned to humans, delivered via email waves |
| [Camps](../src/Sections/Humans.Camps/Docs/Camps.md) | Themed community camps (Barrios) with per-year season registrations, leads, images, and renaming history |
| [Cantina](../src/Sections/Humans.Cantina/Docs/Cantina.md) | Read-only weekly roster surface for the food-service team — who is on site each day and what they can/cannot eat; composes over Shifts, owns no tables |
| [City Planning](../src/Sections/Humans.CityPlanning/Docs/CityPlanning.md) | Interactive map surface with three screens: read-only overview, barrio polygon editing, and container placement |
| [Containers](../src/Sections/Humans.Containers/Docs/Containers.md) | Physical shipping containers managed per-barrio or at org level, placed on the City Planning map |
| [Debug](../src/Sections/Humans.Debug/Docs/Debug.md) | Developer/diagnostics section: admin-only pages exposing operational insight (client demographics, request health) that no domain section owns — owns no tables |
| [Development](../src/Sections/Humans.Development/Docs/Development.md) | Dev-only tooling: persona sign-in and fixture seeding, never reachable in Production — owns no tables |
| [Early Entry](../src/Sections/Humans.EarlyEntry/Docs/EarlyEntry.md) | Cross-source Early Entry: who may enter the site before gates open, on which day, and because of what — owns no data, fans out over every contributing section |
| [Email](../src/Sections/Humans.Email/Docs/Email.md) | Transactional email outbox: queue, render, deliver, retry, and pause/resume — backs campaign sends, onboarding, shift, and feedback emails |
| [Events](../src/Sections/Humans.Events/Docs/Events.md) | Event programming: submission, moderation, browsing, export, and preference management for festival events |
| [Expenses](../src/Sections/Humans.Expenses/Docs/Expenses.md) | Expense reports submitted by members and approved by Finance Admin; approval books into Holded async, and paid/unpaid status is read back from the member's Holded creditor ledger rather than stamped on the report — payment itself happens externally, with no SEPA-file generation in the app |
| [Feedback](../src/Sections/Humans.Feedback/Docs/Feedback.md) | Retired — closed to new reports and Admin-only; the historical archive of in-app reports (bugs, feature requests, questions) with screenshots and conversation threads |
| [Finance](../src/Sections/Humans.Finance/Docs/Finance.md) | Treasurer's reality side of money — actuals, reconciliation, and treasurer-facing operational data sharing keys with Budget |
| [Gate](../src/Sections/Humans.Gate/Docs/Gate.md) | Gate ticket scanning that decides entry at the event door and writes the durable admission record — distinct from the read-only Scanner section, which must never check anyone in |
| [Gdpr](../src/Sections/Humans.Gdpr/Docs/Gdpr.md) | GDPR Article 15 export orchestrator — fans out to every section that holds personal data and merges the slices into the JSON the human downloads; owns no tables and no pages |
| [Google Integration](../src/Sections/Humans.GoogleIntegration/Docs/GoogleIntegration.md) | Shared-Drive-only sync for Drive folders, Groups, and Workspace accounts with reconciliation and Drive-activity monitoring |
| [Governance](../src/Sections/Humans.Governance/Docs/Governance.md) | Colaborador and Asociado tier applications, Board voting workflow, and term lifecycle (not volunteer onboarding) |
| [Guide](../src/Sections/Humans.Guide/Docs/Guide.md) | The in-app `/Guide` renderer for `docs/guide/` markdown with role-scoped block filtering |
| [Holded Connector](../src/Sections/Humans.Holded/Docs/Holded-connector.md) | Thin typed-`HttpClient` surface to the Holded accounting API, shared by Expenses (purchase documents) and Finance (ledger reconciliation). Owned by the Holded section; owns no tables |
| [Holded](../src/Sections/Humans.Holded/Docs/Holded.md) | The ledger mirror: a local, re-derivable copy of Holded's daybook and chart of accounts, plus the sync that maintains it and the `/Holded` admin screen |
| [Issues](../src/Sections/Humans.Issues/Docs/Issues.md) | In-app issue tracker (bugs, features, questions) with screenshots, role-routed triage, and a reporter↔handler conversation thread |
| [Consent](../src/Sections/Humans.Consent/Docs/Consent.md) | GitHub-synced legal documents, per-version append-only consent records, and the Consent Coordinator audit/review queue |
| [MailerLite](../src/Sections/Humans.MailerLite/Docs/MailerLite.md) | Humans ↔ MailerLite synchronisation: inbound import and outbound audience management |
| [Monitor](../src/Sections/Humans.Monitor/Docs/Monitor.md) | Operator-facing monitoring of the Google Workspace estate: detect unrequested permission changes, and show the Google-sync audit trail for one resource or one human |
| [Notifications](../src/Sections/Humans.Notifications/Docs/Notifications.md) | In-app notification fan-out (stored events plus per-user inbox) and live meter counts (computed) |
| [Onboarding](../src/Sections/Humans.Onboarding/Docs/Onboarding.md) | Pure orchestrator over Profiles, Consent, Teams, and Governance — owns no tables |
| [Scanner](../src/Sections/Humans.Scanner/Docs/Scanner.md) | In-browser camera tools for barcode decode (`/Scanner/Barcode`) and read-only ticket lookup (`/Scanner/Tickets`); no owned tables |
| [Search](../src/Sections/Humans.Search/Docs/Search.md) | Orchestrator behind the global `/Search` page — fans out to five sections' read surfaces, scores each independently, owns no tables |
| [Shifts](../src/Sections/Humans.Shifts/Docs/Shifts.md) | Event shifts, rotas, signups, range blocks, event settings, general availability, and per-event volunteer profiles |
| [Store](../src/Sections/Humans.Store/Docs/Store.md) | Per-camp catalog ordering, multi-method payments, and consolidated Holded factura issuance for Camp Lead purchases |
| [Stripe](../src/Sections/Humans.Stripe/Docs/Stripe.md) | The payments connector — Checkout Session creation, webhook signature verification, fee lookups, boot-time key probes; owns no tables and no UI |
| [Survey](../src/Sections/Humans.Surveys/Docs/Surveys.md) | First-party, GDPR-compliant surveys: author typed/branching multi-language surveys, send tokenised email invitations to a resolved audience, collect responses across three anonymity tiers (invite link or public slug), and read results in-app, via CSV/JSON export, or a key-authed analysis API |
| [Teams](../src/Sections/Humans.Teams/Docs/Teams.md) | Departments and sub-teams, join requests, role definitions, team pages, and linked Google resources |
| [Tickets](../src/Sections/Humans.Tickets/Docs/Tickets.md) | External ticket vendor sync (orders + attendees), Stripe-fee enrichment, auto-matching by email, event-participation derivation |
| [Tour](../src/Sections/Humans.Tour/Docs/Tour.md) | Public marketing page — what Humans is, in plain language, for visitors evaluating the platform |
| [Users](../src/Sections/Humans.Users/Docs/Users.md) | Merges the old Users and Profiles docs: the User/Identity aggregate (provisioning, unsubscribe, event participation) plus per-human personal data (profile, contact fields, emails, communication preferences) |

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
| [Events](guide/Events.md) | Browse the festival programme and submit your own events; moderators approve submissions into the public guide |
| [Expenses](guide/Expenses.md) | Submit expense reports and track reimbursement; FinanceAdmin reviews |
| [Feedback](guide/Feedback.md) | Retired predecessor to Issues — Admins triage the historical queue; report bugs and ideas via `/Issues` instead |
| [Google Integration](guide/GoogleIntegration.md) | Wires teams up to Google Workspace: Group, Shared Drive, Workspace accounts, and Drive activity monitoring |
| [Governance](guide/Governance.md) | Tier applications, Board votes, and coordinator/admin role assignments — not Volunteer onboarding |
| [Consent](guide/LegalAndConsent.md) | Documents you sign, GDPR Article 15 export, and Article 17 deletion |
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
| [Database Restore Runbook](database-restore-runbook.md) | Restoring Postgres from a backup, the automatic pre-deploy snapshot, and the event deploy-freeze policy |
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
| [plans/](plans/) | Programme-level plans and audits — the Q3 transition gate ladder, the G0 section audit, the demolition and frozen-section inventories, the section dependency DAG, and the cross-section FK-cut inventory |
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
| [Project Rules Catalog](../memory/INDEX.md) | Atomic rules (one per file under `memory/<bucket>/`). `architecture/coding-rules.md` is now a stub redirecting here. |
| [Code Review Rules](architecture/code-review-rules.md) | Hard-reject rules for code review |
| [Service / Data Access Map](architecture/service-data-access-map.md) | Per-service table access inventory |
| [Code Analysis](architecture/code-analysis.md) | Analyzers, ReSharper configuration |
| [Maintenance Log](architecture/maintenance-log.md) | Recurring maintenance tasks and last-run dates |

See the [root CLAUDE.md](../CLAUDE.md) for build commands and project overview.
