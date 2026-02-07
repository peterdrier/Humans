# Nobodies Profiles

Membership management system for Nobodies Collective (Spanish nonprofit).

## Purpose

Manage the full membership lifecycle for Nobodies Collective: volunteer applications are reviewed and approved by the Board, accepted members are provisioned into the appropriate teams and Google Workspace resources (Drive folders, Groups), and governance roles (Board, Metaleads, Admin) are tracked with temporal assignments. The system provides a way to organize teams logically and visually, gives Board and Admin visibility into what happens automatically on members' behalf through audit trails, and maintains GDPR compliance through consent tracking, data export, and right-to-deletion support.

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
| `src/Profiles.Web/Program.cs` | Startup, DI, middleware configuration |
| `src/Profiles.Domain/Entities/` | Core domain entities |
| `src/Profiles.Infrastructure/Data/ProfilesDbContext.cs` | EF Core DbContext |
| `src/Profiles.Infrastructure/Jobs/` | Hangfire background jobs |
| `Directory.Packages.props` | Centralized NuGet package versions |

## Domain Entities

| Entity | Purpose |
|--------|---------|
| `User` | Custom IdentityUser with Google OAuth |
| `Profile` | Member profile with computed MembershipStatus |
| `ContactField` | Contact info with per-field visibility controls |
| `Application` | Membership application with Stateless state machine |
| `RoleAssignment` | Temporal role memberships (ValidFrom/ValidTo) |
| `LegalDocument` / `DocumentVersion` | Legal docs synced from GitHub |
| `ConsentRecord` | **APPEND-ONLY** consent audit trail |
| `Team` / `TeamMember` | Working groups |
| `GoogleResource` | Shared Drive folder + Group provisioning |

## Important: Shared Drives Only

**All Google Drive resources are on Shared Drives.** This system does NOT use regular (My Drive) folders. All Drive API calls must use `SupportsAllDrives = true`, and permission listing must include `permissionDetails` to distinguish inherited from direct permissions. Only direct permissions are managed by the system — inherited Shared Drive permissions are excluded from drift detection and sync.

**Google permission-modifying jobs are currently DISABLED** (`SystemTeamSyncJob`, `GoogleResourceReconciliationJob`). Use the manual "Sync Now" button at `/Admin/GoogleSync` until automated sync is validated.

## Important: ConsentRecord is Immutable

The `consent_records` table has database triggers that prevent UPDATE and DELETE operations. Only INSERT is allowed to maintain GDPR audit trail integrity.

## Application Workflow State Machine

```
Submitted → UnderReview → Approved/Rejected
         ↘ Withdrawn ↙
```

Triggers: `StartReview`, `Approve`, `Reject`, `Withdraw`, `RequestMoreInfo`

## Namespace Alias

Due to namespace collision, use `MemberApplication` alias when referencing `Profiles.Domain.Entities.Application`:

```csharp
using MemberApplication = Profiles.Domain.Entities.Application;
```

## Scale and Deployment Context

- **Target scale: ~500 users total.** This is a small nonprofit membership system, not a high-traffic service.
- **Single server deployment** — no distributed coordination, no multi-instance concerns. Database concurrency conflicts (e.g., DbContext thread safety) are irrelevant for parallelization decisions since there's only one process.
- **Prefer in-memory caching over query optimization.** At this scale, loading entire datasets into RAM (e.g., all teams, all members) is cheaper and simpler than optimizing individual DB queries. Use `IMemoryCache` freely.
- **Don't over-engineer for scale.** Pagination, batching, and query optimization matter less when the total dataset fits comfortably in memory. Simple, correct code beats performant-but-complex code.

## Build Commands

```bash
dotnet build Profiles.slnx
dotnet test Profiles.slnx
dotnet run --project src/Profiles.Web
```

## Extended Docs

| Topic | File |
|-------|------|
| **Coding rules** | **`.claude/CODING_RULES.md`** |
| Data model | `.claude/DATA_MODEL.md` |
| Analyzers/ReSharper | `.claude/CODE_ANALYSIS.md` |
| NuGet updates | `.claude/NUGET_UPDATE_CHECK.md` |
| **Feature specs** | **`docs/features/`** |

## Feature Documentation

**Important:** When implementing new features, create or update the corresponding feature spec in `docs/features/`. Each feature doc should include:
- Business context
- User stories with acceptance criteria
- Data model
- Workflows/state machines (if applicable)
- Related features

## Post-Fix Documentation Check

**After completing a fix or feature but before committing**, check the relevant BRDs in `docs/features/` and update them if the change affects documented behavior, authorization rules, workflows, data model, or routes. This reduces churn from separate doc-only commits.
