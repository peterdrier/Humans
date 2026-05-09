---
name: execute-sprint
description: "Parse a /sprint plan and launch autonomous batch-worker agents in parallel worktrees to implement, review, and PR each batch. Use when the user says 'execute sprint', 'run batch', or 'start the swarm'."
argument-hint: "batch 2 | batch 2,3,5 | all | all --dry-run"
---

# Execute Sprint — Agent Swarm Orchestrator

Read a sprint plan produced by `/sprint`, then launch batch-worker agents in parallel worktrees to implement, review, and PR each batch.

## Architecture

```
/execute-sprint all
    │
    ├─ Parse local/sprint-{date}.md → fetch issue specs from GitHub
    │
    ├─ Parallel-safe batches → concurrent agents in worktrees
    │   ├─ Agent A (worktree: .worktrees/batch-2) → implements → reviews → PR
    │   └─ Agent B (worktree: .worktrees/batch-3) → implements → reviews → PR
    │
    ├─ Sequential batches (migrations) → one at a time, in order
    │   └─ Agent D (worktree: .worktrees/batch-5) → implements → reviews → PR → merge → next
    │
    └─ Collect results → summary report
```

## Arguments

- `batch 2` / `batch 2,3,5` — execute specific batches
- `all` — all batches, respecting parallel/sequential flags
- `all --dry-run` — validate plan and show what would run, no execution
- *(empty)* — same as `all`

## Step 1: Find and parse the sprint plan

```bash
ls -t local/sprint-*.md | head -1
```

If no plan found, tell the user to run `/sprint` first. If `--dry-run`, show the parsed plan (parallel vs sequential grouping) and stop.

Extract per batch: number, name, priority, issues, work order, parallel-safe flag, dependencies, migration flag.

## Step 2: Fetch issue specs

For each unique issue, fetch once before launching any agents:
```bash
gh issue view {number} --repo nobodies-collective/Humans --json title,body,labels,author,comments
```

Store `labels`, `author.login`, `comments`, and extracted acceptance criteria. Pass as structured data to batch workers — don't make each agent fetch independently.

## Step 2.5: Privilege / spec-change pre-flight (HARD GATE)

Scan every fetched spec before dispatching agents. Two gate paths:

**Path 1 — HARD GATE** (privilege keyword OR label hit, regardless of author):

Triggers on:
- Labels: `needs-owner-review` or `blocked:needs-design`
- Keywords in body: `permission`, `privilege`, `role`, `scope`, `allowlist`, `CORS`, `[Authorize]`, `Admin` (as a grant), `default.*level`, `fileOrganizer`, `ContentManager`, `writer`, `Contributor`, `bypass`, `override.*auth`, `AllowAnonymous`

Action: STOP, do not enter Step 3. Show Peter: issue number, title, trigger (label/keyword), matched signal(s), author login, any `fb:` feedback reference, first ~20 lines of body. Ask: "This issue would `<change>` for `<who>`. Confirm before I dispatch a worker?" Wait for explicit "yes, proceed" — silence is NOT approval. If Peter declines, drop the issue and continue with remaining sprint.

**Path 2 — Spec-change only** (no privilege keyword, no label hit):

Triggers on body keywords: `eligibility`, `workflow step`, `consent step`, `new endpoint`, `public api`, `who can`, `policy change`, `default behavior`

- If `author.login == "peterdrier"` AND no `fb:[a-f0-9]+` reference → **soft notice only**: print one line and proceed. Peter authoring the issue IS his approval.
- Otherwise (non-Peter author OR `fb:` reference) → fall through to Path 1 (hard gate, steps 1–5).

Author and feedback-id signals don't gate on their own, but call them out in Path 1 displays so Peter sees whether this is a re-confirmation of his own decision or not.

This gate fires even when the sprint plan already labeled the issue with a tier — sprint planning is fallible. Never include a privilege-change or spec-change issue in a worker prompt without this gate having fired and Peter having approved.

