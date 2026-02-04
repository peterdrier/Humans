# Nobodies Profiles

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
dotnet run --project src/Profiles.Web
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
│   ├── Profiles.Domain/           # Entities, enums, value objects
│   ├── Profiles.Application/      # Use cases, DTOs, interfaces
│   ├── Profiles.Infrastructure/   # EF Core, external services
│   └── Profiles.Web/              # ASP.NET Core, controllers, views
├── tests/
│   ├── Profiles.Domain.Tests/
│   ├── Profiles.Application.Tests/
│   └── Profiles.Integration.Tests/
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

## License

MIT
