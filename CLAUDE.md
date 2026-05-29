# Nobodies Humans

Membership management system for Nobodies Collective (Spanish nonprofit). Manages the full membership lifecycle: volunteer applications reviewed and approved by the Board, accepted members provisioned into teams + Google Workspace resources, governance roles tracked with temporal assignments. Provides Board/Admin visibility into automated actions through audit trails. GDPR-compliant via consent tracking, data export, and right-to-deletion.

## Architecture

**The hard rules below are the constitution — the final word. They override every other doc on conflict, and are hand-maintained by Peter; never edit them as an LLM.**

@docs/architecture/peters-hard-rules.md

Clean Architecture with 4 layers (strict dependency direction inward):

- **Domain** — entities, enums, value objects. No external dependencies.
- **Application** — service interfaces and implementations, repository/store interfaces, DTOs. No EF types.
- **Infrastructure** — repository implementations, `HumansDbContext`, migrations, external API clients, jobs.
- **Web** — controllers, views, view models, API endpoints, DI wiring.

See [`docs/architecture/design-rules.md`](docs/architecture/design-rules.md) — the **regulations**: the implementing detail behind the hard rules (layer responsibilities, table ownership map, caching pattern §15, authorization, cross-domain rules). Open a single section on demand; read cover-to-cover only when onboarding. On any conflict with the hard rules, the hard rules win.

## Project Rules — `memory/INDEX.md`

Atomic, task-fires rules (URL conventions, GitHub workflow, EF migration discipline, PR review process, terminology restrictions, etc.) live as one-rule-per-file under [`memory/`](memory/). The catalog is at [`memory/INDEX.md`](memory/INDEX.md) — that's the file to scan when you need to know if a rule applies.

See [`memory/META.md`](memory/META.md) for: when to add an atom vs prose doc, file format, bucket conventions, and how to maintain the catalog.

### New project rules → `memory/`, not external memory

When a durable project rule surfaces, capture it as a `memory/<bucket>/<name>.md` atom **plus an `INDEX.md` line in the same commit** — never the per-machine auto-memory at `~/.claude/projects/…` (it doesn't sync across machines). Session-ephemeral notes can stay in auto-memory. Pattern: [`memory/META.md`](memory/META.md) · maintenance loop: [`rules-maintenance`](memory/process/rules-maintenance.md).

## Design Dialogue — 95% Confidence Loop

When drafting an issue/spec/API/refactor: audit from code (not memory), draft, self-assess. If <95% confident, ask focused clarifying questions on the load-bearing guesses **inline** (include a genuine "let the implementer decide" option), update, repeat, *then* ask to submit. Catches cow-path-as-design and hallucinated requirements. Cap ~2 rounds; punt minor stuff to the implementer.

## Reuse-First Change Discipline

Before adding any new file, public type, interface method, service/repository method, DTO/view model, helper, endpoint, dependency, or DI registration, audit the existing owner/surface first and prefer reuse, caller-side composition, and small local LINQ/mapping. State which existing options you rejected and why; public/interface surface needs Peter's approval. Detail: [`reuse-first-change-discipline`](memory/process/reuse-first-change-discipline.md); for read paths, [`read-model-enrichment`](memory/architecture/read-model-enrichment.md) (enrich a canonical read DTO before adding a read interface).

## Concepts — Volunteer vs Tier Applications

SEPARATE concepts; do not conflate:

- **Volunteer** = the standard member (~100% of users). Onboarding: sign up → complete profile → consent → Consent Coordinator clears → auto-approved → Volunteers team. **NOT done through the `Application` entity.**
- **Colaborador** = active contributor; application + Board vote; 2-year term.
- **Asociado** = voting member (assemblies, elections); application + Board vote; 2-year term.

The `Application` entity is for **Colaborador/Asociado tier applications only**. Volunteer access runs in parallel and is **never blocked** by tier applications. Workflow: `Submitted → Approved/Rejected` or `Submitted → Withdrawn`; triggers `Approve`/`Reject`/`Withdraw`.

## Section Invariants — `docs/sections/`

Each major section of the app has a terse invariant doc in [`docs/sections/`](docs/sections/) defining: concepts, data model, actors/roles, invariants, negative access rules, triggers, cross-section dependencies, architecture status. Every section follows [`docs/sections/SECTION-TEMPLATE.md`](docs/sections/SECTION-TEMPLATE.md).

`/Admin/*` is a nav holder, not a section — its services belong to the sections they act on.

## Scale and Deployment

- **~500 users total.** Small nonprofit membership system, not a high-traffic service.
- **Single-server deployment** — no distributed coordination, no multi-instance concerns.
- **Prefer in-memory caching over query optimization.** At this scale, loading entire datasets into RAM is cheaper and simpler than per-query optimization.
- **Don't over-engineer for scale.** Pagination, batching, and query optimization matter less when the dataset fits in memory.
- See [`memory/architecture/no-concurrency-tokens.md`](memory/architecture/no-concurrency-tokens.md) — no `IsConcurrencyToken` or row versioning.

## Build Commands

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

(`-v quiet` is required — see [`memory/process/dotnet-verbosity-quiet.md`](memory/process/dotnet-verbosity-quiet.md).)

## Git Workflow — Two-Remote

- **`origin`** = `peterdrier/Humans` (fork — QA auto-deploys from `main` via Coolify)
- **`upstream`** = `nobodies-collective/Humans` (production)

All changes go on a **feature branch in a worktree** (`.worktrees/<name>`) → PR to `origin/main` (squash if multiple commits). Promote to prod by batching on `origin/main` → PR to `upstream/main` (rebase merge) — use `/pr-prod`. Per-PR preview deploys at `https://{pr_id}.n.burn.camp` (DB `humans_pr_{N}` cloned from QA, dropped on close; dev login enabled). Version check: `GET /api/version`.

Rules: [`no-direct-to-main`](memory/process/no-direct-to-main.md) · [`issue-refs-qualified`](memory/process/issue-refs-qualified.md) · [`after-prod-merge-reset`](memory/process/after-prod-merge-reset.md) · [`cross-repo-pr-push-target`](memory/process/cross-repo-pr-push-target.md).

## Doc Freshness

`/freshness-sweep` regenerates drift-prone docs against `upstream/main` diffs. Catalog at [`docs/architecture/freshness-catalog.yml`](docs/architecture/freshness-catalog.yml). Spec at `docs/superpowers/specs/2026-04-25-freshness-sweep-design.md`.

After running any recurring maintenance process, update [`docs/architecture/maintenance-log.md`](docs/architecture/maintenance-log.md) — see [`memory/process/maintenance-log-update.md`](memory/process/maintenance-log-update.md).

## Extended Docs

| Topic | File |
|-------|------|
| **Atomic project rules (catalog)** | **[`memory/INDEX.md`](memory/INDEX.md)** |
| **Atomic project rules (how to maintain)** | **[`memory/META.md`](memory/META.md)** |
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
