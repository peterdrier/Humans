# Development Processes

How to use the skills and agents in this project. Ordered by typical workflow.

## Planning: `/sprint`

**When:** Start of a work session, or when deciding what to work on next.

**What it does:** Reads open GitHub issues and todos.md, assesses business importance and effort, groups related issues into batches, flags migration conflicts, and identifies which batches can run in parallel.

**Output:** `local/sprint-{date}.md` — a structured plan with numbered batches, work orders, and parallel-safety flags.

```
/sprint              # full backlog analysis
/sprint quick        # skip research, use todos.md only
/sprint area:shifts  # focus on one area
```

## Execution: `/execute-sprint`

**When:** After `/sprint` has produced a plan and you want to execute it autonomously.

**What it does:** Launches an agent swarm — one agent per batch, each in its own worktree. Parallel-safe batches run concurrently (max 3). Each agent implements issues sequentially, runs spec review per-issue, runs code review per-batch, fixes failures in a loop (max 3 iterations), and creates one PR per batch.

**Output:** QA PRs on peterdrier/Humans, one per batch. Summary report showing pass/fail per batch.

```
/execute-sprint batch 2       # execute one batch
/execute-sprint batch 2,3,5   # execute specific batches
/execute-sprint all            # execute all batches
/execute-sprint all --dry-run  # show plan without executing
```

## Review: `/spec-review`

**When:** Before creating a PR, or to audit an existing PR against its linked issues.

**What it does:** Fetches the linked GitHub issue specs, extracts acceptance criteria, reads the actual code (not the PR description), and checks each criterion with evidence. Catches implementation drift — building something plausible but wrong.

**Output:** Structured report with PASS/FAIL per criterion, drift analysis, blocking issues.

```
/spec-review PR 64        # review existing PR
/spec-review #264 #265    # review current changes against specific issues
/spec-review              # review current branch against issues in commit messages
```

## Review: `/code-review`

**When:** Before creating a PR, to check code quality.

**What it does:** Multi-model code review (Claude, Codex, Gemini in parallel). Checks against CODE_REVIEW_RULES.md — authorization gaps, missing .Include(), silent exception swallowing, Razor boolean attributes, etc.

```
/code-review    # review current changes
```

## Navigation: `/nav-audit`

**When:** After adding new pages or features, or periodically as hygiene.

**What it does:** Scans all controller actions and views, maps navigation links, finds orphan pages (routes with no UI path to reach them), missing backlinks, and poor discoverability.

```
/nav-audit         # full site audit
/nav-audit admin   # audit admin section only
/nav-audit teams   # audit team navigation only
```

## Testing: `/test-site`

**When:** After deploying to QA or a preview environment.

**What it does:** Runs browser-based smoke tests against the running site using the Chrome extension.

## Maintenance: `/maintenance`

**When:** Periodically, or when spare AI worker quota is available.

**What it does:** Scans for build warnings, TODO/FIXME debt, stale branches, overdue maintenance tasks. Can dispatch work to Codex or Gemini workers.

```
/maintenance               # full scan + propose plan
/maintenance run overdue   # execute overdue calendar items
```

## Feedback: `/triage`

**When:** Regularly, to check for user-reported issues.

**What it does:** Reviews pending in-app feedback, responds to reporters, creates GitHub issues, updates status. Also reviews open issues to close ones already shipped, and checks log events for errors.

## Other Tools

| Skill | When to use |
|-------|-------------|
| `/gemini` | Get a second opinion from Gemini on a question or implementation |
| `/codex` | Get a second opinion from Codex on a question or implementation |
| `/finish` | End of session — checks git state, captures context, flags loose ends |
| `/resharper` | Run JetBrains static analysis beyond Roslyn analyzers |
| `/nuget-vuln-check` | Check NuGet packages for known vulnerabilities |

## Agents (not invoked directly)

These are used internally by the skills above:

| Agent | Used by | Purpose |
|-------|---------|---------|
| `batch-worker` | `/execute-sprint` | Implements a batch of issues with review loops |
| `spec-compliance-reviewer` | `/spec-review`, batch-worker | Checks code against issue acceptance criteria |
| `ef-migration-reviewer` | Manual (before committing migrations) | Reviews EF Core migrations for common traps |

## Typical Session Flow

1. **`/sprint`** — see what needs doing, get batches
2. **`/execute-sprint batch N`** — let the swarm do the work (or work manually)
3. **`/spec-review`** — verify before PR (automatic in execute-sprint)
4. **`/code-review`** — quality check before PR (automatic in execute-sprint)
5. **Merge or land the QA PR** — `peterdrier/Humans` drives the QA deploy path
6. **`/test-site`** — smoke test the QA deployment
7. **Open the upstream production PR** — only after QA passes, target `nobodies-collective/Humans`
8. **`/finish`** — clean up, capture context
