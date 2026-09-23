# Daily Codex tech-debt runner

Unattended nightly native Codex goal against a **dedicated clone** of this
repo — never your working checkout. Gates on build + test before publishing,
opens one PR per run containing all substantive fixes. Ledger-only and test-only runs
fail without publishing. Scheduler: systemd user timer (the only one shipped here).

The PR is then reviewed by a Claude cloud routine at 08:00 UTC running
[`/debt-review`](../../.claude/skills/debt-review/SKILL.md): per-commit keep/repair/revert,
bot findings, follow-up issues, then stewards the PR inline (3-round ceiling).

Both nightly runs and manual trials exclude `Humans.Integration.Tests`, using
the same `FullyQualifiedName!~Humans.Integration.Tests` filter as CI. The runner
exports it as `VSTestTestCaseFilter` for Codex's test commands and passes it
explicitly to its own test gate.

## Production-code priorities

The worker fixes production code and executable tooling. Standalone coverage
work and test cleanup are not sweep objectives. Add or update focused tests
when a code fix warrants them; prefer existing coverage when sufficient.
Scoped tests validate each change; the wrapper runs the full non-integration
suite once at the end. Test-only changes do not qualify a run for publication.

## Work window and completion

`TIME_BUDGET` (default `90m`) is the minimum active work window. The runner
sets a native goal; the agent completes independently validated fixes and
stops only after the deadline **and** finishing its current task. Time is
the only target; there is no fix-count target. Ledger cleanup and
documentation do not count as substantive fixes. Report the actual work after
completion. One branch and one PR contain the whole run.

The wrapper starts one `codex app-server --stdio` process, creates one thread
and its native goal, and submits one initial turn. It stays attached while
Codex's own goal scheduler continues across normal turn boundaries. If Codex
completes the goal early, the wrapper waits for that turn to finish, reactivates
the same goal, and submits a continuation with the actual clock and original
deadline. Repeated early completions are handled the same way; neither the
work window nor the session is reset. Completion uses native goal and turn status,
not an agent-written "done" flag. The process stays in dangerous mode
(`approvalPolicy=never`, `sandbox=danger-full-access`) for the entire goal.

The last completed turn supplies the cumulative Markdown PR body, followed
by the wrapper's measured goal time, actual worker time, total elapsed time
through validation, and gate result. No unfilled template is appended.
Failed/blocked goals, missing reports, or disconnection fail without publishing. A clean tree and final build/test gates still apply.

If the final build or non-integration test gate fails, the wrapper starts up to
`GATE_REPAIR_ATTEMPTS` (default `2`) short Codex repair passes. Each pass gets
the failure excerpt, must stay on the current branch, commit its repair, and
has a separate `GATE_REPAIR_BUDGET` (default `15m`). The wrapper reruns the
build and tests after each successful repair and still refuses to publish
unless the final tree is clean and both gates pass.

The deadline is not a kill timer. Finishing the active task and the wrapper's
final build/test gates may extend past it. The systemd unit uses
`TimeoutStartSec=infinity`; copy the updated unit and reload systemd when
upgrading an existing installation. The lock still prevents overlapping runs.

## One-time setup

```bash
# 1. Dedicated clone — this is what the runner refreshes/resets every run.
git clone https://github.com/peterdrier/Humans.git ~/.humans-debt-runner/clone
touch ~/.humans-debt-runner/clone/.codex-runner-clone   # marker: "safe to hard-reset/clean this dir"

# 2. Config
cd ~/.humans-debt-runner/clone/.codex/cron
cp debt-runner.env.example debt-runner.env
$EDITOR debt-runner.env   # REPO_URL is the only value you must set

# 3. codex sign-in — plan quota, not an API key. Sign in as the SAME user
#    the timer runs as, or the timer won't see the credential.
codex login
codex login status   # expect a signed-in result

# 4. gh auth — the runner uses gh's already-logged-in credentials for both
#    push and PR creation. Do this as the same user the timer will run as.
gh auth status || gh auth login

# 4. codex on PATH and authenticated (whatever `codex login` your install needs)
codex --version
```

The clone at `~/.humans-debt-runner/clone` is disposable — the script hard
resets it to `origin/main` and cleans untracked files on every run. If you
ever need to nuke it, just delete the directory and redo step 1.

`CODEX_MODEL`/`CODEX_EFFORT` are pinned explicitly (default `gpt-5.6-terra`
/ `medium`) rather than left to inherit whatever the interactive `codex`
config on this machine happens to be set to — otherwise changing Peter's
own day-to-day model preference would silently change what the nightly job
runs too. Override in `debt-runner.env` if you want the nightly job on a
different model/effort than the default.

