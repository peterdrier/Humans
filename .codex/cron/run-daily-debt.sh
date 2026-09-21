#!/usr/bin/env bash
#
# Daily unattended Codex tech-debt runner.
#
# Runs a native Codex goal against a DEDICATED clone of this repo (never a
# human's working checkout), gates the result on build+test, and opens a PR
# only when there is something real and green to review.
#
# Scheduler-agnostic: this script does not know or care whether it was
# invoked by systemd, cron, or a human. See .codex/cron/README.md for the
# systemd timer that drives it in this repo.
#
# Every machine-specific value lives in the CONFIG block below, and every one
# of them can be overridden by .codex/cron/debt-runner.env (gitignored) next
# to this script, or by the calling environment. Nothing here hardcodes a
# particular machine's paths.
#
# NOTE ON SELF-MODIFICATION: this script lives inside the dedicated clone it
# refreshes (WORK_DIR), so a run rewrites its own file on disk mid-execution
# (git fetch/reset/clean). To make that safe, the entire script body is
# wrapped in main() below and invoked only as the very last line. Bash parses
# a function body fully — up to its closing brace — before executing
# anything in it, so by the time main() runs, this whole script is already
# parsed into memory; the destructive git operations inside main() cannot
# corrupt execution of the copy already running. Do not move code outside
# main() unless it truly must run before the file could change.

set -euo pipefail

# Is codex signed in? Prefer asking codex itself; fall back to the stored
# credential when this CLI version has no `login status` subcommand.
codex_auth_ok() {
  if codex login status >/dev/null 2>&1; then
    return 0
  fi
  # `login status` may not exist on this version — distinguish "no such
  # subcommand" from "genuinely signed out" by looking at what it printed.
  #
  # Capture the output first rather than piping straight into grep. Under
  # `set -o pipefail` the pipeline takes the non-zero exit of a signed-out
  # `codex login status`, which masks grep's successful match and skips the
  # `return 1` below — leaving a stale auth.json to report a signed-out CLI
  # as authenticated. That is exactly the "broken runner looks healthy"
  # failure this preflight exists to catch.
  local status_output
  status_output="$(codex login status 2>&1 || true)"
  if codex login --help >/dev/null 2>&1 && grep -qi "not logged in\|signed out" <<<"$status_output"; then
    return 1
  fi
  [[ -s "${CODEX_HOME:-$HOME/.codex}/auth.json" ]]
}

