# Nobodies Humans

Membership management system for Nobodies Collective, a Spanish nonprofit organization.

## Features

- **Members**: Guided Volunteer onboarding, profiles with burner and legal names, contact fields with per-field visibility, role assignments, and account merging
- **Governance**: Colaborador/Asociado applications with Board voting, binding assembly votes, and the working-groups register
- **Volunteer Operations**: Teams and sub-teams, shifts and rotas, Early Entry, cantina rosters, camp (barrio) registration, City Planning and container maps, and rideshare
- **Event & Tickets**: Ticket vendor sync and transfers, ticket scanner and gate admissions, campaign codes and grants, event guide, community calendar with iCal feeds, and a Stripe-backed store
- **Finance**: Budgets, expense reports, pre-approvals, Holded accounting sync, and SEPA payouts
- **Compliance**: GDPR consent tracking, data export and erasure, legal document sync from GitHub, re-consent workflows, and audit logs
- **Communication & Support**: Email outbox, in-app notifications, MailerLite audiences, surveys, in-app Issues, an AI help agent, and the in-app Guide
- **Integrations**: Google OAuth and magic-link sign-in, Google Workspace provisioning, and Drive and Groups sync
- **Observability**: OpenTelemetry traces/metrics, Prometheus endpoint, health checks, and structured logging

## Technology Stack

- .NET 10 SDK
- PostgreSQL with EF Core
- ASP.NET Core Identity with Google OAuth and email magic links
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
2. Run PostgreSQL locally or use Docker. Compose reads `.env` (copy `.env.example`) and won't start without `POSTGRES_PASSWORD`; the default connection string in `appsettings.json` expects the password `humans`:

```bash
docker-compose up -d db
```

