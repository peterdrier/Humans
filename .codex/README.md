# Codex Maintenance Automation

This folder contains project-local automation for long-running Codex maintenance passes.

## Scripts

- `run-weekly-bug-hunt.sh`
  Runs Codex in an isolated git worktree, using the bug-hunt or tech-debt prompt, and resumes the same session across multiple passes.

- `cleanup-merged-bug-hunt-worktrees.sh`
  Finds merged bug-hunt worktrees and removes them safely.

## Prompts

- `bug-hunt-prompt.md`
  Autonomous bug-fix workflow.

- `tech-debt-prompt.md`
  Autonomous consolidation / tech-debt workflow.

## Default Behavior

`run-weekly-bug-hunt.sh` currently defaults to:

- `bug-hunt` mode
- dangerous Codex execution
- isolated worktree execution
- automatic push of progress to `origin`
- resume-based continuation across passes

Typical commands:

```bash
.codex/run-weekly-bug-hunt.sh
.codex/run-weekly-bug-hunt.sh --mode tech-debt
```

## Why It Works This Way

These defaults came from validating the workflow in this repository.

- Nested Codex sandboxing failed in this environment.
  Running the inner Codex session in dangerous mode avoided `bwrap` / loopback sandbox failures.

- Fresh worktrees are safer than reusing the main checkout.
  They isolate autonomous edits from local in-progress work and make cleanup straightforward.

- New runs should start from fresh `origin/main`, not local `main`.
  Local `main` may be dirty or stale. The runner fetches `origin/main` and uses that as the base when launched from `main`.

- Resume is better than full prompt restarts.
  `codex exec resume` keeps the same session and avoids replaying the full prompt every pass.

- Pushes are useful, but optional.
  Normal runs push progress so branch activity is visible in GitHub. Validation runs can use `--no-push`.

## Caveats

- The required build/test commands in this repo can be blocked by existing NuGet audit failures unrelated to the autonomous fix.
- Long autonomous runs can still make imperfect decisions; pushed branches should be reviewed before merging.
