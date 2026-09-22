# Steward conventions for Humans

Read by the `pd:steward` skill (the steward and its round worker). Where this conflicts with
the skill, this file wins.

## Who decides

- **Peter merges.** "The user" in the skill is Peter: his questions, his change requests, and
  only he raises the review-round ceiling ([`review-round-budget`](../memory/process/review-round-budget.md)).
- A question from Peter gets an answer, not a commit
  ([peters-working-rules](../docs/architecture/peters-working-rules.md)).
- A builder skill may set a lower ceiling (`/debt-review`: 3); it goes in every worker
  brief as `Ceiling: <c>`. Read the skill's ceiling table with 4 as `c-1` and 5+ as `c+`.
- Never schedule check-ins ([`no-scheduled-pr-checkins`](../memory/process/no-scheduled-pr-checkins.md)).

## Triage

Run the [`/fix`](skills/fix/SKILL.md) gates on every unresolved thread and print its triage
table into the report file. Posture and mechanics:
[`review-finding-triage`](../memory/process/review-finding-triage.md) ·
[`pr-review-feedback-handling`](../memory/process/pr-review-feedback-handling.md). Fork PRs
carry threads on both `peterdrier/Humans` and `nobodies-collective/Humans`: check both.

## Verification gate

Before any push, both clean, against a fresh build:

```bash
dotnet build Humans.slnx -v quiet            # grep "Error(s)": 0
dotnet test Humans.slnx -v quiet --no-build  # no "Failed!"
```

Skipped `Humans.Integration.Tests` entries are by design, not a failure
([`integration-tests-are-not-ci-tests`](../memory/process/integration-tests-are-not-ci-tests.md)).

## Workspace and push

- Locally, reuse the builder's worktree or add `.claude/worktrees/steward-<N>`
  ([`always-use-worktree`](../memory/process/always-use-worktree.md)); a cloud run uses the repo root.
- In the cloud, push by URL ([`push-by-url-in-cloud`](../memory/process/push-by-url-in-cloud.md)).
