# Daily Codex tech-debt runner

Unattended nightly `codex exec` pass against a **dedicated clone** of this
repo — never your working checkout. Gates on build + test before pushing,
opens one PR per night if there's something real and green, and is a quiet
no-op otherwise. Scheduler: systemd user timer (the only one shipped here).

## One-time setup

```bash
# 1. Dedicated clone — this is what the runner refreshes/resets every run.
git clone https://github.com/peterdrier/Humans.git ~/.humans-debt-runner/clone
touch ~/.humans-debt-runner/clone/.codex-runner-clone   # marker: "safe to hard-reset/clean this dir"

# 2. Config
cd ~/.humans-debt-runner/clone/.codex/cron
cp debt-runner.env.example debt-runner.env
$EDITOR debt-runner.env   # fill in REPO_URL, OPENAI_API_KEY at minimum

# 3. gh auth — the runner uses gh's already-logged-in credentials for both
#    push and PR creation. Do this as the same user the timer will run as.
gh auth status || gh auth login

# 4. codex on PATH and authenticated (whatever `codex login` your install needs)
codex --version
```

The clone at `~/.humans-debt-runner/clone` is disposable — the script hard
resets it to `origin/main` and cleans untracked files on every run. If you
ever need to nuke it, just delete the directory and redo step 1.

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
# live somewhere these defaults don't cover. Adjust humans-debt.timer's
# OnCalendar= if 02:00 local time isn't quiet on your machine.

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

Run the script directly with a short budget first, so a bad config fails in
two minutes instead of at 2am:

```bash
TIME_BUDGET=2m ~/.humans-debt-runner/clone/.codex/cron/run-daily-debt.sh
```

Or via systemd, to also exercise the unit file / PATH / journal wiring:

```bash
systemctl --user start humans-debt.service
journalctl --user -u humans-debt.service -f
```

A short `TIME_BUDGET` almost always ends in `no-op` (codex won't get far
enough to commit) — that's expected and confirms the plumbing works. Watch
for the preflight checks (codex on PATH, `OPENAI_API_KEY` set, `gh auth
status`) passing before codex even starts.

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

- Never pushes to `main`, never opens a draft PR — a run either produces a
  ready-for-review PR against `origin/main`, or produces nothing.
- Never pushes if `dotnet build` or `dotnet test` fails after codex's pass —
  the failure is logged, the branch stays local, exit code is non-zero.
- Never runs codex at all if `gh auth status`, `OPENAI_API_KEY`, or `codex`
  on `PATH` fail preflight — fails in seconds, before spending anything.
- One branch/PR per calendar day (`$BRANCH_PREFIX/YYYY-MM-DD`); a second run
  the same day is a no-op if that day's branch already exists on origin.
- A second run while one is still in flight is a no-op (`flock`), not a
  pile-up.
