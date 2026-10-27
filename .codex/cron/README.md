# Daily Codex tech-debt runner

Unattended nightly native Codex goal against a **dedicated clone** of this
repo — never your working checkout. Gates on build + test before publishing,
opens one PR per run containing all substantive fixes. Ledger-only and test-only runs
fail without publishing. Scheduler: systemd user timer (the only one shipped here).

A separate Claude cloud routine currently runs at 08:00 UTC using
[`/debt-review`](../../.claude/skills/debt-review/SKILL.md): per-commit keep/repair/revert,
bot findings, follow-up issues, then stewards the PR inline (3-round ceiling).
The nightly timer starts at 00:00 UTC, so a full 12-hour run can publish after
that review slot. Review scheduling is separate from this systemd timer.

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

`TIME_BUDGET` defaults to `12h` and cannot exceed 12 hours. The worker finishes
its active batch afterward; final build/test and publication may extend past it.
The service pins only the quota guard; `TIME_BUDGET` in `debt-runner.env`
remains effective for scheduled runs. Set it to `12h` for the full window, or
choose a shorter duration.

There is no fix-count target. Work existing ledger rows before searching for new
debt. One branch and PR contain all completed substantive fixes.

Before starting work and after each validated, committed round, the runner reads
live quota through `account/rateLimits/read`. It selects the `codex` bucket's
seven-day window by duration, not by primary/secondary position. The minimum
remaining percentage is `max(16, 100 - 12 * (elapsed_days_since_weekly_reset + 1))`.
The next day's allowance is available upfront: immediately after reset, stop below
88% remaining; at 1.5 days, stop below 70%. The cutoff is fixed at the run's first
quota read and renewed only if the weekly reset timestamp changes. Later runs
release more allowance; unused allowance carries forward within the week. At
least 16% is reserved. All account usage, including interactive work, counts.
Missing, invalid, stale or failed quota reads stop new work. Another exhausted
returned window also stops work. No earned reset credits are consumed.

Each round completes the native goal and supplies a cumulative report after one
coherent batch. The wrapper checks fresh quota, then reactivates the same goal
and thread when time and quota allow another round. It does not interrupt a batch
or kill a process at a quota threshold. A quota stop before any work is a quiet
`skip-quota`; after committed fixes, normal validation and publication still run.
Gate repair sessions are exempt from the quota cutoff so completed work can be
validated. The quota check is a boundary guard, not a guarantee that a batch or
repair cannot consume additional quota.

For an intentional manual run that borrows ahead of the weekly allowance:

```bash
IGNORE_QUOTA_CUTOFF=1 TIME_BUDGET=12h ~/.humans-debt-runner/clone/.codex/cron/run-daily-debt.sh
```

Keep the override out of persistent scheduled configuration. It bypasses quota
reads but keeps the time cap, clean-tree checks and final gates. Explicit inherited `TIME_BUDGET` and `IGNORE_QUOTA_CUTOFF` values take precedence
over the environment file for manual overrides. Other settings retain environment-file
precedence.

The wrapper starts one `codex app-server --stdio` process and stays attached to
one native goal/thread across rounds. The process keeps its configured dangerous
permissions throughout, with the same original work deadline.

The last completed turn supplies the cumulative Markdown PR body, followed
by the wrapper's measured goal time, actual worker time, total elapsed time
through validation, and gate result. No unfilled template is appended.
Blocked goals return a distinct result: the wrapper records the actual commit
count, checks the branch and clean tree, and runs final build/test gates. Substantive
committed work is published as a draft with a blocked warning and Git commit list,
while the service still fails. Other goal failures, missing completion reports,
and disconnection fail without publishing. Dirty blocked runs never publish.

If the final build or non-integration test gate fails, the wrapper starts short Codex repair passes, capped at
`GATE_REPAIR_ATTEMPTS` (default `2`) in total across both gates. Each pass gets
a bounded diagnostic excerpt, must stay on the current branch, commit its repair,
and has a separate `GATE_REPAIR_BUDGET` (default `15m`). Every pass consumes the
shared attempt budget, including failed sessions and repairs that leave a gate
red. The wrapper reruns build before tests after every repair. Dirty trees or
branch changes stop publication; exhausted repairs publish a failed draft.
Only a clean tree with both gates passing produces a ready PR.

Repair prompts and failure warnings omit raw app-server JSON events and limit
individual diagnostic lines and the complete excerpt. The complete log remains
on disk. PR bodies are bounded to 60,000 UTF-8 bytes; shortened reports retain
automation warnings and the runner's validation footer. The complete original
report remains in `last-message-YYYY-MM-DD.md`, including on publication retry.

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

