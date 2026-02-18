# Nobodies Humans

Membership management system for Nobodies Collective (Spanish nonprofit).

## Purpose

Manage the full membership lifecycle for Nobodies Collective: volunteer applications are reviewed and approved by the Board, accepted members are provisioned into the appropriate teams and Google Workspace resources (Drive folders, Groups), and governance roles (Board, Leads, Admin) are tracked with temporal assignments. The system provides a way to organize teams logically and visually, gives Board and Admin visibility into what happens automatically on members' behalf through audit trails, and maintains GDPR compliance through consent tracking, data export, and right-to-deletion support.

## Critical: Coding Rules

**See [`.claude/CODING_RULES.md`](.claude/CODING_RULES.md) for critical rules:**
- Do not remove "unused" properties (reflection usage)
- Never rename fields in serialized objects (breaks JSON deserialization)
- JSON serialization requirements
- String comparison rules
- **NodaTime for all dates/times** (`Instant`, `LocalDate`, etc.)

## Architecture

Clean Architecture with 4 layers:
- **Domain**: Entities, enums, value objects
- **Application**: Interfaces, DTOs, use cases
- **Infrastructure**: EF Core, external services, jobs
- **Web**: Controllers, views, API

## Key Files

| File | Purpose |
|------|---------|
| `src/Humans.Web/Program.cs` | Startup, DI, middleware configuration |
| `src/Humans.Domain/Entities/` | Core domain entities |
| `src/Humans.Infrastructure/Data/HumansDbContext.cs` | EF Core DbContext |
| `src/Humans.Infrastructure/Jobs/` | Hangfire background jobs |
| `Directory.Packages.props` | Centralized NuGet package versions |
| `src/Humans.Web/Views/Home/About.cshtml` | About page with package attribution and licenses |
| `LICENSE` | AGPL-3.0 license |

## Domain Entities

See [`.claude/DATA_MODEL.md`](.claude/DATA_MODEL.md) for full data model, relationships, and serialization notes. Key entities: `User`, `Profile`, `ContactField`, `Application` (Colaborador/Asociado tier applications), `BoardVote` (transient), `RoleAssignment`, `LegalDocument`/`DocumentVersion`, `ConsentRecord` (append-only), `Team`/`TeamMember`, `GoogleResource`.

## Important: Shared Drives Only

**All Google Drive resources are on Shared Drives.** This system does NOT use regular (My Drive) folders. All Drive API calls must use `SupportsAllDrives = true`, and permission listing must include `permissionDetails` to distinguish inherited from direct permissions. Only direct permissions are managed by the system — inherited Shared Drive permissions are excluded from drift detection and sync.

**Google permission-modifying jobs are currently DISABLED** (`SystemTeamSyncJob`, `GoogleResourceReconciliationJob`). Use the manual "Sync Now" button at `/Admin/GoogleSync` until automated sync is validated.

## Important: ConsentRecord is Immutable

The `consent_records` table has database triggers that prevent UPDATE and DELETE operations. Only INSERT is allowed to maintain GDPR audit trail integrity.

## Important: Volunteer vs Tier Applications — Separate Concepts

**Volunteer** = the standard member. ~100% of users. Onboarding: sign up, complete profile, consent to legal docs, Consent Coordinator clears → auto-approved → added to Volunteers team. This is NOT done through the Application entity.

**Colaborador** = active contributor with project/event responsibilities. Requires application + Board vote. 2-year term.

**Asociado** = voting member with governance rights (assemblies, elections). Requires application + Board vote. 2-year term.

**NEVER conflate Volunteer access with tier applications.** The Application/Board Voting workflow is NOT part of volunteer onboarding. It is a separate, optional path for volunteers who want Colaborador or Asociado status. Volunteer access proceeds in parallel and is never blocked by tier applications.

## Application Workflow State Machine

The Application entity is for **Colaborador and Asociado tier applications only**, NOT for becoming a volunteer.

```
Submitted → UnderReview → Approved/Rejected
         ↘ Withdrawn ↙
```

Triggers: `StartReview`, `Approve`, `Reject`, `Withdraw`, `RequestMoreInfo`

## Namespace Alias

Due to namespace collision, use `MemberApplication` alias when referencing `Humans.Domain.Entities.Application`:

```csharp
using MemberApplication = Humans.Domain.Entities.Application;
```

## Important: UI Terminology — "Humans" Not "Members" or "Volunteers"

