# Nobodies Humans

Membership management system for Nobodies Collective, a Spanish nonprofit organization.

## Features

- **Member Management**: Profile management, role assignments, contact fields, birthday tracking, and membership status
- **Applications & Governance**: Colaborador/Asociado applications, Board voting, and temporal governance roles
- **Volunteer Operations**: Shift browsing, urgent staffing, shift signups, coordinator tooling, and camp registration
- **Compliance**: GDPR consent tracking, legal document sync from GitHub, re-consent workflows, and audit logs
- **Integrations**: Google OAuth, Google Workspace provisioning, Drive and Groups sync, ticket vendor sync, and campaign codes
- **Admin & Finance**: Member administration, feedback triage, email outbox, budget management, and reporting
- **Observability**: OpenTelemetry traces/metrics, Prometheus endpoint, health checks, and structured logging

## Technology Stack

- .NET 10 SDK
- PostgreSQL with EF Core
- ASP.NET Core Identity with Google OAuth
- Hangfire for background jobs
- NodaTime for date/time handling
- Stateless for state machine
- OpenTelemetry + Serilog for observability

## Getting Started

### Prerequisites

- .NET 10 SDK
- PostgreSQL 16+
- Docker (optional, for full stack)

### Development Setup

1. Clone the repository
2. Copy `.env.example` to `.env` and configure Google OAuth credentials
3. Run PostgreSQL locally or use Docker:

```bash
docker-compose up -d db
```

4. Run the web app. It applies pending migrations on startup:

```bash
dotnet run --project src/Humans.Web
```

For development with live reload (Razor views update on browser refresh):

```bash
dotnet watch --project src/Humans.Web
```

### Full Stack with Docker

```bash
docker-compose up -d
```

This starts:
- Application (port 5000)
- PostgreSQL (port 5432)

## Documentation

- Full documentation index: [docs/README.md](docs/README.md)
- Historical repo stats: [docs/development-stats.md](docs/development-stats.md)

## Project Structure

```
humans/
├── src/
│   ├── Humans.Domain/           # Entities, enums, value objects
│   ├── Humans.Application/      # Use cases, DTOs, interfaces
│   ├── Humans.Infrastructure/   # EF Core, external services
│   └── Humans.Web/              # ASP.NET Core, controllers, views
├── tests/
│   ├── Humans.Domain.Tests/
│   ├── Humans.Application.Tests/
│   └── Humans.Integration.Tests/
└── docker-compose.yml
```

## Configuration

### Required Environment Variables

| Variable | Description |
|----------|-------------|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string |
| `Authentication__Google__ClientId` | Google OAuth client ID |
| `Authentication__Google__ClientSecret` | Google OAuth client secret |

### Optional Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `Email__Username` / `Email__Password` | empty | SMTP credentials for outbound mail |
| `GoogleWorkspace__ServiceAccountKeyJson` | empty | Google Workspace service account key |
| `GitHub__AccessToken` | empty | GitHub token for legal document sync |
| `GoogleMaps__ApiKey` | empty | Maps API key for profile locations and maps |
| `TicketVendor__Provider` | `TicketTailor` | Ticket vendor integration provider |
| `OpenTelemetry__OtlpEndpoint` | `http://localhost:4317` | OTLP collector endpoint for tracing |

## API Endpoints

| Endpoint | Description |
|----------|-------------|
| `/health/live` | Liveness probe |
| `/health/ready` | Readiness probe |
| `/metrics` | Prometheus metrics |
| `/hangfire` | Hangfire dashboard (admin only) |
| `/api/version` | Build metadata and commit info |

## Legal Documents

