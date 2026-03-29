---
name: execute-sprint
description: "Execute sprint batches as an agent swarm — parallel worktrees, spec compliance review per issue, code review per batch, fix loops until clean, one PR per batch. Use when the user says 'execute sprint', 'run batch', 'execute batch', 'start the swarm', 'run the sprint', or wants to execute work from a sprint plan."
argument-hint: "batch 2 | batch 2,3,5 | all | all --dry-run"
---

# Execute Sprint — Agent Swarm Orchestrator

Read a sprint plan produced by `/sprint`, then launch autonomous batch-worker agents in parallel worktrees to implement, review, and PR each batch.

## Architecture

```
/execute-sprint all
    │
    ├─ Parse local/sprint-{date}.md
    ├─ Fetch issue specs from GitHub
    │
    ├─ Parallel-safe batches → concurrent agents in worktrees
    │   ├─ Agent A (worktree: .worktrees/batch-2) → implements → reviews → PR
    │   ├─ Agent B (worktree: .worktrees/batch-3) → implements → reviews → PR
    │   └─ Agent C (worktree: .worktrees/batch-4) → implements → reviews → PR
    │
    ├─ Sequential batches (migrations) → one at a time, in order
    │   └─ Agent D (worktree: .worktrees/batch-5) → implements → reviews → PR → merge → next
    │
    └─ Collect results → summary report
```

## Input: `$ARGUMENTS`

| Argument | Behavior |
|----------|----------|
| `batch 2` | Execute only batch 2 |
| `batch 2,3,5` | Execute batches 2, 3, and 5 |
| `all` | Execute all batches, respecting parallel/sequential flags |
| `all --dry-run` | Parse and validate the plan, show what would run, but don't execute |
| *(empty)* | Same as `all` |

## Step 1: Find and parse the sprint plan

Look for the most recent sprint plan:
```bash
ls -t local/sprint-*.md | head -1
```

If no plan found, tell the user to run `/sprint` first.

Parse the plan to extract:
- Each batch: number, name, priority, issues, work order, parallel-safe flag, dependencies, migration flag
- The migration sequence (if any)

Validate:
- All referenced issues exist (quick `gh issue view --json number` check)
- No circular dependencies between batches
- Migration batches have an explicit sequence

If `--dry-run`: show the parsed plan, which batches would run in parallel vs sequential, and stop.

## Step 2: Fetch issue specs

For each unique issue across all batches being executed, fetch the full spec:
```bash
gh issue view {number} --repo nobodies-collective/Humans --json title,body
```

Extract acceptance criteria from each issue body. Store as structured data to pass to batch workers.

**Do this BEFORE launching agents** — fetch once, pass to each agent. Don't make each agent fetch independently.

## Step 3: Determine execution plan

Group batches into execution waves:

**Wave 1:** All parallel-safe batches with no dependencies → launch concurrently
**Wave 2:** Batches that depend on Wave 1 batches → launch after Wave 1 completes
**Migration sequence:** Migration batches run strictly sequentially per the sprint plan's migration order, regardless of wave grouping

Example:
```
Wave 1 (parallel): Batch 1, Batch 3, Batch 4
Wave 2 (sequential, migrations): Batch 2 → Batch 5
Wave 3 (depends on Batch 2): Batch 6
```

## Step 4: Launch agents

For each batch in the current wave, launch a batch-worker agent:

1. **Create a worktree branch** from current `main`:
   ```bash
   git worktree add .worktrees/batch-{N}-{date} -b sprint/{date}/batch-{N}
   ```

2. **Build the agent prompt** using the template in Step 6 below.

3. **Launch the agent** using the Agent tool with:
   - `subagent_type`: general-purpose
   - `isolation`: do NOT use worktree isolation (we manage our own worktrees)
   - `mode`: bypassPermissions (autonomous execution)
   - `run_in_background`: true for parallel batches, false for sequential/migration batches
   - `name`: `batch-{N}` (for SendMessage if needed)

4. **For parallel batches:** Launch all in one message (multiple Agent tool calls), then wait for notifications.

5. **For sequential/migration batches:** Launch one, wait for completion, verify PR was created, then launch next.

## Step 5: Monitor and collect results

As each agent completes:
- Read the agent's result (success report or failure report)
- Track: batch number, status (complete/blocked/failed), PR URL, issues completed, issues blocked

If a batch fails:
- Log the failure reason
- Do NOT retry automatically — the agent already did 3 fix iterations
- Continue with other batches that don't depend on the failed one
- Skip batches that depend on the failed one (mark as "skipped — dependency failed")

