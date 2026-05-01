# Nobodies Humans

Membership management system for Nobodies Collective (Spanish nonprofit). Manages the full membership lifecycle: volunteer applications reviewed and approved by the Board, accepted members provisioned into teams + Google Workspace resources, governance roles tracked with temporal assignments. Provides Board/Admin visibility into automated actions through audit trails. GDPR-compliant via consent tracking, data export, and right-to-deletion.

## Architecture

Clean Architecture with 4 layers (strict dependency direction inward):

- **Domain** — entities, enums, value objects. No external dependencies.
- **Application** — service interfaces and implementations, repository/store interfaces, DTOs. No EF types.
- **Infrastructure** — repository implementations, `HumansDbContext`, migrations, external API clients, jobs.
- **Web** — controllers, views, view models, API endpoints, DI wiring.

See [`docs/architecture/design-rules.md`](docs/architecture/design-rules.md) for the full architecture story (the constitution): layer responsibilities, table ownership map, caching pattern (§15), authorization pattern, cross-domain rules. Read it once cover-to-cover.

## Project Rules — `docs/rules/INDEX.md`

Atomic, task-fires rules (URL conventions, GitHub workflow, EF migration discipline, PR review process, terminology restrictions, etc.) live as one-rule-per-file under [`docs/rules/`](docs/rules/). The catalog is at [`docs/rules/INDEX.md`](docs/rules/INDEX.md) — that's the file to scan when you need to know if a rule applies.

See [`docs/rules/META.md`](docs/rules/META.md) for: when to add an atom vs prose doc, file format, bucket conventions, and how to maintain the catalog.

## Concepts — Volunteer vs Tier Applications

These are SEPARATE concepts; do not conflate them.

**Volunteer** = the standard member. ~100% of users. Onboarding: sign up → complete profile → consent to legal docs → Consent Coordinator clears → auto-approved → added to Volunteers team. **NOT done through the `Application` entity.**

**Colaborador** = active contributor with project/event responsibilities. Requires application + Board vote. 2-year term.

**Asociado** = voting member with governance rights (assemblies, elections). Requires application + Board vote. 2-year term.

The `Application` entity is for **Colaborador and Asociado tier applications only**, NOT for becoming a volunteer. Volunteer access proceeds in parallel and is **never blocked** by tier applications.

Application workflow state machine:

```
Submitted → Approved/Rejected
         ↘ Withdrawn ↙
```

Triggers: `Approve`, `Reject`, `Withdraw`.

## Section Invariants — `docs/sections/`

Each major section of the app has a terse invariant doc in [`docs/sections/`](docs/sections/) defining: concepts, data model, actors/roles, invariants, negative access rules, triggers, cross-section dependencies, architecture status. Every section follows [`docs/sections/SECTION-TEMPLATE.md`](docs/sections/SECTION-TEMPLATE.md).

`/Admin/*` is a nav holder, not a section — its services belong to the sections they act on.

## Scale and Deployment

- **~500 users total.** Small nonprofit membership system, not a high-traffic service.
- **Single-server deployment** — no distributed coordination, no multi-instance concerns.
- **Prefer in-memory caching over query optimization.** At this scale, loading entire datasets into RAM is cheaper and simpler than per-query optimization.
- **Don't over-engineer for scale.** Pagination, batching, and query optimization matter less when the dataset fits in memory.
- See [`docs/rules/architecture/no-concurrency-tokens.md`](docs/rules/architecture/no-concurrency-tokens.md) — no `IsConcurrencyToken` or row versioning.

## Build Commands

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

(`-v quiet` is required — see [`docs/rules/process/dotnet-verbosity-quiet.md`](docs/rules/process/dotnet-verbosity-quiet.md).)

## Git Workflow — Two-Remote

- **`origin`** = `peterdrier/Humans` (peter's fork — QA deploys from `main`)
- **`upstream`** = `nobodies-collective/Humans` (production)

**All changes go on a feature branch** → PR to `main` on peter's fork (squash merge if multiple commits). Preview environments deploy per-PR at `{pr_id}.n.burn.camp`. Use a worktree under `.worktrees/<name>`.

**Promote to production:** batch changes on peter's `main`, PR to nobodies' `main` (rebase merge — individual efforts already squashed).

**Preview environment:**
- URL: `https://{pr_id}.n.burn.camp`
- Database: cloned from QA via GitHub Action (`humans_pr_{N}`), dropped on PR close
- Auth: dev login enabled (`DevAuth__Enabled=true`)
- Connection string override: `docker-entrypoint.sh` extracts PR number from `COOLIFY_CONTAINER_NAME`

**Version endpoint:** `GET /api/version` (unauthenticated) returns `{ version, commit, informationalVersion }`.

**QA deployment:** Coolify auto-deploys on push to `main` on peter's fork. Coolify UI at `https://coolify.n.burn.camp`.

For workflow rules, see:
- [`docs/rules/process/no-direct-to-main.md`](docs/rules/process/no-direct-to-main.md)
- [`docs/rules/process/issue-refs-qualified.md`](docs/rules/process/issue-refs-qualified.md)
- [`docs/rules/process/after-prod-merge-reset.md`](docs/rules/process/after-prod-merge-reset.md)

## Doc Freshness

`/freshness-sweep` regenerates drift-prone docs against `upstream/main` diffs. Catalog at [`docs/architecture/freshness-catalog.yml`](docs/architecture/freshness-catalog.yml). Spec at `docs/superpowers/specs/2026-04-25-freshness-sweep-design.md`.

After running any recurring maintenance process, update [`docs/architecture/maintenance-log.md`](docs/architecture/maintenance-log.md) — see [`docs/rules/process/maintenance-log-update.md`](docs/rules/process/maintenance-log-update.md).

## Extended Docs

| Topic | File |
|-------|------|
| **Atomic project rules (catalog)** | **[`docs/rules/INDEX.md`](docs/rules/INDEX.md)** |
| **Atomic project rules (how to maintain)** | **[`docs/rules/META.md`](docs/rules/META.md)** |
| **Design rules (architecture story)** | **[`docs/architecture/design-rules.md`](docs/architecture/design-rules.md)** |
| **Code review rules (reviewer handoff)** | **[`docs/architecture/code-review-rules.md`](docs/architecture/code-review-rules.md)** |
| **Section invariants** | **[`docs/sections/`](docs/sections/)** |
| **Section template** | [`docs/sections/SECTION-TEMPLATE.md`](docs/sections/SECTION-TEMPLATE.md) |
| **Feature specs** | [`docs/features/`](docs/features/) |
| Data model | [`docs/architecture/data-model.md`](docs/architecture/data-model.md) |
| Dependency graph | [`docs/architecture/dependency-graph.md`](docs/architecture/dependency-graph.md) |
| Analyzers/ReSharper | [`docs/architecture/code-analysis.md`](docs/architecture/code-analysis.md) |
| Maintenance log | [`docs/architecture/maintenance-log.md`](docs/architecture/maintenance-log.md) |
| EF migration reviewer | [`.claude/agents/ef-migration-reviewer.md`](.claude/agents/ef-migration-reviewer.md) |
| Freshness catalog | [`docs/architecture/freshness-catalog.yml`](docs/architecture/freshness-catalog.yml) |

The project is licensed under **AGPL-3.0** (`LICENSE` at repo root).
