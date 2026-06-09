---
name: humans-tech-debt
description: Autonomous tech-debt reduction workflow for the Humans repository. Use when the user wants Codex to improve high-value code quality issues in a dedicated git worktree branched from origin/main, avoid database or migration changes, commit each improvement separately, push progress, and continue until diminishing returns.
---

# Humans Tech Debt

Run recurring autonomous tech-debt reduction passes in this repository.

## Start Or Resume

1. Confirm the repo root is the current Humans checkout.
2. Resume an existing `techdebt/*` worktree and branch if the user is continuing prior work.
3. Otherwise fetch `origin/main`, create a fresh branch `techdebt/YYYY-MM-DD-codex-N`, and attach a worktree at `.worktrees/techdebt-YYYY-MM-DD-codex-N`.
4. Keep scratch notes and temporary files under `local/tech-debt-runs/<run-id>/`.

## Non-Negotiable Limits

- Do not touch database or storage behavior. Avoid `src/Humans.Infrastructure/Data/HumansDbContext.cs`, `src/Humans.Infrastructure/Data/EntityConfigurations/**`, `src/Humans.Infrastructure/Migrations/**`, and any change that alters persistence, migrations, or schema configuration.
- Do not modify entity shapes, migration files, or JSON serialization attributes.
- Do not delete files, remove controller actions, or remove public members as part of the cleanup.
- Prefer structural simplification and consolidation over broad rewrites.
- Treat Reforge or any score as a detector, not an objective. A score decrease alone never justifies a commit.
- Do not add thin behavior to DTO/request/command/options/input records just to escape a parameter-bag or long-signature rule. Methods such as `ApplyTo`, `ToDto`, `RenderWith`, `BuildSummary`, `SaveAsync`, or service-delegating wrappers are acceptable only when they enforce real invariants, validation, state transitions, or domain policy that the type already owns.

## Working Loop

1. Read [humans-tech-debt-rules.md](./references/humans-tech-debt-rules.md).
2. Pick one high-value debt item at a time.
3. Before editing, write a one-sentence architecture thesis: what concept will be deleted, what responsibility will move to its rightful owner, or what duplication/coupling will disappear. If the thesis is "the score drops", abandon the candidate.
4. Make the smallest coherent improvement that reduces divergence, duplication, misplaced responsibility, or durable public surface.
5. Add or extend tests when practical.
6. Run targeted verification, plus `dotnet build Humans.slnx --disable-build-servers -v q`.
7. Run a score-blind second pass before commit. Review only the diff, the architecture thesis, and verification. Reject the change if it would not be worth keeping without metric movement.
8. Periodically run `dotnet test Humans.slnx --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application"`.
9. Commit each accepted improvement separately and push the branch after verified progress.
10. Continue until remaining ideas are low-value, speculative, blocked by forbidden areas, or only reducible through metric-gaming changes.

## Metric Integrity

- Use Reforge to find suspicious surfaces and compare checkpoints, not to decide that a weak patch is good.
- Prefer changes that remove a real concept, collapse duplicated behavior, narrow cross-section dependencies, or move a rule to its owning section.
- Reject refactors whose main effect is moving assignments, formatting strings, or service calls from one class into a request/command object without reducing coupling or clarifying ownership.
- Do not keep working toward a percentage target after the remaining candidates fail the architecture thesis or score-blind review. Report that the safe score reduction is exhausted instead.

## Parallel Work

- Use subagents only for independent exploration or disjoint implementation slices.
- Keep ownership separated to avoid conflicting edits.
- Review and integrate each result before pushing.

## Output

- Leave the worktree clean when stopping.
- Report the branch, worktree path, commits made, verification run, and any unresolved risks.
