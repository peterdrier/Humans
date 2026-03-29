# Nobodies Humans

Membership management system for Nobodies Collective, a Spanish nonprofit organization.

## Features

- **Member Management**: Profile management, role assignments, team memberships
- **Application Workflow**: State machine-based membership application processing
- **Legal Document Consent**: GDPR-compliant consent tracking with re-consent requirements
- **Google Integration**: Drive folder provisioning for teams and members
- **Admin Dashboard**: Member management, application review, reporting
- **Observability**: OpenTelemetry traces/metrics, Prometheus endpoint, health checks

## Technology Stack

- .NET 10 LTS
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

4. Apply migrations and run:

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
- Redis (port 6379)
- OpenTelemetry Collector (port 4317)
- Prometheus (port 9091)
- Grafana (port 3000)

## Project Structure

```
profiles.net/
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
| `OpenTelemetry__OtlpEndpoint` | `http://localhost:4317` | OTLP collector endpoint |

## API Endpoints

| Endpoint | Description |
|----------|-------------|
| `/health/live` | Liveness probe |
| `/health/ready` | Readiness probe |
| `/metrics` | Prometheus metrics |
| `/hangfire` | Hangfire dashboard (admin only) |

## Legal Documents

Legal documents are sourced from [nobodies-collective/legal](https://github.com/nobodies-collective/legal) on GitHub. Spanish content is canonical and legally binding; English translations are provided for convenience.

When legal documents are updated:
1. Members are notified and must re-consent
2. Members without valid consent are marked as Inactive
3. Access is restricted until re-consent is provided

## Development Timeline

Built from zero to production in 40 days (493 commits). Key milestones:

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

## License

AGPL-3.0