Per `memory/process/privilege-changes-need-explicit-approval.md` and `memory/process/user-feedback-spec-changes-need-review.md`.

## Step 3: Determine execution plan

Group into waves:
- **Parallel wave:** all parallel-safe batches with no unmet dependencies → launch concurrently
- **Dependency waves:** batches that depend on prior waves → launch after those waves complete
- **Migration sequence:** migration batches run strictly sequentially per sprint plan order, regardless of wave

Example: `Wave 1 (parallel): 1, 3, 4` → `Wave 2 (sequential/migrations): 2 → 5` → `Wave 3 (depends on 2): 6`

## Step 4: Launch agents

Per batch:

1. Create worktree branch from current `main`:
   ```bash
   git worktree add .worktrees/batch-{N}-{date} -b sprint/{date}/batch-{N}
   ```

2. Build agent prompt (see Step 6) — must be self-contained; agent has no access to sprint plan or conversation context.

3. Launch via Agent tool: `subagent_type: general-purpose`, no worktree isolation (we manage our own), `mode: bypassPermissions`, `run_in_background: true` for parallel / `false` for sequential, `name: batch-{N}`.

4. Parallel batches: launch all in one message, then wait for notifications. Sequential/migration batches: launch one, wait for completion and PR creation, then launch next.

## Step 5: Monitor and collect results

Track per batch: status (complete/blocked/failed), PR URL, issues completed, issues blocked. On failure: log reason, do NOT retry (agent already did 3 fix iterations), continue batches that don't depend on it, skip those that do.

## Step 6: Agent prompt template

Self-contained prompt passed to each batch worker. Include: worktree path, branch name, pointer to `.claude/agents/batch-worker.md`, list of project context files to read (`CLAUDE.md`, `memory/INDEX.md`, `docs/architecture/design-rules.md`, `docs/architecture/code-review-rules.md`, `docs/architecture/data-model.md`), full issue body + extracted AC per issue in work order, review gates (spec-compliance review per issue per `.claude/agents/spec-compliance-reviewer.md`, code review per batch per `docs/architecture/code-review-rules.md`, `dotnet format --verify-no-changes` before PR), PR target (`main` on `peterdrier/Humans`, title format: `{batch_name} (#{issue1}, ...)`, all issue numbers in body), and critical rules: stop immediately if user sends a message; never skip review gates; stop if spec review fails 3 times on one issue; read original issue spec during review, not your own summary.

## Step 7: Summary report

After all waves complete:

```
## Sprint Execution Report — {date}

### Waves
| Wave | Batches | Mode | Status |
|------|---------|------|--------|
| 1 | 1, 3, 4 | Parallel | ✅ Complete |
| 2 | 2, 5 | Sequential (migrations) | ⚠️ Batch 5 blocked |

### Batch Results
| Batch | Name | Issues | Spec | Code | PR | Status |
|-------|------|--------|------|------|----|--------|
| 1 | Shift fixes | 3/3 | PASS | PASS | #72 | ✅ Complete |
| 5 | Ticket widget | 1/3 | FAIL | — | — | ❌ Blocked at #264 |

### Needs Human Review
- **Batch 5, Issue #264:** [what AC failed, what the code did instead, likely root cause]

### PRs Created
- #72: peterdrier/Humans — Shift fixes (#176, #180, #184)
```

## Worktree Cleanup

After the report, clean up completed batches:
```bash
git worktree remove .worktrees/batch-{N}-{date}
```

Keep worktrees for failed/blocked batches.

## Concurrency Limits

Max 3 parallel agents (NUC, not a build farm). If more than 3 parallel-safe batches exist, queue: launch 3, wait for one to finish, launch next. Migration batches always run one at a time.

## Failure Escalation

Report must give enough context for a human decision. Per failed issue: (1) the failing AC verbatim, (2) what the code did instead, (3) summary of the 3 fix attempts, (4) likely root cause.