## Installing the systemd timer (Linux)

**User unit, not system unit.** `gh auth` and any codex login state live in
this user's home directory (`~/.config/gh`, etc.), so the timer needs to run
as that user with that user's environment — a system-wide unit would need a
separate, more painful auth story for no benefit here.

```bash
mkdir -p ~/.config/systemd/user
cp ~/.humans-debt-runner/clone/.codex/cron/humans-debt.service ~/.config/systemd/user/
cp ~/.humans-debt-runner/clone/.codex/cron/humans-debt.timer   ~/.config/systemd/user/

# Adjust humans-debt.service's Environment=PATH= line if dotnet/codex/gh
# live somewhere these defaults don't cover — order matters, since a stale
# system-wide binary earlier on PATH will silently win over a newer
# user-local one. Each run logs the resolved path + version of codex,
# dotnet, git and gh at preflight, so a PATH regression shows up in the log.
# Adjust humans-debt.timer's OnCalendar= if 06:00 UTC isn't quiet on your
# machine. Keep the trailing UTC, or systemd reads the time in the machine's
# local zone instead.

systemctl --user daemon-reload
systemctl --user enable --now humans-debt.timer

# User units normally stop when you log out. Since this needs to fire
# overnight with nobody logged in:
loginctl enable-linger "$USER"
```

Check it's armed:

```bash
systemctl --user list-timers humans-debt.timer
```

## Testing a run manually

Run a shorter work window while keeping the same completion and publication
rules (the active task and final gates may extend past 15 minutes):

```bash
TIME_BUDGET=15m ~/.humans-debt-runner/clone/.codex/cron/run-daily-debt.sh
```

Or run the scheduled configuration, which uses the full default window:

```bash
systemctl --user start humans-debt.service
journalctl --user -u humans-debt.service -f
```

A successful end-to-end trial ends with `exit_reason=pushed`, `build=pass`,
`test=pass`, and a PR URL in `SUMMARY`. Starting Codex, completing bookkeeping,
or passing tests in a separate command is not a successful runner trial.

Run the wrapper's isolated regression checks (fake Codex/GitHub/.NET, real
throwaway Git repositories; no integration tests or remote publication):

```bash
python3 .codex/cron/test_daily_debt.py
```

## Logs

- File log: `$LOG_DIR/debt-YYYY-MM-DD.log` (default
  `~/.humans-debt-runner/logs/`), one file per calendar day, appended across
  runs. Ends with a one-line `SUMMARY ...` you can `grep` across days.
- Journal (systemd path only): `journalctl --user -u humans-debt.service`.
- Logs older than `LOG_RETENTION_DAYS` (default 30) are pruned at the start
  of each run.

## Disabling

```bash
systemctl --user disable --now humans-debt.timer
```

Leaves the dedicated clone and config in place — `enable --now` again to
resume. To remove entirely, also delete
`~/.config/systemd/user/humans-debt.{service,timer}` and
`~/.humans-debt-runner/`.

## What it will and won't do

- Never pushes to `main`. A green run produces a ready-for-review PR; an
  unrepaired gate failure produces a draft PR for follow-up repair.
- A bounded repair pass may be attempted after a gate failure. If the final
  build or test gate is still red, the runner pushes the committed branch and
  opens a draft PR with the failure warning and log excerpt so the follow-up
  reviewer can repair it. The service still exits non-zero to keep the failure
  visible.
- Never runs codex at all if `gh auth status`, the codex sign-in, or `codex`
  on `PATH` fail preflight — fails in seconds, before spending anything.
- One branch/PR per calendar day (`$BRANCH_PREFIX/YYYY-MM-DD`); a second run
  the same day is a no-op if that day's branch already exists on origin.
- Never runs while one of its own PRs is still open. Every run edits the same
  ledger files (`next_id` in `docs/architecture/debt-ledger.yml` and the
  per-section `debt.yml`), so two unmerged runs from the same base conflict on
  the counter alone, and the second re-reads a ledger that still shows the
  first one's work as open. `MAX_OPEN_AUTO_PRS` (default 1) caps how many of
  this runner's PRs may be open before a night is skipped
  (`exit_reason=skip-open-auto-pr`). **Idling while a PR waits for review is
  the intended behaviour** — the job produces work at the rate you merge it.
  Merge or close the PR and the next night runs. If `gh` can't answer, the run
  fails closed rather than risk opening a second one.
- A second run while one is still in flight is a no-op (`flock`), not a
  pile-up.