main() {
  # ============================================================
  # CONFIG — every machine-specific value, sourced from
  # debt-runner.env if present, then defaulted. Override via env
  # file, not by editing this script.
  # ============================================================
  local script_dir
  script_dir="$(dirname -- "$(readlink -f -- "${BASH_SOURCE[0]}")")"
  local env_file="$script_dir/debt-runner.env"
  if [[ -f "$env_file" ]]; then
    set -a
    # shellcheck source=/dev/null
    source "$env_file"
    set +a
  fi

  REPO_URL="${REPO_URL:-}"                                    # git remote to clone/push, e.g. git@github.com:peterdrier/Humans.git
  WORK_DIR="${WORK_DIR:-$HOME/.humans-debt-runner/clone}"      # DEDICATED clone. Never Peter's working checkout.
  TIME_BUDGET="${TIME_BUDGET:-90m}"                            # minimum active work window; finish the current task afterward
  # Pinned rather than left to inherit the interactive `codex` config, so
  # changing Peter's own day-to-day model/effort preference doesn't silently
  # change what the nightly job runs.
  CODEX_MODEL="${CODEX_MODEL:-gpt-5.6-terra}"
  CODEX_EFFORT="${CODEX_EFFORT:-medium}"
  LOG_DIR="${LOG_DIR:-$HOME/.humans-debt-runner/logs}"         # must be outside WORK_DIR (git clean would wipe it)
  BRANCH_PREFIX="${BRANCH_PREFIX:-codex/daily-debt}"           # branch = $BRANCH_PREFIX/YYYY-MM-DD
  GH_BASE_BRANCH="${GH_BASE_BRANCH:-main}"                     # base branch on origin
  CODEX_DANGEROUS="${CODEX_DANGEROUS:-1}"                      # 1 = dangerous mode on every turn, 0 = unattended workspace-write
  PUSH_RETRIES="${PUSH_RETRIES:-4}"                            # retries after the first push attempt, network failures only
  MAX_OPEN_AUTO_PRS="${MAX_OPEN_AUTO_PRS:-1}"                  # skip the night when this many of this runner's PRs are already open
  LOG_RETENTION_DAYS="${LOG_RETENTION_DAYS:-30}"
  # Applies to the wrapper and dotnet test commands launched by Codex.
  # Integration tests are outside nightly runs and manual runner trials.
  export VSTestTestCaseFilter='FullyQualifiedName!~Humans.Integration.Tests'
  readonly CLONE_MARKER_NAME=".codex-runner-clone"
  readonly PROMPT_REL_PATH=".codex/prompts/daily-debt.md"
  readonly ENV_FILE_REL_PATH=".codex/cron/debt-runner.env"     # excluded from `git clean` inside WORK_DIR

  # ---- run state, deliberately global, not local ------------------------
  # Every exit has to leave a SUMMARY line, including the ones nobody wrote
  # a branch for: a `set -e` abort on a failed fetch/reset/sed, or a signal.
  # A nightly that dies silently reads in the log exactly like a night with
  # nothing to do. The EXIT trap below is what covers those, and it cannot
  # see main()'s locals: `set -e` pops this function's frame *before* the
  # trap runs, so as locals these would all be unset by the time the trap
  # needs them — the silent-failure case would stay silent. Globals outlive
  # the frame, so the trap reports the real reason. Verified against a
  # failing `git fetch`.
  run_date="$(date -u +%F)"
  local run_started
  run_started="$(date -u +%s)"
  exit_reason="unknown"
  summary_written=0
  commits_made=0
  build_result="skipped"
  test_result="skipped"
  pr_url="none"
  trap 'exit_rc=$?; if (( summary_written == 0 )); then
          write_summary "$run_date" "${exit_reason:-unknown}:unexpected-exit-$exit_rc" \
            "$commits_made" "$build_result" "$test_result" "$pr_url"
        fi' EXIT

  local log_file="$LOG_DIR/debt-$run_date.log"
  LOG_FILE="$log_file" # used by log()/die() below
  local run_report=""

  mkdir -p "$LOG_DIR"

  local budget_seconds
  if ! budget_seconds="$(parse_seconds "$TIME_BUDGET")"; then
    exit_reason="invalid-time-budget"
    die "TIME_BUDGET must be a positive integer with optional s/m/h/d suffix"
  fi

  # ---- single-instance lock ----------------------------------------------
  local lock_file="$LOG_DIR/.run.lock"
  exec {lock_fd}>"$lock_file"
  if ! flock -n "$lock_fd"; then
    exit_reason="lock-held"
    log "another run is already in progress ($lock_file) — exiting"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 0
  fi

  prune_old_logs

  # ---- preflight: log exactly which binaries will run tonight ------------
  # The PATH regression that motivated this (a stale system codex shadowing
  # the current user-local one) was invisible because nothing logged which
  # binary actually ran. Log resolved path + version for every tool this
  # script depends on, so a future PATH regression shows up in the log
  # instead of silently running the wrong binary.
  for tool in codex dotnet git gh; do
    local tool_path tool_version
    tool_path="$(command -v "$tool" 2>/dev/null || echo "NOT FOUND")"
    tool_version="$("$tool" --version 2>&1 | head -n1 || true)"
    log "tool: $tool -> $tool_path ($tool_version)"
  done

  # ---- preflight: fail fast, before spending any money -------------------
  if ! command -v codex >/dev/null 2>&1; then
    exit_reason="preflight-failed-no-codex"
    log "ERROR: 'codex' not found on PATH — cannot run"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi
  # We run on Codex plan quota via a ChatGPT sign-in, not an API key. That
  # credential is stored on disk by `codex login` and can expire, and a
  # refresh needs an interactive login that a 06:00 timer cannot perform.
  # So check it here: a stale credential must cost a log line, not the window.
  if ! codex_auth_ok >>"$log_file" 2>&1; then
    exit_reason="preflight-failed-codex-auth"
    log "ERROR: codex is not signed in (or the saved credential has expired)."
    log "       Run 'codex login' as $(id -un) on this machine, then re-run."
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi
  if ! gh auth status >>"$log_file" 2>&1; then
    exit_reason="preflight-failed-gh-auth"
    log "ERROR: 'gh auth status' failed — gh is not authenticated on this machine. See $log_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi
  if [[ -z "$REPO_URL" ]]; then
    exit_reason="preflight-failed-no-repo-url"
    log "ERROR: REPO_URL is not configured — set it in $env_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi
  log "preflight ok: codex on PATH and signed in, gh authenticated"

  # owner/repo for every `gh` call that takes --repo — this repo has two
  # remotes (origin=peterdrier/Humans, upstream=nobodies-collective/Humans)
  # with overlapping issue/PR numbers, so a bare `gh` call can resolve
  # against the wrong one (memory/process/cross-repo-pr-push-target.md,
  # memory/process/issue-refs-qualified.md).
  local gh_repo
  gh_repo="$(printf '%s' "$REPO_URL" | sed -E 's#^(https://github\.com/|git@github\.com:)##; s#\.git$##')"

  # ---- assert this is a dedicated, disposable clone ----------------------
  if [[ ! -d "$WORK_DIR/.git" ]]; then
    exit_reason="work-dir-not-a-git-checkout"
    die "WORK_DIR ($WORK_DIR) is not a git checkout. Run the one-time setup in .codex/cron/README.md first."
  fi
  if [[ ! -f "$WORK_DIR/$CLONE_MARKER_NAME" ]]; then
    exit_reason="work-dir-missing-clone-marker"
    die "WORK_DIR ($WORK_DIR) has no $CLONE_MARKER_NAME marker — refusing to run destructive git operations on it. This must be a dedicated clone made for this runner, never a human's working checkout. See .codex/cron/README.md's one-time setup."
  fi

  # ---- from here on, the shell IS in the clone ---------------------------
  # This is the only directory change in the script, and it happens the
  # moment the checks above prove this clone is ours to work in. Everything
  # after it — git, dotnet, gh — runs plain, from here. No `git -C`, no
  # `(cd ... && ...)` subshell: one working directory, established once, so
  # git, the build and any file the script touches can never disagree about
  # where they are. Paths that must point outside the clone (LOG_DIR, the
  # evidence directory, the run report) are absolute for that reason.
  cd "$WORK_DIR"

  # ---- preserve evidence of an unfinished/failed previous run ------------
  # A dirty tree here means the previous run was interrupted (SIGKILLed
  # mid-write, crashed) before it could clean up after itself. Save what it
  # left before the hard reset below destroys it, so a failure can still be
  # diagnosed after the fact.
  if [[ -n "$(git status --porcelain 2>/dev/null)" ]]; then
    local prev_branch
    prev_branch="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)"
    local evidence_dir="$LOG_DIR/evidence-$run_date-$(date -u +%H%M%S)"
    mkdir -p "$evidence_dir"
    log "previous run left a dirty tree on branch '$prev_branch' — saving evidence to $evidence_dir before reset"
    {
      echo "branch: $prev_branch"
      echo "saved: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    } >"$evidence_dir/info.txt"
    git status --porcelain >"$evidence_dir/status.txt" 2>/dev/null || true
    git diff >"$evidence_dir/uncommitted.diff" 2>/dev/null || true
    # Copy untracked files out bodily. Both captures around this line are
    # tracked-files-only — `git diff` by definition, and `git stash create`
    # has no untracked mode — while the `git clean -fdx` below deletes every
    # untracked file there is. A brand-new file the interrupted run had just
    # written is exactly the evidence worth having, and a filename in
    # status.txt is not recovery. --exclude-standard skips ignored paths, so
    # this saves real work and not bin/obj or the env file.
    mkdir -p "$evidence_dir/untracked"
    git ls-files --others --exclude-standard -z \
      | xargs -0 -r cp --parents -t "$evidence_dir/untracked" 2>>"$log_file" || true
    local stash_sha
    stash_sha="$(git stash create 2>/dev/null || true)"
    if [[ -n "$stash_sha" ]]; then
      echo "$stash_sha" >"$evidence_dir/stash-sha.txt"
      log "uncommitted work also saved as loose commit $stash_sha (not attached to any ref/stash-list entry)"
    fi
  fi

  # ---- refresh the dedicated clone ----------------------------------------
  # Reset/clean the current branch first, before switching branches, so a
  # dirty tree can never make the checkout below fail.
  # Both cleans must spare the clone marker as well as the env file. The
  # marker is gitignored, and `-x` deletes ignored files, so without this the
  # first clean removes the very thing that authorizes cleaning. It is only
  # re-touched after the fetch/checkout below, so any transient network
  # failure in between exits under `set -e` with the marker gone — and every
  # later run then dies at the marker preflight, permanently, until a human
  # recreates the file by hand. A blip must not brick the nightly.
  log "refreshing $WORK_DIR from origin/$GH_BASE_BRANCH"
  git remote set-url origin "$REPO_URL"
  git reset --quiet --hard
  git clean -fdx --quiet -e "$ENV_FILE_REL_PATH" -e "$CLONE_MARKER_NAME"
  git fetch --quiet origin "$GH_BASE_BRANCH" >>"$log_file" 2>&1
  git checkout --quiet "$GH_BASE_BRANCH" 2>/dev/null \
    || git checkout --quiet -b "$GH_BASE_BRANCH" "origin/$GH_BASE_BRANCH"
  git reset --quiet --hard "origin/$GH_BASE_BRANCH"
  git clean -fdx --quiet -e "$ENV_FILE_REL_PATH" -e "$CLONE_MARKER_NAME"
  touch "$CLONE_MARKER_NAME"

  local prompt_file="$WORK_DIR/$PROMPT_REL_PATH"
  if [[ ! -f "$prompt_file" ]]; then
    exit_reason="prompt-file-missing"
    die "prompt file missing after refresh: $prompt_file"
  fi

  # ---- branch: one per calendar day ---------------------------------------
  local branch="$BRANCH_PREFIX/$run_date"
  # The goal client saves Codex's final run report here. It lives in
  # LOG_DIR, outside WORK_DIR, so `git clean` never touches it and it
  # survives for the rest of the day — which is what lets the recovery path
  # below re-use the report from the run that pushed this branch.
  local last_message_file="$LOG_DIR/last-message-$run_date.md"
  if git ls-remote --exit-code --heads origin "$branch" >/dev/null 2>&1; then
    # Branch already pushed today. If a PR exists for it in ANY state, today's
    # work is done — a PR Peter closed or merged during the day is finished
    # work, not missing work, and `--state open` alone would have a same-day
    # rerun open a second PR for the same branch. If there is no PR at all
    # (a prior run pushed but `gh pr create` failed), the work is invisible
    # until one exists — open it now instead of silently skipping every
    # remaining run today.
    local existing_pr
    existing_pr="$(gh pr list --repo "$gh_repo" --head "$branch" --state all --json url,state --jq '.[0] | select(.url) | "\(.state) \(.url)"' 2>>"$log_file")"
    if [[ -n "$existing_pr" ]]; then
      exit_reason="skip-already-ran-today"
      log "branch $branch already exists on origin with a PR ($existing_pr) — a run already completed today; skipping"
      write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
      exit 0
    fi
    log "branch $branch exists on origin but has no open PR — opening one now instead of skipping"
    # Recover the earlier run's report so the PR this path opens carries the
    # rung, closed ledger ids and skip reasons that daily-debt.md requires —
    # a PR opened on retry is no less reviewable than one opened first time.
    if [[ -f "$last_message_file" ]]; then
      run_report="$(cat "$last_message_file")"
      log "recovered run report from $last_message_file for the retried PR"
    fi
    if pr_url="$(open_pr_for_branch "$branch" "$GH_BASE_BRANCH" "$run_date" "$log_file" "$run_report" "$gh_repo")"; then
      exit_reason="pushed"
      log "opened PR for pre-existing branch: $pr_url"
    else
      exit_reason="pr-create-failed"
      pr_url="none"
      log "ERROR: gh pr create failed for pre-existing branch $branch. See $log_file"
      write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
      exit 1
    fi
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 0
  fi
  # ---- don't outrun the reviewer -----------------------------------------
  # Every run edits the same ledger files — bumping `next_id` in
  # docs/architecture/debt-ledger.yml and the per-section debt.yml, flipping
  # rows to closed. Two runs doing that from the same base conflict on the
  # counter alone, before any code is involved, and the second one re-reads a
  # ledger that still shows day one's work as open, so the ladder hands it
  # the same rung again. Capping the open PRs makes both impossible by
  # construction rather than merely unlikely, which is the property this
  # needs while running unattended: queue depth never exceeds the cap, and
  # the pipeline produces work at exactly the rate Peter merges it. Idling
  # while a PR waits is the intended behaviour — one unreviewed PR is a
  # better state to be in than a week of conflicting ones.
  local open_auto_prs
  if ! open_auto_prs="$(BRANCH_PREFIX="$BRANCH_PREFIX" gh pr list --repo "$gh_repo" \
      --state open --limit 100 --json headRefName,number,url \
      --jq '.[] | select(.headRefName | startswith(env.BRANCH_PREFIX + "/")) | "#\(.number) \(.url)"' \
      2>>"$log_file")"; then
    # Fail closed. An empty answer from a failed `gh` is indistinguishable
    # from "nothing is open", and guessing wrong here opens the second PR
    # this whole check exists to prevent.
    exit_reason="open-pr-check-failed"
    die "could not list this runner's open PRs on $gh_repo — refusing to run rather than risk a second concurrent debt PR. See $log_file"
  fi
  local open_auto_count
  open_auto_count="$(grep -c . <<<"$open_auto_prs" || true)"
  if (( open_auto_count >= MAX_OPEN_AUTO_PRS )); then
    exit_reason="skip-open-auto-pr"
    log "$open_auto_count of this runner's PRs still open (cap $MAX_OPEN_AUTO_PRS), nothing merged since the last run — skipping: $(tr '\n' ' ' <<<"$open_auto_prs")"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 0
  fi

  git checkout --quiet -B "$branch" "origin/$GH_BASE_BRANCH"
  log "working on branch $branch"

  # ---- run Codex with a persistent timed goal -------------------------------------
  local head_before
  head_before="$(git rev-parse HEAD)"

  # The goal client saves the final report outside the checkout, keeping
  # runner evidence out of the tree being tested and pushed.
  rm -f "$last_message_file"

  local work_started work_deadline
  work_started="$(date -u +%s)"
  work_deadline=$(( work_started + budget_seconds ))
  local rendered_prompt_file="$LOG_DIR/prompt-$run_date.md"
  sed -e "s/__TIME_BUDGET__/$TIME_BUDGET/g" \
    -e "s/__WORK_STARTED_UTC__/$(date -u -d "@$work_started" +%FT%TZ)/g" \
    -e "s/__WORK_DEADLINE_UTC__/$(date -u -d "@$work_deadline" +%FT%TZ)/g" \
    -e "s/__WORK_DEADLINE_EPOCH__/$work_deadline/g" \
    "$prompt_file" >"$rendered_prompt_file"
  prompt_file="$rendered_prompt_file"

  log "starting one Codex session, work deadline $work_deadline, dangerous mode $CODEX_DANGEROUS"
  export WORK_DIR CODEX_MODEL CODEX_EFFORT CODEX_DANGEROUS
  local codex_exit=0
  python3 "$WORK_DIR/.codex/cron/run-goal.py" "$prompt_file" "$last_message_file" "$work_deadline" \
    >>"$log_file" 2>&1 || codex_exit=$?
  if (( codex_exit != 0 )); then
    exit_reason="codex-failed"
    die "Codex goal failed; preserving local work without publishing"
  fi
  if (( $(date -u +%s) < work_deadline )); then
    exit_reason="goal-ended-early"
    die "Goal returned before the work deadline"
  fi
  local work_elapsed=$(( $(date -u +%s) - work_started ))
  log "timed goal completed after $work_elapsed seconds"

  # ---- refuse to gate or push from anywhere but the branch we hand over ----
  # The gate below tests whatever is checked out, but the push names
  # "$branch". Codex switching branches or checking out a worktree makes
  # those two different commits, and the runner would advertise a green
  # build for code it never compiled. The prompt forbids it; this catches it
  # regardless, before the 30-minute build/test rather than after.
  local current_branch
  current_branch="$(git rev-parse --abbrev-ref HEAD)"
  if [[ "$current_branch" != "$branch" ]]; then
    exit_reason="branch-changed-during-run"
    log "ERROR: expected to be on '$branch' after codex, but HEAD is on '$current_branch'."
    log "       The tested tree would not be the pushed commit — refusing to continue."
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi

  local head_after
  head_after="$(git rev-parse HEAD)"

  # ---- refuse to test or push a tree that isn't what would be pushed --------
  # `git push` only ever sends committed history. An interrupted agent can
  # leave uncommitted changes in the working tree; testing
  # that tree and then pushing head_after would advertise a green gate for code
  # that was never actually tested. Check this before the substantive-change check below,
  # since a dirty tree can coexist with head_before == head_after too.
  if [[ -n "$(git status --porcelain)" ]]; then
    exit_reason="dirty-tree-after-codex"
    log "ERROR: working tree has uncommitted changes after codex exited (status $codex_exit)."
    log "       Refusing to build/test/push a tree that is not what would be pushed."
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi

  # ---- capture codex's run report -----------------------------------------
  # Saved by the goal client outside WORK_DIR, not part of the tested tree.
  run_report="$(cat "$last_message_file")"

  # Bookkeeping is not a debt fix. Do not publish a ledger-only or docs-only
  # run as successful work; executable tooling under docs/scripts counts.
  if ! has_substantive_changes "$head_before" "$head_after"; then
    exit_reason="no-substantive-fixes"
    die "No substantive fixes; ledger/documentation changes alone cannot produce a debt PR"
  fi

  commits_made="$(git rev-list --count "$head_before..$head_after")"
  log "codex made $commits_made commit(s)"

  # ---- gate before pushing: build + test ----------------------------------
  log "running dotnet build Humans.slnx -v quiet -clp:ErrorsOnly"
  if dotnet build Humans.slnx -v quiet -clp:ErrorsOnly >>"$log_file" 2>&1; then
    build_result="pass"
  else
    build_result="fail"
    exit_reason="build-failed"
    log "ERROR: build failed — not pushing. See $log_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi

  log "running dotnet test Humans.slnx -v quiet -clp:ErrorsOnly --filter $VSTestTestCaseFilter"
  if dotnet test Humans.slnx -v quiet -clp:ErrorsOnly --filter "$VSTestTestCaseFilter" >>"$log_file" 2>&1; then
    test_result="pass"
  else
    test_result="fail"
    exit_reason="test-failed"
    log "ERROR: tests failed — not pushing. See $log_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi
  log "build and test both passed"
  # Wrapper timestamps, not the agent's estimate. Save this with the report
  # so a retry of PR creation preserves the original run's timing.
  local timing="Goal time: $TIME_BUDGET; actual worker time: $(format_duration "$work_elapsed"); total run through validation: $(format_duration "$(( $(date -u +%s) - run_started ))")."
  printf '\n\n%s\n' "$timing" >>"$last_message_file"
  run_report="$(cat "$last_message_file")"

  # ---- assert HEAD hasn't moved between gate and push ---------------------
  # head_after was captured right after codex exited and validated by the
  # gate above; re-check it immediately before the one command that
  # publishes anything, so nothing can slip in between validation and push.
  local head_at_push
  head_at_push="$(git rev-parse HEAD)"
  if [[ "$head_at_push" != "$head_after" ]]; then
    exit_reason="head-changed-before-push"
    log "ERROR: HEAD moved between gate ($head_after) and push ($head_at_push) — refusing to push."
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi

  # ---- push (retry on network failure only) -------------------------------
  if ! push_with_retry "$branch" "$log_file" "$PUSH_RETRIES"; then
    exit_reason="push-failed"
    log "ERROR: push failed after retries. See $log_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi
  log "pushed $branch to origin"

  # ---- open the PR, ready for review --------------------------------------
  if pr_url="$(open_pr_for_branch "$branch" "$GH_BASE_BRANCH" "$run_date" "$log_file" "$run_report" "$gh_repo")"; then
    exit_reason="pushed"
    log "opened PR: $pr_url"
  else
    exit_reason="pr-create-failed"
    pr_url="none"
    log "ERROR: gh pr create failed — branch is pushed but no PR was opened. See $log_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi

  write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
}

