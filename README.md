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

## License

AGPL-3.0
