<!-- freshness:triggers
  src/**
  memory/**
  docs/architecture/**
  CONTEXT.md
-->
<!-- freshness:flag-on-change
  Repo-root agent guide: architecture roles, glossary, workflow, and restated rules
  with links to their canonical sources. Flag if a linked atom/doc moves, a section-role
  concept shifts, or the build/PR workflow changes.
-->

# Humans

Humans is the membership management system for Nobodies Collective, a Spanish nonprofit. It runs the full membership lifecycle: volunteer signup, profile and consent, Colaborador/Asociado applications voted on by the Board, provisioning members into teams and Google Workspace, governance roles, shifts, tickets, and finance — with audit trails so the Board can see what automation did, and GDPR compliance throughout.

## What makes Humans special?

About 500 real people trust this system with their personal data, and a volunteer Board relies on it to run a legal Spanish asociación. Here's what we never compromise on.

### 1. Sections own their data, end to end

The app is ~40 vertical sections (`src/Sections/Humans.<Section>`), each owning its services and views, and — where it has data — its own `DbContext`, migrations, and tables (orchestrator sections like Onboarding and Gdpr own none). There is no shared DbContext — every table belongs to exactly one section. Sections interact only through public interfaces and `.Contracts` leaves. Reaching into another section's tables or internals is the cardinal sin here, and existing violations are tech debt, never precedent.

### 2. GDPR is a feature, not a checkbox

Consent tracking, data export, right-to-deletion. Anything that touches personal data must keep those paths whole: new personal data needs an export contributor and a deletion path, and consent gates stay in front of what they gate.

### 3. The Board can see what happened

Automated actions leave audit trails. An automation that acts invisibly is a bug, even when it acts correctly.

### 4. Small scale, simple systems

~500 users, one server. Load the dataset into RAM instead of optimizing queries. No distributed coordination, no concurrency tokens or row versioning ([`no-concurrency-tokens`](memory/architecture/no-concurrency-tokens.md)), no pagination for its own sake. Complexity has to buy something at *this* scale, not an imagined one.

## Peter's rules

The constitution of this repo is hand-written by Peter and is the final word, above this file and everything else:

- [`docs/architecture/peters-hard-rules.md`](docs/architecture/peters-hard-rules.md) — the intent behind the architecture (data ownership, layers, public surface) and the short list of absolutes.
- [`docs/architecture/peters-working-rules.md`](docs/architecture/peters-working-rules.md) — the behavioral absolutes: fix at the source, a question gets an answer not a commit.

Read both before your first change. LLMs never edit either file; changes to them come from Peter himself. If a rule fights the task in front of you, say so loudly and get Peter's sign-off — for hard rules there is no sign-off, only an issue recording the debt.

## When you need a rule

Atomic, task-triggered rules (URL conventions, EF migration discipline, terminology restrictions, PR process, …) live one-per-file under [`memory/`](memory/). **Scan [`memory/INDEX.md`](memory/INDEX.md) whenever you wonder whether a rule applies** — the descriptions are written to match against the task in front of you; fetch an atom's body when its trigger matches.

When a new durable rule surfaces, capture it as a `memory/<bucket>/<name>.md` atom **plus an INDEX line in the same commit** — never in per-machine agent memory, which doesn't sync. Format and bucket conventions: [`memory/META.md`](memory/META.md).

## A small glossary

Terminology matters here — the full ubiquitous language lives in [`CONTEXT.md`](CONTEXT.md), and where other prose disagrees with it, the prose is wrong.

- **you** means the agent reading this file and changing Humans.
- **Peter / maintainer** means who you are talking to now.
- **member** means a person in the system. Nearly all are **Volunteers**.
- **Volunteer** means the standard member: sign up → complete profile → sign the required consents → auto-admitted to the Volunteers team. Admission is name + required consents; the Consent Coordinator's clear/flag review is an independent audit annotation that does not gate admission (reject and suspend are the kick-out levers). Volunteers never go through the `Application` entity.
- **Colaborador** means an active contributor; application plus Board vote; 2-year term.
- **Asociado** means a voting member (assemblies, elections); application plus Board vote; 2-year term.
- **Application** means the entity for Colaborador/Asociado tier applications *only* (`Submitted → Approved/Rejected/Withdrawn`). Volunteer access runs in parallel and is never blocked by it.
- **Section** means a vertical slice owning one lane — its tables plus the logic over them.
- **Lane / width** mean one section's domain, and how many lanes a service touches. Width is a cost, not a feature.
- **Crosscut** means a full-width, logic-free tool section (Audit, Email, Notifications). It owns its own data and reaches into nobody else's.
- **Orchestrator** means a service that owns no tables and coordinates two or more sections through their interfaces only. The moment it owns a table, it's a Section.
- **Base** means `src/Humans.Base`, the bottom of the dependency graph and the only project every section may reference.
- **Shell** means `src/Humans.Web`: chrome, page composition, platform context. Nothing references the Shell.
- **Board** means the governance body that reviews and votes on tier applications.

## The ways to hurt yourself

1. **Conflating Volunteer with Application.** These are separate concepts and the most common conceptual mistake in this codebase. Volunteers are ~100% of users and never touch the `Application` entity; tier applications are the exception, not the model.
2. **Hand-editing state to make a red light go away.** No `--no-verify`, no suppressing errors, no deleting "stuck" state, no editing the database or deployed config by hand. The fix lives in code, configuration, or re-provisioning. If the only path you can see goes through a manual state edit or a bypass flag, stop and ask — offering the shortcut as one option among several is itself the violation. "Broken" is sometimes the correct state to leave something in.
3. **Mid-chain migration surgery.** Migrations are per-section and shipped ones are immutable. After your branch merges main, `dotnet ef migrations remove` on your in-flight migrations is unsafe — regenerate the branch's migrations as one consolidated migration instead ([`migration-regen-after-rebase`](memory/architecture/migration-regen-after-rebase.md)).
4. **Trampling parallel sessions.** On a local machine several agent sessions share one clone: work only in your own worktree (`.worktrees/<name>`), never assume the main checkout is idle or yours, and never clean up state — worktrees, branches, stashes — you didn't create. A Claude Code cloud run (`CLAUDE_CODE_REMOTE=true`) is a single-session ephemeral container with nobody to trample, so it works in the repo root and skips the worktree — [`always-use-worktree`](memory/process/always-use-worktree.md) carries both cases.
5. **Committing straight to main.** Every change goes on a feature branch, then a PR ([`no-direct-to-main`](memory/process/no-direct-to-main.md)). `origin/main` auto-deploys to QA; there is no such thing as a commit too small for a PR. One carve-out: a change confined to `memory/**` may go straight to `origin/main` — the atom has the details.

## Hit every surface

The most common defect here is a change that works on the path you tested and is missing everywhere else. Before calling work done, walk this list and say which entries applied:

- **Every supported culture.** Every user-facing string lives in the section's resx set, in all six supported cultures (en, es, de, it, fr, ca — parity tests enforce it). A hardcoded string or a missing translation is an incomplete change. Exception: admin-side views (`/Admin/*`, `/TeamAdmin/*`, `/Shifts/Dashboard`) don't get new localization keys ([`localization-admin-exempt`](memory/code/localization-admin-exempt.md)).
- **Authorization, including the negative cases.** Each section's invariant doc lists who must *not* see or do a thing. New pages and endpoints need the deny paths verified, not just the happy path.
- **Audit trail.** Actions taken by automation or admins on members' behalf need their audit entries.
- **GDPR paths.** New personal data → export contributor, deletion path, consent where it applies.
- **The section's invariant doc.** Behavior changes must keep `src/Sections/Humans.<Section>/Docs/<Section>.md` true — update it in the same PR or you've shipped documentation drift.
- **Migrations.** Schema changes get a migration in that section's own context; the EF discipline atoms in `memory/` and the [`ef-migration-reviewer`](.claude/agents/ef-migration-reviewer.md) agent own the details.
- **Navigation.** A feature nobody can find is a dead end: the page needs a link that reaches it and a way back out.
- **Tests.** Under the section's own test project (`tests/Humans.<Section>.Tests`), covering the invariant you changed — not a screenshot of the happy path.

## Designing

When drafting an issue, spec, API, or refactor: audit from code, not memory; draft; self-assess. Below 95% confidence, ask focused questions on the load-bearing guesses inline — include a genuine "let the implementer decide" option — then update. Cap it at ~2 rounds; punt minor calls to the implementer. This catches cow-paths presented as design and hallucinated requirements.

## Building and verifying

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

- `-v quiet` is mandatory — default verbosity floods the context for no benefit ([`dotnet-verbosity-quiet`](memory/process/dotnet-verbosity-quiet.md)).
- Scope testing to the change's blast radius ([`scoped-inner-loop-tests`](memory/process/scoped-inner-loop-tests.md)): docs-only changes need no build or tests; a single-section change is gated by that section's test project (`dotnet test tests/Humans.<Section>.Tests -v quiet`, seconds — CI runs the full suite anyway); cross-section surface or an unclear radius gets `tests/Humans.Application.Tests` plus the full `dotnet test Humans.slnx -v quiet` gate before the PR.
- Analyzers enforce the call-site rules (repository access, service boundaries). A red analyzer is the answer, not an obstacle — grandfathered and baselined violations are documented tech debt and never justify a new one.
- Version check on a deployed instance: `GET /api/version`.
- Every PR whose branch lives in `peterdrier/Humans` gets a preview deploy at `https://{pr_id}.n.burn.camp` with its own database cloned from QA and dev login enabled — the place to verify user-visible changes for real. Fork PRs get no preview; a maintainer can deploy one by hand.

## Pull requests

- **Always open the PR yourself** ([`always-open-a-pr`](memory/process/always-open-a-pr.md)). A pushed branch with no PR is invisible: no review, no preview environment, no bots. Don't ask permission first.
- Two remotes: `origin` = `peterdrier/Humans` (fork; QA auto-deploys from its main) and `upstream` = `nobodies-collective/Humans` (production). Feature branches PR to `origin/main` (squash). Promotion to production batches `origin/main` → `upstream/main` and is the one PR that needs Peter's explicit go-ahead. Details: [`cross-repo-pr-push-target`](memory/process/cross-repo-pr-push-target.md) · [`after-prod-merge-reset`](memory/process/after-prod-merge-reset.md).
- Qualify issue references across repos: `nobodies-collective/Humans#123`, never a bare `#123` ([`issue-refs-qualified`](memory/process/issue-refs-qualified.md)).
- Reviewer findings — Codex, Claude bot, Gemini, humans — are hypotheses, not a work list. Verify each against the code before changing anything ([`review-finding-triage`](memory/process/review-finding-triage.md)); every finding ends with a disposition reply in its thread ([`pr-review-feedback-handling`](memory/process/pr-review-feedback-handling.md)).
- Before acting on any CI or review event on a PR you opened, read [`.claude/skills/steward/SKILL.md`](.claude/skills/steward/SKILL.md). Unattended review rounds are capped at five review-round commits — bot/CI response commits only, not the PR's own deliverable ([`review-round-budget`](memory/process/review-round-budget.md)) — past that, stop and surface it.

## How it works

Controllers parse the request, call services, and format the response — no logic. Services (`IApplicationService`) hold the business rules and are the only callers of their section's repository. Repositories (`IRepository`) are the sole readers and writers of their section's tables, and EF entities never leave the section — DTOs and domain objects cross boundaries. Caching decorators wrap service interfaces (never repositories) using `TrackedCache`. Each section registers its own DI from `Section.cs : ISection`, and the Shell composes the registered sections into one app.

## Where code lives

- `src/Humans.Base` — the bottom of the graph: role markers, architecture attributes, `TrackedCache`, the shared view layer. The only project every section may reference.
- `src/Sections/Humans.<Section>[.Contracts]` — the sections. `ls src/Sections` is the list. Each carries `Docs/<Section>.md` (its invariants, following [`docs/sections/SECTION-TEMPLATE.md`](docs/sections/SECTION-TEMPLATE.md)), `Docs/features/` (its feature specs; cross-section specs live in [`docs/features/global/`](docs/features/global/)), and `Docs/data-access.md` — grep the latter to find which section owns a service, repository, or table.
- `src/Humans.Web` — the Shell. Layouts, page composition, platform context. `/` (Home) and `/Admin/*` are Shell frames, not sections: their page bodies are section-contributed, and their services belong to the sections they act on.
- `docs/architecture/` — the hard rules, [`design-rules.md`](docs/architecture/design-rules.md) (the **regulations**: the implementing detail behind the hard rules — open a single section on demand; read cover-to-cover only when onboarding), [`code-review-rules.md`](docs/architecture/code-review-rules.md) (reviewer reject rules), [`dependency-graph.md`](docs/architecture/dependency-graph.md), and [`code-analysis.md`](docs/architecture/code-analysis.md).
- `memory/` — the atomic project rules (see "When you need a rule" above).
- `tests/` — one test project per section, plus `Humans.Testing` helpers.

Recurring maintenance (doc-freshness sweeps, tech-debt burndown) is skill-driven; its ledgers live at [`docs/architecture/freshness-catalog.yml`](docs/architecture/freshness-catalog.yml), [`docs/architecture/debt-ledger.yml`](docs/architecture/debt-ledger.yml), and [`docs/architecture/maintenance-log.md`](docs/architecture/maintenance-log.md) — record newly-found debt in the ledger ([`debt-ledger-additions`](memory/process/debt-ledger-additions.md)).

## Taste

- Width is a cost. The best cross-section feature is the one that touches the fewest lanes, through the narrowest interfaces (`I<Section>ServiceRead` for cross-section reads).
- Reuse before adding ([`reuse-first-change-discipline`](memory/process/reuse-first-change-discipline.md)). Before any new file, public type, interface method, DTO, helper, endpoint, or DI registration: audit the existing owner and prefer reuse or caller-side composition. New public surface needs Peter's approval, and you should say which existing options you rejected and why.
- Think before coding. Don't assume, don't hide confusion, surface tradeoffs. State your assumptions; if multiple interpretations exist, present them instead of picking silently; if a simpler approach exists, say so and push back.
- Minimum code that solves the problem. No speculative flexibility, no abstractions for single-use code. If you wrote 200 lines and it could be 50, rewrite it.
- Surgical changes. Every changed line traces to the request. Don't improve adjacent code; mention dead code, don't delete it.
- Define success criteria, loop until verified. "Fix the bug" means "write a test that reproduces it, then make it pass" — strong criteria let you work independently; weak ones ("make it work") mean constant clarification.
- Prefer in-memory data over clever queries. The whole dataset fits in RAM; act like it.
- Brevity in everything you write — replies, commits, PR bodies, comments, docs. Never use 15 words where 5 will do. Lead with the answer, and write it short the first time.

Humans is licensed under **AGPL-3.0** (`LICENSE` at repo root).