In all user-facing text (views, localization strings, emails), use **"humans"** — not "members", "volunteers", or "users". This is the org's branded terminology. It applies across all locales (the word "humans" is kept in English even in es/de/fr/it translations). Internal code (entity names, variable names) is unaffected.

Also: the system stores **birthday** (month + day only), not **date of birth** (which implies year). Use "birthday" in UI text.

## Important: Coolify Docker Build Constraints

Coolify strips `.git` from the Docker build context. Do NOT use `COPY .git` in the Dockerfile — it will fail on production deploys. Instead, Coolify passes `SOURCE_COMMIT` as a Docker build arg containing the full commit SHA. The `Directory.Build.props` MSBuild target for `SourceRevisionId` has a `Condition` to skip when the property is already set via `-p:`.

## Scale and Deployment Context

- **Target scale: ~500 users total.** This is a small nonprofit membership system, not a high-traffic service.
- **Single server deployment** — no distributed coordination, no multi-instance concerns. Database concurrency conflicts (e.g., DbContext thread safety) are irrelevant for parallelization decisions since there's only one process.
- **Prefer in-memory caching over query optimization.** At this scale, loading entire datasets into RAM (e.g., all teams, all members) is cheaper and simpler than optimizing individual DB queries. Use `IMemoryCache` freely.
- **Don't over-engineer for scale.** Pagination, batching, and query optimization matter less when the total dataset fits comfortably in memory. Simple, correct code beats performant-but-complex code.

## LSP Integration

The `csharp-ls` LSP is active via the `csharp-lsp` Claude Code plugin. It provides real-time C# compiler diagnostics (type errors, missing usings, nullable warnings, etc.) on `.cs` files when they are read.

**After editing any `.cs` file, re-read it before moving on.** Diagnostics appear on `Read`, not on `Edit`. This catches errors immediately without waiting for a full `dotnet build`. Always fix LSP-reported errors in the current file before editing the next one.

## Git Workflow

Two-remote workflow with QA gating:

- **`origin`** = `peterdrier/Humans` (QA environment)
- **`upstream`** = `nobodies-collective/Humans` (production)

**Default flow:** Push to `origin` (QA) first. Batch changes, test in QA, then PR from `origin` to `upstream` for production. Never push directly to `upstream` without QA validation.

- `git push origin main` — deploy to QA
- `gh pr create -R nobodies-collective/Humans --head peterdrier:main` — PR to production

**QA deployment:** Always use `./deploy-qa.sh` to start QA. This script pulls latest changes, sets `SOURCE_COMMIT` for the footer git hash, and rebuilds/restarts the containers. Use `--no-pull` if you've already pulled. Never use bare `docker compose up` for QA — always use the deploy script.

## Build Commands

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx
dotnet run --project src/Humans.Web
```

## Maintenance Log

**After running any recurring maintenance process** (context cleanup, feature spec sync, NuGet check, code simplification, etc.), update `.claude/MAINTENANCE_LOG.md` with the current date and next-due date.

## Extended Docs

| Topic | File |
|-------|------|
| **Coding rules** | **`.claude/CODING_RULES.md`** |
| Data model | `.claude/DATA_MODEL.md` |
| Analyzers/ReSharper | `.claude/CODE_ANALYSIS.md` |
| Maintenance log | `.claude/MAINTENANCE_LOG.md` |
| **Feature specs** | **`docs/features/`** |

## Feature Documentation

**Important:** When implementing new features, create or update the corresponding feature spec in `docs/features/`. Each feature doc should include:
- Business context
- User stories with acceptance criteria
- Data model
- Workflows/state machines (if applicable)
- Related features

## About Page / License Attribution

The About page (`Views/Home/About.cshtml`) lists all production NuGet packages and frontend CDN dependencies with versions and licenses. **After any NuGet package update, add the new package versions to the About page.** This is tracked as a monthly maintenance task tied to the NuGet full update cycle.

The project is licensed under **AGPL-3.0** (`LICENSE` at repo root).

## Post-Fix Documentation Check

**After completing a fix or feature but before committing**, check the relevant BRDs in `docs/features/` and update them if the change affects documented behavior, authorization rules, workflows, data model, or routes. This reduces churn from separate doc-only commits.

## Todos and Issue Tracking

**After committing work that resolves or partially resolves items in `todos.md`**, update the file: move completed items to the Completed section with a summary of what was done and the commit hash. This keeps the todo list accurate and avoids stale entries.

**After committing work that resolves a GitHub issue**, close the issue with `gh issue close <number> -c "comment"` including a brief summary and the commit hash.