## Step 6: Agent prompt template

Build each agent's prompt from this template. The prompt must be self-contained — the agent has no access to the sprint plan file or conversation context.

```
You are a batch worker agent executing sprint batch {N}: "{batch_name}".

## Your task

Implement the following issues in a worktree at `.worktrees/batch-{N}-{date}`.
Work in this directory for all commands. Your branch is `sprint/{date}/batch-{N}`.

Follow the batch worker process documented in `.claude/agents/batch-worker.md`.

## Project context

Read these files before starting:
- `CLAUDE.md` — project rules and architecture
- `.claude/CODING_RULES.md` — mandatory coding rules
- `.claude/CODE_REVIEW_RULES.md` — code review checklist
- `.claude/DATA_MODEL.md` — data model reference

## Work order

{For each issue in the batch work order:}

### Issue #{number}: {title}
**Description:** {brief description from sprint plan}

**Full spec:**
{Paste the full issue body here}

**Acceptance criteria:**
{Extracted AC list with IDs}

{End for each}

## Review gates

1. **After implementing each issue:** Run spec compliance review per `.claude/agents/spec-compliance-reviewer.md`. Compare your code against EVERY acceptance criterion. Max 3 fix iterations.
2. **After all issues:** Run code review per `.claude/CODE_REVIEW_RULES.md`. Check every rule. Max 3 fix iterations.
3. **Before PR:** Run `dotnet format Humans.slnx --verify-no-changes`.

## PR target

Create PR to `main` on `peterdrier/Humans`.
Title: "{batch_name} (#{issue1}, #{issue2}, ...)"
Reference all issue numbers in the PR body.

## Critical rules

- If the user sends a message, STOP and answer immediately.
- NEVER skip review gates. Every issue gets spec-reviewed. Every batch gets code-reviewed.
- If spec review fails 3 times on an issue, STOP. Do not continue to the next issue.
- Read the ORIGINAL issue spec when reviewing, not your own summary.
```

## Step 7: Summary report

After all waves complete, output:

```
## Sprint Execution Report — {date}

### Waves
| Wave | Batches | Mode | Status |
|------|---------|------|--------|
| 1 | 1, 3, 4 | Parallel | ✅ Complete |
| 2 | 2, 5 | Sequential (migrations) | ⚠️ Batch 5 blocked |
| 3 | 6 | Sequential (depends on 2) | ⏭️ Skipped |

### Batch Results
| Batch | Name | Issues | Spec | Code | PR | Status |
|-------|------|--------|------|------|----|--------|
| 1 | Shift fixes | 3/3 | PASS | PASS | #72 | ✅ Complete |
| 2 | Data model | 2/2 | PASS | PASS | #73 | ✅ Complete |
| 3 | UI cleanup | 4/4 | PASS | PASS | #74 | ✅ Complete |
| 4 | Profile | 2/2 | PASS | PASS | #75 | ✅ Complete |
| 5 | Ticket widget | 1/3 | FAIL | — | — | ❌ Blocked at #264 |
| 6 | Admin pages | — | — | — | — | ⏭️ Skipped |

### Needs Human Review
- **Batch 5, Issue #264:** Spec review failed 3 times on AC2 ("Card displays confirmation when user is matched to tickets"). Implementation kept querying aggregate data instead of per-user. Needs architectural decision about how to look up current user's tickets.

### PRs Created
- #72: peterdrier/Humans — Shift fixes (#176, #180, #184)
- #73: peterdrier/Humans — Data model (#190, #195)
- #74: peterdrier/Humans — UI cleanup (#188, #191, #192, #194)
- #75: peterdrier/Humans — Profile (#186, #189)
```

## Worktree Cleanup

After the report is generated, clean up worktrees for completed batches:
```bash
git worktree remove .worktrees/batch-{N}-{date}
```

Keep worktrees for failed/blocked batches — the user may want to inspect or continue them.

## Concurrency Limits

- **Max parallel agents:** 3 (to avoid overwhelming the machine — this is a NUC, not a build farm)
- If more than 3 parallel-safe batches exist, queue them: launch 3, wait for one to finish, launch next.
- Migration batches always run one at a time regardless of limits.

## Failure Escalation

When a batch fails, the report should give enough context for a human to decide:
1. **What was the spec?** — quote the failing acceptance criterion
2. **What did the code do instead?** — describe the drift
3. **What was tried?** — summarize the 3 fix attempts
4. **What's the likely root cause?** — architectural misunderstanding, missing data, wrong entity, etc.

This turns a failure into an actionable decision, not a mystery.