# Opens the daily-debt PR for an already-pushed branch. Prints the PR URL on
# success. Shared by the normal push-then-PR flow and by the "branch already
# exists on origin but has no open PR" recovery path, so a transient
# `gh pr create` failure never leaves a pushed branch permanently invisible.
open_pr_for_branch() {
  local branch="$1" base_branch="$2" run_date="$3" log_file="$4" run_report="${5:-}" gh_repo="${6:-}"
  local pr_title="Daily tech-debt sweep — $run_date"
  local pr_body_file
  pr_body_file="$(mktemp)"
  if [[ -z "$run_report" ]]; then
    rm -f "$pr_body_file"
    echo "Cannot publish a debt PR without its cumulative report" >&2
    return 1
  fi
  {
    printf '%s\n\n' "$run_report"
    echo "Runner validation: build and tests passed; Humans.Integration.Tests excluded."
  } >"$pr_body_file"

  local result
  if result="$(gh pr create --repo "$gh_repo" --base "$base_branch" --head "$branch" \
      --title "$pr_title" --body-file "$pr_body_file" 2>>"$log_file")"; then
    rm -f "$pr_body_file"
    echo "$result"
    return 0
  fi
  rm -f "$pr_body_file"
  return 1
}

log() {
  local line
  line="[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*"
  echo "$line"
  if [[ -n "${LOG_FILE:-}" ]]; then
    echo "$line" >>"$LOG_FILE"
  fi
}

