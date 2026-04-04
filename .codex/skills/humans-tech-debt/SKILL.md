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

## Working Loop

1. Read [humans-tech-debt-rules.md](./references/humans-tech-debt-rules.md).
2. Pick one high-value debt item at a time.
3. Make the smallest coherent improvement that reduces divergence, duplication, or misplaced responsibility.
4. Add or extend tests when practical.
5. Run targeted verification, plus `dotnet build Humans.slnx --disable-build-servers -v q`.
6. Periodically run `dotnet test Humans.slnx --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application"`.
7. Commit each improvement separately and push the branch after verified progress.
8. Continue until remaining ideas are low-value, speculative, or blocked by forbidden areas.

## Parallel Work

- Use subagents only for independent exploration or disjoint implementation slices.
- Keep ownership separated to avoid conflicting edits.
- Review and integrate each result before pushing.

## Output

- Leave the worktree clean when stopping.
- Report the branch, worktree path, commits made, verification run, and any unresolved risks.
