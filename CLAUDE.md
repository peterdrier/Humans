# Nobodies Humans

Membership management system for Nobodies Collective (Spanish nonprofit).

## Purpose

Manage the full membership lifecycle for Nobodies Collective: volunteer applications are reviewed and approved by the Board, accepted members are provisioned into the appropriate teams and Google Workspace resources (Drive folders, Groups), and governance roles (Board, Coordinators, Admin) are tracked with temporal assignments. The system provides a way to organize teams logically and visually, gives Board and Admin visibility into what happens automatically on members' behalf through audit trails, and maintains GDPR compliance through consent tracking, data export, and right-to-deletion support.

## Critical: Coding Rules

**See [`.claude/CODING_RULES.md`](.claude/CODING_RULES.md) for critical rules:**
- Do not remove "unused" properties (reflection usage)
- Never rename fields in serialized objects (breaks JSON deserialization)
- JSON serialization requirements
- String comparison rules
- **NodaTime for all dates/times** (`Instant`, `LocalDate`, etc.)
- **Every new page MUST have a nav link.** If you add a controller action that returns a view, add a link to it from the nav menu or a contextual link from a related page. No orphan pages.

## Architecture

Clean Architecture with 4 layers:
- **Domain**: Entities, enums, value objects
- **Application**: Interfaces, DTOs, use cases
- **Infrastructure**: EF Core, external services, jobs
- **Web**: Controllers, views, API

## Domain Entities

See [`.claude/DATA_MODEL.md`](.claude/DATA_MODEL.md) for full data model, relationships, and serialization notes. Key entities: `User`, `Profile`, `ContactField`, `Application` (Colaborador/Asociado tier applications), `BoardVote` (transient), `RoleAssignment`, `LegalDocument`/`DocumentVersion`, `ConsentRecord` (append-only), `Team`/`TeamMember`, `GoogleResource`, `BudgetYear`/`BudgetGroup`/`BudgetCategory`/`BudgetLineItem`, `BudgetAuditLog` (append-only).

## Important: Shared Drives Only

**All Google Drive resources are on Shared Drives.** This system does NOT use regular (My Drive) folders. All Drive API calls must use `SupportsAllDrives = true`, and permission listing must include `permissionDetails` to distinguish inherited from direct permissions. Only direct permissions are managed by the system — inherited Shared Drive permissions are excluded from drift detection and sync.

**Google sync jobs** (`SystemTeamSyncJob` hourly, `GoogleResourceReconciliationJob` daily at 03:00) are controlled by per-service mode at `/Admin/SyncSettings` (None/AddOnly/AddAndRemove). Set a service to "None" to disable without redeploying.

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
Submitted → Approved/Rejected
         ↘ Withdrawn ↙
```

Triggers: `Approve`, `Reject`, `Withdraw`

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
- **No concurrency tokens.** Do NOT add `IsConcurrencyToken()`, `[ConcurrencyCheck]`, or row versioning to any entity. At single-server scale with ~500 users, concurrency conflicts don't happen and optimistic concurrency only causes bugs. Never add them without explicit user permission.

## Git Workflow

Two-remote workflow:

- **`origin`** = `peterdrier/Humans` (peter's fork — QA deploys from `main`)
- **`upstream`** = `nobodies-collective/Humans` (production)

**Development flow:**

- **Small changes:** commit directly to `main` on peter's fork. Coolify auto-deploys to QA.
- **Larger changes:** feature branch → PR to `main` on peter's fork (squash merge if multiple commits). Preview environments deploy per-PR at `{pr_id}.n.burn.camp`.
- **Promote to production:** batch changes on peter's `main`, PR to nobodies' `main` (rebase merge, since individual efforts were already squashed going into peter's `main`).
- **After production merge:** reset peter's `main` to nobodies' `main`:
  ```bash
  git fetch upstream main
  git checkout main && git reset --hard upstream/main
  git push origin main --force-with-lease
  ```

**QA deployment:** Coolify auto-deploys on push to `main` on peter's fork. Coolify UI at `https://coolify.n.burn.camp`.

**Preview environment details:**
- URL: `https://{pr_id}.n.burn.camp`
- Database: cloned from QA via GitHub Action (`humans_pr_{N}`), dropped on PR close
- Auth: dev login enabled (`DevAuth__Enabled=true`) since Google OAuth doesn't support wildcard redirect URIs
- Connection string override: `docker-entrypoint.sh` extracts PR number from `COOLIFY_CONTAINER_NAME`

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
| **Code review rules** | **`.claude/CODE_REVIEW_RULES.md`** |
| Data model | `.claude/DATA_MODEL.md` |
| Analyzers/ReSharper | `.claude/CODE_ANALYSIS.md` |
| Maintenance log | `.claude/MAINTENANCE_LOG.md` |
| **Feature specs** | **`docs/features/`** |
| **EF migration reviewer** | **`.claude/agents/ef-migration-reviewer.md`** |

## Critical: EF Migration Review Gate

**Before committing any EF Core migration**, run the EF migration reviewer agent (`.claude/agents/ef-migration-reviewer.md`). Mandatory for all database changes — do not commit or create PRs until it passes with no CRITICAL issues.

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
