# Development Processes

How to use the skills and agents in this project. Ordered by typical workflow. Each skill's full instructions live in `.claude/skills/<name>/SKILL.md`.

## Planning: `/plan-sprint`

**When:** Deciding what to work on next. Runs locally — it needs `gh` against both repos.

**What it does:** Reads open issues on `nobodies-collective/Humans` and `peterdrier/Humans`, classifies pipeline status, assesses importance and effort, and groups issues into file-disjoint batches. Only the batches Peter approves are published as a `sprint` tracking issue.

**Output:** `local/sprint-{date}.md`, plus the tracking issue that fires `/execute-sprint`.

```
/plan-sprint                  # full backlog analysis
/plan-sprint quick            # skip research, use issue metadata only
/plan-sprint section:shifts   # focus on one section
/plan-sprint migrations       # only items that need EF migrations
```

## Execution: `/execute-sprint`

**When:** A cloud routine fires on a sprint tracking issue, or an unattended session is told to work the sprint queue.

**What it does:** Works each approved batch on its own `claude/sprint-<date>-batch-<n>` branch through the `batch-worker` agent, skips gated items to a Needs-Peter list, and never merges.

**Output:** One PR per batch on `peterdrier/Humans`, and a run report on the tracking issue.

## Review: `/spec-review`

**When:** Before creating a PR, or to audit an existing PR against its linked issues.

**What it does:** Fetches the linked GitHub issue specs, extracts acceptance criteria, reads the actual code (not the PR description), and checks each criterion with evidence. Catches implementation drift — building something plausible but wrong.

**Output:** Structured report with PASS/FAIL per criterion, drift analysis, blocking issues.

```
/spec-review PR 64                                # review existing PR
/spec-review nobodies-collective#264 peterdrier#265   # current changes against specific issues
/spec-review                                      # current branch against issues in commit messages
```

## Review: `/reuse-review`

**When:** Before creating a PR that adds files, types, interface methods, routes or DI registrations.

**What it does:** Flags new durable surface that should reuse an existing owner instead.

```
/reuse-review         # current branch
/reuse-review PR 64
```

## Review: `/check-pr`

**When:** A PR arrives from outside the agent workflow.

**What it does:** Checks it for rule violations against the `memory/INDEX.md` atoms and `docs/architecture/code-review-rules.md`.

```
/check-pr PR 64
```

## Review findings: `/fix`

**When:** Bot or human review findings are open on a PR, before any fix commit in a review loop.

**What it does:** Treats every finding as a hypothesis to refute; fixes only what is proven real, reachable, in scope and above the round's severity bar, and declines or files the rest. The bar rises each round.

```
/fix        # current branch's PR
/fix 123
```

## Migrations: `/ef-regen`

**When:** A branch's in-flight migrations have accumulated cruft, or the branch merged main and its migrations are stuck mid-chain.

**What it does:** Scraps the branch's migrations and regenerates them as one consolidated migration.

```
/ef-regen AddContainers   # name of the consolidated migration
```

## Navigation: `/nav-audit`

**When:** After adding new pages or features, or periodically as hygiene.

**What it does:** Scans controller actions and views, maps navigation links, and finds orphan pages, missing backlinks and poor discoverability. Produces a list; changes no code.

```
/nav-audit         # full site audit
/nav-audit admin   # audit admin navigation only
/nav-audit teams   # audit team navigation only
```

## Testing: `/test-site`

**When:** After deploying to QA or a preview environment.

**What it does:** Runs browser-based smoke tests against the running site using the Chrome extension.

```
/test-site smoke   # or: all | auth | profile | consent | teams | admin | gdpr | i18n | <url>
```

## Release: `/pr-prod`

**When:** QA has passed and batched changes on `peterdrier/Humans` should go to production.

**What it does:** Opens the promotion PR from `peterdrier/Humans:main` to `nobodies-collective/Humans:main` with a qualified commit summary. Merging it needs Peter's explicit go.

## Section health: `/section-doctor`

**When:** The daily per-section improvement run, or `doctor <section>` on demand.

**What it does:** Picks a section, inventories every file, derives its target shape, and strikes a ranked list of findings on a 2–3h budget. One PR per run; the run report and Needs-Peter queue live in `docs/health/runs/`. `resume` applies Peter's answers later.