Legal documents are sourced from [nobodies-collective/legal](https://github.com/nobodies-collective/legal) on GitHub. Spanish content is canonical and legally binding; English translations are provided for convenience.

When legal documents are updated:
1. Members are notified and must re-consent
2. Members without valid consent are marked as Inactive
3. Access is restricted until re-consent is provided

## Selected Milestones

The project grew quickly from the initial member portal into a broader operational system. Selected milestones:

| Date | Milestone |
|------|-----------|
| **Feb 4** | Project inception — member profiles, admin dashboard, Google Places location autocomplete |
| **Feb 5** | Self-organizing teams, GDPR compliance suite (consent tracking, data export, right-to-deletion), legal document sync from GitHub, Google Workspace integration |
| **Feb 7** | Google resource management GUI, nightly Drive/Groups reconciliation job, comprehensive audit logging |
| **Feb 8** | Multi-language support across 5 locales (EN, ES, DE, IT, FR) with language chooser |
| **Feb 9** | Profile picture uploads with multi-format conversion, Google sync audit trail with per-resource views |
| **Feb 10** | Visual rebrand to "Humans" with Da Vinci Renaissance identity; legal document redesign with team-scoped, multi-language admin CRUD |
| **Feb 11** | Interactive volunteer map with Google Maps, consolidated contact fields, birthday tracking |
| **Feb 15** | Custom Prometheus metrics for membership and compliance, email preview system, outbox-backed Google sync dispatcher |
| **Feb 18** | Onboarding redesign epic — membership tiers (Colaborador/Asociado), consent-check gating, Board voting workflow, term lifecycle management |
| **Mar 5** | Service-layer architecture extraction across all controllers, E.164 phone validation, admin sorting and filtering |
| **Mar 9** | Google Groups via Cloud Identity API, configurable sync modes (Manual/Preview/Auto), enhanced map with avatar popups and location clustering |
| **Mar 11** | Team role slots — named positions within teams with markdown descriptions and Lead auto-provisioning; in-memory caching layer |
| **Mar 13** | Camps — full event camp registration system with approval workflow, co-leads, and public JSON API |
| **Mar 14** | Email outbox with retry and crash recovery, campaign system with CSV import and discount code distribution |
| **Mar 15** | Ticket vendor integration (TicketTailor sync), membership status partition model, admin log viewer |
| **Mar 16** | Shift management foundation and team hierarchy/coordinator workflows |
| **Mar 18** | Feedback system and shift sign-up visibility work |
| **Mar 19** | Volunteer management refinements and ticket/vendor follow-up changes |
| **Mar 22** | Playwright E2E smoke-test coverage and section-based test structure |
| **Mar 23** | Auth/Google sync hardening, feedback triage workflow, and session polish |
| **Mar 24** | Feedback upgrade rollout and communication preference work |
| **Mar 27** | Budget phase 1 foundation and related finance scaffolding |
| **Mar 30-31** | Documentation refresh, repo stats update, and docs index expansion |
| **Apr 1** | Notification inbox — full in-app notification center; staff directory and About pages |
| **Apr 2** | Duplicate account detection and resolution workflow; community-focused homepage redesign |
| **Apr 3** | Authorization overhaul foundation — auth inventory, Playwright auth test suite, sub-team manager role |
| **Apr 4** | Catalan (ca) as sixth locale; communication preferences redesign; consolidated Finance view; authorization policy rollout across controllers |
| **Apr 6** | City Planning — interactive polygon map for laying out barrios; EF query monitoring dashboard |
| **Apr 7** | Resource-based authorization with scoped coordinator and camp-lead permissions |
| **Apr 8** | Profileless (guest) account support — guest dashboard, GDPR-safe deletion; Google sync hardening |
| **Apr 10** | Full i18n pass across user-facing views in 5 locales; GDPR export covering all user-linked entities; breadcrumbs on 29 dead-end pages |
| **Apr 13** | Service-ownership migration continued (CityPlanning, Shifts, Tickets/Budget/Campaigns); event participation tracking with self-service opt-out |
| **Apr 15** | Architecture docs reorganization; Governance migrated to the repository/store/decorator pattern |
| **Apr 21-22** | Clean Architecture mega-migration — 20+ services extracted to the Application layer with the repository/store/decorator pattern across every section; Volunteer Coordinator dashboard |
| **Apr 25** | In-app user Guide section; per-season camp membership; xUnit v3 upgrade |
| **Apr 26** | Ticket scanner phase 1 — in-browser barcode decode; per-camp role assignments; City Planning GeoJSON bulk import |
| **Apr 29** | Admin shell and left-nav redesign; filesystem-backed profile pictures; shifts browse UX overhaul with calendar availability picker |
| **Apr 30** | Volunteer admission ungated from Consent Coordinator approval; email-identity decoupling begins |
| **May 2** | Issues section launched (succeeds Feedback); account-merge fold-into-target redesign; hidden teams split into an admin-only section |
| **May 3** | Barrios section — lead listings, role slots, lead-status tracking; Agent phase 1 — in-app AI assistant; post-purchase /Welcome landing |
| **May 4** | Store section — catalog admin CRUD; 2FA status and backup codes on Accounts admin |
| **May 9** | Email-identity decoupling completed — grid, link surface, admin parity; global person search consolidated |
| **May 10** | Ticket transfer wizard between humans; global search across humans, teams, camps, and shifts; unified `<vc:human>` view component |
| **May 11** | Expenses and Holded sections launched (budget actuals, category spend, SEPA batches); Roslyn analyzers replace IL-scan architecture tests |
| **May 12** | Mailer section — MailerLite import, outbound sends, audience framework |
| **May 13** | UserInfo cached read-model spanning User and Profile; Google Group membership sync orchestrator |
| **May 15** | Event Guide — submission, moderation, browse, schedule, and admin; City Planning container placement map |
| **May 16-17** | Cache migration sprint — every major section served from in-memory decorated caches; linked OAuth accounts dashboard; shift workload aggregations |
| **May 18** | On-site ticket check-in view; Agent admin status page (usage, spend, refusals) |
| **May 19** | CampLead folded into role assignments; Marketing/HasShift/HasTicket mailer audiences |
| **May 22-23** | Cross-section read boundaries (`I<Section>ServiceRead`) introduced; burner + legal name model replaces DisplayName; shift availability calendar |
| **May 25** | Architecture ratchet baselines converted to compile-time Roslyn analyzers; dietary and medical data moved to Profile |
| **May 26** | Holded expense actuals matching and creditor balances; Early Entry cross-source roster with ticket stub self-view |
| **May 27** | Repository consolidation across shifts, camps, profile, and user-email; WYSIWYG Markdown editor |
| **May 29-30** | Surface-reduction sweep across Budget, Google, Users, Email, and Tickets sections; admin nav realigned to section structure |
| **Jun 1** | Cross-section callers rerouted through read interfaces; Teams as an Early Entry provider |
| **Jun 4** | Store — Stripe payment reconciliation admin and order repricing with audit trail; per-day instant shift signup toggle |
| **Jun 5** | Barrios compliance page redesigned as a role-staffing matrix |
| **Jun 6** | Account merge consolidated into one ordered engine and admin surface; expense travel lines (mileage/per-diem) with personal IOU view |
| **Jun 7** | People/team/camp search made relevance-ranked, uncapped, and cache-only |
| **Jun 9-10** | Survey section — first-party GDPR-compliant surveys; personal iCal feed for shifts and events; gate-terminal kiosk login for the ticket scanner |
| **Jun 11** | Stripe async-payment checkout state machine; post-event shift stats dashboard; per-occurrence event favourites |
| **Jun 12** | Ticket scanner manual barcode entry; 3,700-string i18n sweep across public pages and enums |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for workflow and guidelines.

## License

AGPL-3.0
