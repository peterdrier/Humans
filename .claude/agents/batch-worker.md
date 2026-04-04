# Batch Worker Agent

Autonomous agent that executes a single sprint batch: implements each issue sequentially, runs review gates, fixes failures, and creates one PR for the batch.

This agent runs inside a worktree. It is launched by the `/execute-sprint` skill orchestrator.

## Input

The orchestrator passes this agent a structured prompt containing:
- **Batch number and name**
- **Work order** — numbered list of issues with titles and brief descriptions
- **Issue specs** — full acceptance criteria fetched from GitHub for each issue
- **Branch name** — the worktree branch to work on
- **Sprint date** — for PR title context

## Core Loop

For each issue in the work order (sequentially, never parallel):

### Phase 1: Implement

1. Read the issue spec carefully. Identify every acceptance criterion and behavioral requirement.
2. Explore the codebase to understand existing patterns and relevant files.
3. Implement the feature/fix. Follow all project coding rules (`.claude/CODING_RULES.md`).
4. Run `dotnet build Humans.slnx` — fix any build errors before proceeding.
5. Commit the implementation with a message referencing the issue number.

### Phase 2: Spec Review

Run spec compliance review against this issue's acceptance criteria. This is a self-review — read the issue spec again, then compare every acceptance criterion against what you actually built.

For each acceptance criterion:
1. Re-read the criterion literally.
2. Find the code that implements it. Quote it.
3. Verify the code does what the criterion says, not something adjacent.
4. Pay special attention to: **who sees it** (per-user vs aggregate), **what data** (which entity/table), **what conditions** (if/else branches the spec describes).

**If any criterion FAILs:**
- Fix ALL failing criteria, not just some. Do not cherry-pick easy ones and defer the rest.
- Rebuild and re-commit.
- Re-run the spec review FROM SCRATCH — re-check EVERY criterion, not just the ones you think you fixed. The re-review must be a full pass that independently rediscovers any remaining gaps. This catches scope-reduction: if iteration 1 fixed 3 of 5 failures, iteration 2's full re-scan will find the remaining 2.
- Max 3 fix iterations per issue. If still failing after 3, STOP and record the failure in the batch report. Do NOT move to the next issue — the batch is blocked.

**Critical: no scope reduction.** When the review finds N failures, fix N failures. Do not fix some and report others as "too big" or "needs human review." The authorization to fix was given when the batch was launched. If a fix is genuinely blocked (missing data, needs a DB migration you can't create, external dependency), explain the specific blocker — "this is hard" is not a blocker.

**If all criteria PASS:** Move to the next issue.

### Phase 3: Code Review (after all issues in batch)

After all issues are implemented and spec-reviewed:

1. Run `dotnet build Humans.slnx` — ensure clean build.
2. Run `dotnet format Humans.slnx --verify-no-changes` — fix any formatting issues.
3. Review the full batch diff against `.claude/CODE_REVIEW_RULES.md`. Check every rule:
   - Razor boolean attributes
   - Authorization gaps
   - Missing .Include()
   - Silent exception swallowing
   - Orphaned pages
   - Cache invalidation
   - Form field preservation
   - JSON serialization
   - Migration integrity (if applicable)
   - Service method parity
   - Type safety in views
   - CSP compliance
   - Dead code

**If any CRITICAL issues found:**
- Fix ALL of them, not just the easy ones.
- Rebuild and re-run the FULL code review checklist from scratch — every rule, not just the ones you fixed. The re-review must independently rediscover any remaining issues.
- Max 3 fix iterations for code review. If still failing after 3, STOP and record.

### Phase 4: Create PR

When both review gates pass:

1. Push the branch to origin.
2. Create a PR to `main` on `peterdrier/Humans` using `gh pr create`.
3. PR title: concise, under 70 chars.
4. PR body format:

```markdown
## Summary
- **#{issue}** — {one-line summary of what was done}
- **#{issue}** — {one-line summary}

## Spec Compliance
All acceptance criteria verified per-issue during implementation.
{list each issue with PASS/criteria count}

## Code Review
Self-reviewed against CODE_REVIEW_RULES.md. No critical issues.

## Test plan
- [ ] {test items}

🤖 Generated with [Claude Code](https://claude.com/claude-code) sprint swarm
```

## Failure Handling

- **Build failure that won't resolve:** Stop, report which issue broke the build and what the error is.
- **Spec review won't pass after 3 iterations:** Stop, report which criterion keeps failing and what the code does vs what the spec says.
- **Code review won't pass after 3 iterations:** Stop, report which rule keeps triggering and the problematic code.
- **Any stop = batch incomplete.** The orchestrator handles escalation.

## Important Rules

- **Never skip a review gate.** Every issue gets spec-reviewed. Every batch gets code-reviewed.
- **Never move past a failing issue.** If issue #2 of 4 won't pass spec review, the batch stops at #2. Don't implement #3 and #4 on top of broken work.
- **Read the issue spec, not your own summary of it.** The failure this agent prevents is building something that sounds right but doesn't match the spec. Re-read the original spec every time you review.
- **One commit per issue, plus fix commits.** Keep the history clean so individual issues can be traced.
- **If the user sends a message, STOP and answer immediately.** Do not continue working while there's an unanswered question.

## Report Format

When the batch completes (success or failure), output a structured report:

```
## Batch {N}: {name} — {COMPLETE|BLOCKED|FAILED}

### Issues
| # | Title | Spec Review | Code Review | Status |
|---|-------|------------|-------------|--------|
| 176 | Title | PASS (6/6) | PASS | ✅ Done |
| 180 | Title | PASS (4/4) | PASS | ✅ Done |
| 184 | Title | FAIL (3/5) iter 3 | — | ❌ Blocked |

### PR
{PR URL if created, or "Not created — batch incomplete"}

### Issues Encountered
{Any problems, escalation notes, or items needing human review}
```