```
/section-doctor
/section-doctor --section=Camps
/section-doctor resume
```

## Section boundaries: `/section-align`

**When:** A sizable PR landed or a section shows architecture drift.

**What it does:** Checks a section's boundaries, internal cohesion and test focus, then fixes them in a push-and-bot-review loop.

```
/section-align PR 374
/section-align section Camps --inventory-only
```

## Debt: `/debt-sweep` and `/debt-review`

**When:** Daily debt burndown.

**What they do:** `/debt-sweep` works the least-recently-served theme in `docs/architecture/debt-ledger.yml` for a time budget and opens one PR. `/debt-review` reviews, repairs and stewards the nightly Codex debt PR (`codex/daily-debt/<date>`), judging each commit GOOD / REPAIR / REVERT.

```
/debt-sweep --budget=2h
/debt-review          # today's codex/daily-debt PR
```

## Docs: `/freshness-sweep` and `/cleanup-memory`

**When:** Docs may have drifted from code, or rule files need hygiene.

**What they do:** `/freshness-sweep` refreshes the docs listed in `docs/architecture/freshness-catalog.yml` against code changed since the last sweep and opens one PR. `/cleanup-memory` migrates durable rules from per-machine agent memory into `memory/` and audits the in-repo rules for dead links, duplication and drift.

```
/freshness-sweep
/freshness-sweep --full
/cleanup-memory report-only
```

## Tests: `/trim-tests`

**When:** A test file or section carries LLM-generated test bloat.

**What it does:** Uses Stryker mutation scores to delete low-value tests, consolidate redundant ones and fill coverage gaps — score up, test count down.

```
/trim-tests          # worst-scored file
/trim-tests Shifts   # one section
```

## Brand: `/redraw-logo`

**When:** Every 2-3 months, when a new first-class model ships.

**What it does:** Redraws the Vitruvian Man mark against the standing brief (`src/Humans.Web/wwwroot/img/logo.md`), derives every icon from the master, archives the previous round under `docs/brand/logo-history/`, wires it in and opens the PR.

```
/redraw-logo            # label the archive after the running model
/redraw-logo opus-5.5   # explicit label
```

## Triage: `/triage`

**When:** Regularly, to work user-reported problems.

**What it does:** Five phases: application logs, closing already-shipped GitHub issues, pending feedback, the in-app Issues backlog, and agent conversations whose refusals reveal FAQ gaps.

```
/triage          # all phases
/triage logs
/triage issues
```

## Other Tools

| Skill | When to use |
|-------|-------------|
| `/finish` | End of session — checks git state, captures context, flags loose ends |
| `/reforge` | Roslyn-powered queries for references, callers and implementations |
| `/refactor-swarm` | Parallel Reforge-guided refactors across several sections, one draft PR each |

## Agents (not invoked directly)

These are used internally by the skills above:

| Agent | Used by | Purpose |
|-------|---------|---------|
| `batch-worker` | `/execute-sprint` | Implements a batch of issues with review loops |
| `spec-compliance-reviewer` | `/spec-review` | Checks code against issue acceptance criteria |
| `doctor-reader` | `/section-doctor` | Reading threads (freshness, tests, history, comments, inbox) |
| `doctor-reviewer`, `doctor-reviewer-light`, `doctor-reviewer-critical` | `/section-doctor` | Score-blind second-opinion gate for non-mechanical strikes, tiered by section risk |
| `ef-migration-reviewer` | Manual (before committing migrations) | Reviews EF Core migrations for common traps |

Codex runs its own nightly debt and bug-hunt passes from `.codex/` (see `.codex/README.md`); `/debt-review` picks up their PRs.

## Typical Session Flow

1. **`/plan-sprint`** — groom the backlog; publish approved batches as a sprint tracking issue
2. **`/execute-sprint`** — let the cloud run work the batches (or work manually)
3. **`/spec-review`** and **`/reuse-review`** — verify before the PR
4. **Open the PR** on `peterdrier/Humans`; **`/fix`** review findings as they arrive
5. **Merge the QA PR** (with Peter's go) — `peterdrier/Humans` main auto-deploys to QA
6. **`/test-site`** — smoke test the QA deployment
7. **`/pr-prod`** — promote to `nobodies-collective/Humans` once QA passes
8. **`/finish`** — clean up, capture context