die() {
  log "ERROR: $*"
  # Called only from within main(), where these locals are already declared
  # (bash's dynamic scoping makes them visible here) — every exit path,
  # including this one, must leave a SUMMARY line.
  write_summary "$run_date" "${exit_reason:-unknown}" "${commits_made:-0}" "${build_result:-skipped}" "${test_result:-skipped}" "${pr_url:-none}"
  exit 1
}

write_summary() {
  local run_date="$1" reason="$2" commits="$3" build="$4" test="$5" pr="$6"
  # Assigns main()'s local through dynamic scoping, so the EXIT trap there
  # knows a summary already went out and does not print a second one.
  summary_written=1
  log "SUMMARY date=$run_date exit_reason=$reason commits=$commits build=$build test=$test pr=$pr"
}

prune_old_logs() {
  find "$LOG_DIR" -maxdepth 1 -type f -name 'debt-*.log' -mtime "+$LOG_RETENTION_DAYS" -delete 2>/dev/null || true
}

# Whole-second work windows; reject typos rather than silently using 90m.
parse_seconds() {
  local spec="$1" num
  [[ "$spec" =~ ^([0-9]+)([smhd]?)$ ]] || return 1
  num=$(( 10#${BASH_REMATCH[1]} ))
  case "${BASH_REMATCH[2]:-s}" in
    m) num=$(( num * 60 )) ;;
    h) num=$(( num * 3600 )) ;;
    d) num=$(( num * 86400 )) ;;
  esac
  (( num > 0 )) || return 1
  echo "$num"
}

