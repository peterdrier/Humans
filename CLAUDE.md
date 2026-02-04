# Nobodies Profiles

Membership management system for Nobodies Collective (Spanish nonprofit).

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
| `Application` | Membership application with Stateless state machine |
| `RoleAssignment` | Temporal role memberships (ValidFrom/ValidTo) |
| `LegalDocument` / `DocumentVersion` | Legal docs synced from GitHub |
| `ConsentRecord` | **APPEND-ONLY** consent audit trail |
| `Team` / `TeamMember` | Working groups |
| `GoogleResource` | Drive folder provisioning |

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