# 4. codex CLI 0.159.2 or newer on PATH and authenticated
#    (whatever `codex login` your install needs)
codex --version
```

The clone at `~/.humans-debt-runner/clone` is disposable — the script hard
resets it to `origin/main` and cleans untracked files on every run. If you
ever need to nuke it, just delete the directory and redo step 1.

`CODEX_MODEL`/`CODEX_EFFORT` are pinned explicitly (default `gpt-6.1-sol`
/ `medium`) rather than left to inherit whatever the interactive `codex`
config on this machine happens to be set to — otherwise changing Peter's
own day-to-day model preference would silently change what the nightly job
runs too. Override in `debt-runner.env` if you want the nightly job on a
different model/effort than the default. Existing installations with an
explicit model override must update it to adopt the new default.

Codex CLI **0.159.2 or newer is required**. The helper routing below was
verified with 0.159.2; older CLI configuration schemas are unsupported.

The runner enables subagents and pins `agents.default_subagent_model` to
`gpt-6-luna` and `agents.default_subagent_reasoning_effort` to `medium` on its
app-server process, independently of the coordinator's model and local config.
The nightly prompt permits these helpers for bounded discovery, localization
preparation, and mechanical edits. The coordinator reviews their
work and owns validation and commits; helpers never run builds/tests or Git
mutations. Small tasks stay with the coordinator, and unavailable delegation
falls back to direct work.

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
# Adjust humans-debt.timer's OnCalendar= if 00:00 UTC isn't quiet on your
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

## Pinning a runner release outside the disposable clone

The script parses itself before refreshing the clone, so its current invocation
survives that refresh. However, an `ExecStart` inside the clone uses whatever
script the previous refresh left behind on the next invocation. To deploy a
reviewed runner fix before its PR merges, provision an immutable release outside
`WORK_DIR` and override only `ExecStart`:

```bash
# Run from a checkout containing the reviewed runner commit.
runner_revision=$(git rev-parse HEAD)
runner_release="$HOME/.humans-debt-runner/releases/$runner_revision"
mkdir -p "$runner_release"
git archive "$runner_revision" .codex/cron .codex/prompts/daily-debt.md | tar -x -C "$runner_release"
ln -s "$HOME/.humans-debt-runner/clone/.codex/cron/debt-runner.env" \
  "$runner_release/.codex/cron/debt-runner.env"
mkdir -p ~/.config/systemd/user/humans-debt.service.d
cat > ~/.config/systemd/user/humans-debt.service.d/runner-release.conf <<EOF
[Service]
ExecStart=
ExecStart=$runner_release/.codex/cron/run-daily-debt.sh
EOF
systemctl --user daemon-reload
systemctl --user cat humans-debt.service
```

Provision a new release without modifying an existing release or the running
clone. `daemon-reload` changes future starts; it does not restart an active job.
Do not restart/stop the service to apply an upgrade during a run. This leaves
existing resource limits, timer settings, credentials, and the clone's branch intact. Clone
refreshes cannot overwrite the release. Repeat with a newly reviewed revision
to upgrade; the override remains pinned until explicitly changed. The pinned
wrapper still reads the clone's existing environment file. Its goal client and
daily prompt come from the same release, so a clone refresh cannot mix versions.

## Testing a run manually

Run a shorter work window while keeping the same completion and publication
rules (the active task and final gates may extend past 15 minutes):

```bash
TIME_BUDGET=15m ~/.humans-debt-runner/clone/.codex/cron/run-daily-debt.sh
```

Or run the scheduled configuration, which uses the configured window and quota cutoff:

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

## Spend comment

After creating a PR (including drafts and recovered publications), the wrapper
posts the Codex spend skill's report. It records explicit cleanup and gate-repair
session IDs, with a separate breakdown for each root and its linked subagents.
The estimate uses Standard API list rates; it is not subscription billing.
Reporting failures are logged and never block publishing validated fixes.

Install the Codex `spend` skill from
[`peterdrier/skills`](https://github.com/peterdrier/skills/tree/main/plugins/pd-codex/skills/spend)
into `${CODEX_HOME:-~/.codex}/skills/spend`, using the Skill Installer with repo
`peterdrier/skills` and path `plugins/pd-codex/skills/spend`. For a plugin install
or another location, set `SPEND_SCRIPT` to its `scripts/spend.py` path.
A missing calculator is reported visibly in the PR comment.

## Logs

- File log: `$LOG_DIR/debt-YYYY-MM-DD.log` (default
  `~/.humans-debt-runner/logs/`), one file per calendar day, appended across
  runs. Ends with a one-line `SUMMARY ...` you can `grep` across days.
- Session IDs: `$LOG_DIR/sessions-YYYY-MM-DD.jsonl`; spend comment body:
  `$LOG_DIR/spend-YYYY-MM-DD.md`. Kept outside the disposable clone for
  publication retries.
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
  unrepaired gate failure or blocked goal produces a draft PR for follow-up review.
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