format_duration() {
  printf '%dm %ds' "$(( $1 / 60 ))" "$(( $1 % 60 ))"
}

has_substantive_changes() {
  local path
  while IFS= read -r -d '' path; do
    case "$path" in
      docs/scripts/*) return 0 ;;
      docs/*|memory/*|*/Docs/*|*.md) ;;
      *) return 0 ;;
    esac
  done < <(git diff --name-only -z "$1" "$2")
  return 1
}

# Retries a push up to $4 additional times (2s/4s/8s/16s backoff), but only
# when the failure looks like a transient network error. Any other failure
# (auth, non-fast-forward, etc.) fails immediately without retrying.
push_with_retry() {
  local branch="$1" log_file="$2" max_retries="$3"
  local attempt=1
  local delay=2
  local err_file
  err_file="$(mktemp)"

  while true; do
    if git push -u origin "$branch" >"$err_file" 2>&1; then
      cat "$err_file" >>"$log_file"
      rm -f "$err_file"
      return 0
    fi
    cat "$err_file" >>"$log_file"

    if ! grep -qiE 'could not resolve host|connection (timed out|reset|refused)|network is unreachable|early eof|unexpected disconnect|temporary failure|the remote end hung up|rpc failed|ssl_connect|tls.*timeout' "$err_file"; then
      log "push failed with a non-network error — not retrying"
      rm -f "$err_file"
      return 1
    fi

    if (( attempt > max_retries )); then
      rm -f "$err_file"
      return 1
    fi

    log "push failed (network error), retrying in ${delay}s (attempt $attempt/$max_retries)"
    sleep "$delay"
    delay=$(( delay * 2 ))
    attempt=$(( attempt + 1 ))
  done
}

main "$@"
