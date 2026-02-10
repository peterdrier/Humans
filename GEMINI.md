# Gemini Project Context: Profiles.net

This document provides context for the Profiles.net project, a membership management system for a nonprofit organization.

## Project Overview

Profiles.net is a full-stack ASP.NET Core web application built on .NET 10. It manages member profiles, membership applications, legal document consent, and team assignments. It's designed with a clean architecture, separating domain logic, application use cases, and infrastructure concerns.

### Key Technologies

*   **Backend**: ASP.NET Core, C#
*   **Database**: PostgreSQL with Entity Framework Core
*   **Date/Time**: NodaTime for robust time zone handling
*   **Authentication**: ASP.NET Core Identity with Google OAuth
*   **Background Jobs**: Hangfire
*   **State Machine**: Stateless
*   **Observability**: OpenTelemetry (Metrics & Tracing) and Serilog (Logging)
*   **Containerization**: Docker and Docker Compose

### Architecture

The project follows a clean architecture pattern, organized into the following layers:

*   `Humans.Domain`: Contains core business entities, value objects, and enums. It has no external dependencies.
*   `Humans.Application`: Implements application logic and use cases. It defines interfaces for infrastructure concerns (like repositories and external services) and depends on the Domain layer.
*   `Humans.Infrastructure`: Provides concrete implementations for the interfaces defined in the Application layer, including the EF Core `DbContext`, repositories, and services for interacting with external systems (Google Workspace, SMTP, etc.).
*   `Humans.Web`: The main ASP.NET Core web application, containing controllers, views, and the application's entry point (`Program.cs`). It depends on all other layers.
*   `tests`: Contains unit and integration tests for the solution.

## Building and Running

### Prerequisites

*   .NET 10 SDK
*   PostgreSQL 16+
*   Docker (for the full stack or individual services)

### Local Development (CLI)

1.  **Configure Environment**: Copy `.env.example` to `.env` and fill in the required variables, especially `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`.
2.  **Start Database**: `docker-compose up -d db`
3.  **Restore & Build**: `dotnet build Humans.slnx`
4.  **Run Application**: `dotnet run --project src/Humans.Web/Humans.Web.csproj`
    *   The application will be available at `https://localhost:5001` or `http://localhost:5000`.
    *   Database migrations are automatically applied on startup in the `Development` environment.

### Full Stack (Docker Compose)

1.  **Configure Environment**: Copy `.env.example` to `.env` and provide the Google OAuth credentials.
2.  **Start Services**: `docker-compose up -d`
3.  **Stop Services**: `docker-compose down`

This command starts the following services:
*   `app`: The web application (port 5000)
*   `db`: PostgreSQL database (port 5432)
*   `redis`: Redis for Hangfire (port 6379)
*   `otel-collector`: OpenTelemetry Collector (port 4317)
*   `prometheus`: Prometheus for metrics (port 9091)
*   `grafana`: Grafana for dashboards (port 3000)

## Key Commands

The continuous integration pipeline (`.github/workflows/build.yml`) defines the canonical commands for working with the project.

*   **Restore Dependencies**:
    ```bash
    dotnet restore Humans.slnx
    ```
*   **Build Solution (Release)**:
    ```bash
    dotnet build Humans.slnx --no-restore --configuration Release
    ```
*   **Run Tests**:
    ```bash
    dotnet test Humans.slnx --no-build --configuration Release
    ```
*   **Check Code Formatting**:
    ```bash
    dotnet format Humans.slnx --verify-no-changes
    ```

## Development Conventions

*   **Coding Style**: The project uses the standard C# coding conventions enforced by the `.editorconfig` file. Use the `dotnet format` command to check and apply formatting.
*   **Database Migrations**: Migrations are managed with EF Core. To add a new migration, use the following command from the project root:
    ```bash
    dotnet ef migrations add <MigrationName> --project src/Humans.Infrastructure --startup-project src/Humans.Web
    ```
*   **Secrets**: User secrets are used for local development. Google OAuth credentials should be stored in your user secrets, not in `appsettings.json`. The `.env` file is used for Docker Compose configuration.
*   **Observability**: The application is instrumented with OpenTelemetry. When running locally or via Docker Compose, traces are sent to an OpenTelemetry Collector, and metrics are exposed on the `/metrics` endpoint for Prometheus to scrape.
*   **Health Checks**: A detailed health check endpoint is available at `/health` and `/health/ready`, which confirms the status of the database and other critical dependencies. The `/health/live` endpoint can be used for simple liveness probes.