3. Set secrets for `dotnet run` with user secrets or environment variables (see [Configuration](#configuration)) — `dotnet run` does not read `.env`. In Development, dev login (`DevAuth:Enabled`) adds persona sign-in buttons to `/Account/Login`.

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
│   ├── Humans.Base/             # Base — primitives, shared contracts, EF/hosting seam
│   ├── Humans.Analyzers/        # Roslyn analyzers enforcing the architecture rules
│   ├── Sections/                # Humans.<Section>[.Contracts] — one project per section
│   └── Humans.Web/              # Shell: ASP.NET Core host, layouts, composition root
├── tests/
│   ├── Humans.Base.Tests/
│   ├── Humans.<Section>.Tests/  # one per section
│   ├── Humans.Web.Tests/        # architecture tests
│   ├── Humans.Integration.Tests/ # opt-in: HUMANS_INTEGRATION_TESTS=1
│   ├── Humans.Testing/          # shared test helpers
│   └── e2e/                     # Playwright smoke tests
└── docker-compose.yml
```

## Configuration

Defaults live in `src/Humans.Web/appsettings.json`; override them with user secrets or environment variables (`Section__Key`). Board and Admin can see every key the running app reads, and whether it is set, at `/Debug/Configuration`.

### Required

| Setting | Description |
|---------|-------------|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string |
| `Authentication__Google__ClientId` | Google OAuth client ID |
| `Authentication__Google__ClientSecret` | Google OAuth client secret |

### Optional

| Setting | Description |
|---------|-------------|
| `Email__Username` / `Email__Password` | SMTP credentials for outbound mail (host and sender are in `appsettings.json`) |
| `GoogleMaps__ApiKey` | Maps and Places for profile locations and maps |
| `GoogleWorkspace__ServiceAccountKeyJson` or `__ServiceAccountKeyPath` | Service account for Workspace provisioning and Drive/Groups sync |
| `GitHub__AccessToken` | Raises the GitHub API rate limit for legal document sync |
| `Anthropic__ApiKey` | In-app AI help agent |
| `TICKET_VENDOR_API_KEY` | Ticket vendor API key (`TicketVendor__*` sets provider and event) |
| `STRIPE_TICKETS_KEY`, `STRIPE_STORE_KEY`, `STRIPE_STORE_WEBHOOK_SECRET` | Stripe keys for tickets and the store |
| `HOLDED_API_KEY_V2` | Holded accounting API |
| `MAILERLITE_API_KEY` or `MailerLite__ApiKey` | MailerLite audiences |
| `ORS_API_KEY` | OpenRouteService geocoding and routes for Rideshare |
| `Sepa__*`, `SEPA_CREDITOR_IBAN` | Creditor details for SEPA payout files |
| `OpenTelemetry__OtlpEndpoint` | OTLP collector endpoint (default `http://localhost:4317`) |

### Docker Compose

`docker-compose.yml` builds the app container's environment from `.env` (template: `.env.example`):

| `.env` variable | App setting |
|-----------------|-------------|
| `POSTGRES_PASSWORD` (required) | Database password, used in `ConnectionStrings__DefaultConnection` and by the `db` container |
| `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET` | `Authentication__Google__ClientId` / `__ClientSecret` |
| `GOOGLE_MAPS_API_KEY` | `GoogleMaps__ApiKey` |
| `EMAIL_USERNAME` / `EMAIL_PASSWORD` | `Email__Username` / `Email__Password` |
| `GITHUB_ACCESS_TOKEN` | `GitHub__AccessToken` |
| `GOOGLE_SA_KEY_JSON` | `GoogleWorkspace__ServiceAccountKeyJson` |

`.env.example` also lists `ORS_API_KEY` and `GOOGLE_SA_KEY_PATH`, which `docker-compose.yml` does not pass to the app. Any other setting needs its own `environment:` entry.

## API Endpoints

| Endpoint | Description |
|----------|-------------|
| `/health` | Detailed health report |
| `/health/live` | Liveness probe |
| `/health/ready` | Readiness probe |
| `/metrics` | Prometheus metrics |
| `/hangfire` | Hangfire dashboard (Admin; read-only for Board) |
| `/api/version` | Build metadata and commit info |
| `/api/camps/{year}` | Public camp (barrio) listing for a year |

## Legal Documents

Legal documents are sourced from [nobodies-collective/legal](https://github.com/nobodies-collective/legal) on GitHub. Spanish content is canonical and legally binding; English translations are provided for convenience.

When legal documents are updated:
1. Members are notified and must re-consent
2. Members who have not re-consented when the document's grace period ends are suspended
3. Access returns once they re-consent

## Selected Milestones

The project grew quickly from the initial member portal into a broader operational system. Selected milestones, one row per month of 2026:

| Month | Milestones |
|-------|------------|
| **Feb** | Project inception (Feb 4) — member profiles, self-organizing teams, GDPR suite (consent tracking, data export, right to deletion), legal document sync from GitHub, Google Workspace provisioning with nightly Drive/Groups reconciliation, audit logging, five locales, volunteer map, the "Humans" rebrand, and the onboarding redesign with Colaborador/Asociado tiers and Board voting |
| **Mar** | Service-layer extraction; Google Groups via Cloud Identity with configurable sync modes; team role slots; Camps registration with a public JSON API; email outbox; campaigns with discount-code distribution; TicketTailor sync; shift management and coordinator workflows; feedback system; budget phase 1; Playwright E2E smoke tests |
| **Apr** | Notification inbox; duplicate-account detection; authorization overhaul with resource-based coordinator and camp-lead permissions; Catalan as sixth locale; City Planning barrio map; guest (profileless) accounts; full GDPR export; repository/store/decorator pattern across every section; in-app Guide; ticket scanner; admin shell redesign; Volunteer admission ungated from Consent Coordinator approval |
| **May** | Issues section (succeeds Feedback); Barrios lead tooling; in-app AI Agent; Store; Expenses and Holded sections; MailerLite; Event Guide; ticket transfers; global search; email-identity decoupling; in-memory cached reads across sections; burner + legal name model; Early Entry roster; Roslyn analyzers replace IL-scan architecture tests |
| **Jun** | Stripe payment reconciliation and async-payment checkout for the Store; Surveys section; personal iCal feeds; gate-terminal kiosk login and manual barcode entry; post-event shift stats; Holded creditor statements and daybook ledger; community knowledge base for the Agent; 3,700-string i18n sweep; Gate Admissions with staff PINs; automated TicketTailor void-and-reissue for transfers |
| **Jul** | Gate terminal hardening for the event — PIN audit, supervisor override, kiosk sign-out; logged-in-since survey audience |
| **Aug** | Holded API v2 section with full ledger mirror and reconciliation; SEPA payouts of member creditor balances; complete, test-enforced GDPR erasure; 14-day persistent login sessions; survey grid questions, previews and ranked-choice voting; expense invoice lines with proofs; Feedback retired in favour of Issues; `/Tour` landing page |
| **Sep** | Assembly votes; Asociado ballots stored without voter links; Rideshare section; Workgroups register; app-wide `/Settings` tabs; unified Markdown email composer; member-side reskin to the design system; accountant exports (monthly ticket income, donor list); Store deposit returns and refunds |
| **Oct** | Pre-approvals and Grants sections (with a Low Income stub); workgroup budgets, join requests and Drive access; calendar timezone and recurrence pickers; public `/Teams/Tree`; off-season member dashboard; Google Activity Log; expense-vs-Holded reconciliation report; event guide, grants, barrio seasons and rideshare scoped to an event; audited reveals for health data |

## Contributing

See [CONTRIBUTING.md](.github/CONTRIBUTING.md) for workflow and guidelines.

## License

AGPL-3.0
