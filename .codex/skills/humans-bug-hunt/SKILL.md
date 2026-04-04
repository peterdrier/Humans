---
name: humans-bug-hunt
description: Autonomous bug-hunt workflow for the Humans repository. Use when the user wants Codex to scan for real bugs, work in a dedicated git worktree branched from origin/main, avoid database or migration changes, commit each fix separately, push progress, and continue until diminishing returns.
---

# Humans Bug Hunt

Run recurring autonomous bug-hunt passes in this repository.

## Start Or Resume

1. Confirm the repo root is the current Humans checkout.
2. Resume an existing `bughunt/*` worktree and branch if the user is continuing prior work.
3. Otherwise fetch `origin/main`, create a fresh branch `bughunt/YYYY-MM-DD-codex-N`, and attach a worktree at `.worktrees/bughunt-YYYY-MM-DD-codex-N`.
4. Keep scratch notes and temporary files under `local/bug-hunt-runs/<run-id>/`.

## Non-Negotiable Limits

- Do not touch database or storage behavior. Avoid `src/Humans.Infrastructure/Data/HumansDbContext.cs`, `src/Humans.Infrastructure/Data/EntityConfigurations/**`, `src/Humans.Infrastructure/Migrations/**`, and any change that alters persistence, migrations, or schema configuration.
- Do not modify `.csproj` files, `src/Humans.Web/Program.cs`, or DI registration unless the user explicitly overrides the rule.
- Do not delete files, remove controller actions, or remove public members as part of the hunt.
- Focus on real defects only. Skip refactors, style cleanup, performance work, and feature ideas.

## Working Loop

1. Read [humans-repo-rules.md](./references/humans-repo-rules.md).
2. Search for one high-confidence bug at a time, using repo patterns rather than a fixed checklist.
3. Implement the smallest defensible fix.
4. Add or extend tests when practical.
5. Run targeted verification, plus `dotnet build Humans.slnx --disable-build-servers -v q`.
6. Periodically run `dotnet test Humans.slnx --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application"`.
7. Commit each fix separately and push the branch after verified progress.
8. Continue until remaining ideas are speculative or require forbidden areas.

## Parallel Work

- Use subagents only for independent exploration or disjoint implementation slices.
- Keep ownership separated to avoid conflicting edits.
- Review and integrate each result before pushing.

## Output

- Leave the worktree clean when stopping.
- Report the branch, worktree path, commits made, verification run, and any unresolved risks.
