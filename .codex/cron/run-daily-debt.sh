#!/usr/bin/env bash
#
# Daily unattended Codex tech-debt runner.
#
# Runs `codex exec` headless against a DEDICATED clone of this repo (never a
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

main() {
  # ============================================================
  # CONFIG — every machine-specific value, sourced from
  # debt-runner.env if present, then defaulted. Override via env
  # file, not by editing this script.
  # ============================================================
  local script_dir
  script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
  local env_file="$script_dir/debt-runner.env"
  if [[ -f "$env_file" ]]; then
    # shellcheck source=/dev/null
    source "$env_file"
  fi

  REPO_URL="${REPO_URL:-}"                                    # git remote to clone/push, e.g. git@github.com:peterdrier/Humans.git
  WORK_DIR="${WORK_DIR:-$HOME/.humans-debt-runner/clone}"      # DEDICATED clone. Never Peter's working checkout.
  TIME_BUDGET="${TIME_BUDGET:-90m}"                            # wall-clock cap passed to `timeout`
  CODEX_MODEL="${CODEX_MODEL:-}"                               # empty = codex's own default model
  LOG_DIR="${LOG_DIR:-$HOME/.humans-debt-runner/logs}"         # must be outside WORK_DIR (git clean would wipe it)
  BRANCH_PREFIX="${BRANCH_PREFIX:-codex/daily-debt}"           # branch = $BRANCH_PREFIX/YYYY-MM-DD
  GH_BASE_BRANCH="${GH_BASE_BRANCH:-main}"                     # base branch on origin
  CODEX_DANGEROUS="${CODEX_DANGEROUS:-1}"                      # 1 = --dangerously-bypass-approvals-and-sandbox, 0 = --full-auto
  PUSH_RETRIES="${PUSH_RETRIES:-4}"                            # retries after the first push attempt, network failures only
  LOG_RETENTION_DAYS="${LOG_RETENTION_DAYS:-30}"
  readonly CLONE_MARKER_NAME=".codex-runner-clone"
  readonly PROMPT_REL_PATH=".codex/prompts/daily-debt.md"
  readonly ENV_FILE_REL_PATH=".codex/cron/debt-runner.env"     # excluded from `git clean` inside WORK_DIR

  local run_date
  run_date="$(date -u +%F)"
  local log_file="$LOG_DIR/debt-$run_date.log"
  LOG_FILE="$log_file" # used by log()/die() below

  mkdir -p "$LOG_DIR"

  # ---- single-instance lock ----------------------------------------------
  local lock_file="$LOG_DIR/.run.lock"
  exec {lock_fd}>"$lock_file"
  if ! flock -n "$lock_fd"; then
    log "another run is already in progress ($lock_file) — exiting"
    exit 0
  fi

  prune_old_logs

  local exit_reason="unknown"
  local commits_made=0
  local build_result="skipped"
  local test_result="skipped"
  local pr_url="none"

  # ---- preflight: fail fast, before spending any money -------------------
  if ! command -v codex >/dev/null 2>&1; then
    exit_reason="preflight-failed-no-codex"
    log "ERROR: 'codex' not found on PATH — cannot run"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi
  if [[ -z "${OPENAI_API_KEY:-}" ]]; then
    exit_reason="preflight-failed-no-api-key"
    log "ERROR: OPENAI_API_KEY is not set — refusing to run codex"
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
  log "preflight ok: codex on PATH, OPENAI_API_KEY set, gh authenticated"

  # ---- assert this is a dedicated, disposable clone ----------------------
  if [[ ! -d "$WORK_DIR/.git" ]]; then
    die "WORK_DIR ($WORK_DIR) is not a git checkout. Run the one-time setup in .codex/cron/README.md first."
  fi
  if [[ ! -f "$WORK_DIR/$CLONE_MARKER_NAME" ]]; then
    die "WORK_DIR ($WORK_DIR) has no $CLONE_MARKER_NAME marker — refusing to run destructive git operations on it. This must be a dedicated clone made for this runner, never a human's working checkout. See .codex/cron/README.md's one-time setup."
  fi

  # ---- refresh the dedicated clone ----------------------------------------
  log "refreshing $WORK_DIR from origin/$GH_BASE_BRANCH"
  git -C "$WORK_DIR" remote set-url origin "$REPO_URL"
  git -C "$WORK_DIR" fetch --quiet origin "$GH_BASE_BRANCH" >>"$log_file" 2>&1
  git -C "$WORK_DIR" checkout --quiet "$GH_BASE_BRANCH" 2>/dev/null \
    || git -C "$WORK_DIR" checkout --quiet -b "$GH_BASE_BRANCH" "origin/$GH_BASE_BRANCH"
  git -C "$WORK_DIR" reset --quiet --hard "origin/$GH_BASE_BRANCH"
  git -C "$WORK_DIR" clean -fdx --quiet -e "$ENV_FILE_REL_PATH"
  touch "$WORK_DIR/$CLONE_MARKER_NAME"

  local prompt_file="$WORK_DIR/$PROMPT_REL_PATH"
  if [[ ! -f "$prompt_file" ]]; then
    die "prompt file missing after refresh: $prompt_file"
  fi

  # ---- branch: one per calendar day ---------------------------------------
  local branch="$BRANCH_PREFIX/$run_date"
  if git -C "$WORK_DIR" ls-remote --exit-code --heads origin "$branch" >/dev/null 2>&1; then
    exit_reason="skip-already-ran-today"
    log "branch $branch already exists on origin — a run already completed today; skipping"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 0
  fi
  git -C "$WORK_DIR" checkout --quiet -B "$branch" "origin/$GH_BASE_BRANCH"
  log "working on branch $branch"

  # ---- run codex, hard wall-clock cap -------------------------------------
  local head_before
  head_before="$(git -C "$WORK_DIR" rev-parse HEAD)"

  local -a codex_args=(exec --cd "$WORK_DIR" --color never)
  if [[ -n "$CODEX_MODEL" ]]; then
    codex_args+=(--model "$CODEX_MODEL")
  fi
  if [[ "$CODEX_DANGEROUS" == "1" ]]; then
    codex_args+=(--dangerously-bypass-approvals-and-sandbox)
  else
    codex_args+=(--full-auto)
  fi

  log "starting codex, time budget $TIME_BUDGET: codex ${codex_args[*]}"
  local codex_exit=0
  set +e
  timeout --signal=INT --kill-after=120s "$TIME_BUDGET" \
    codex "${codex_args[@]}" - <"$prompt_file" >>"$log_file" 2>&1
  codex_exit=$?
  set -e
  log "codex exited with status $codex_exit"

  local head_after
  head_after="$(git -C "$WORK_DIR" rev-parse HEAD)"

  if [[ "$head_before" == "$head_after" ]]; then
    exit_reason="no-op"
    log "no-op: codex made no commits"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 0
  fi

  commits_made="$(git -C "$WORK_DIR" rev-list --count "$head_before..$head_after")"
  log "codex made $commits_made commit(s)"

  # ---- gate before pushing: build + test ----------------------------------
  log "running dotnet build Humans.slnx -v quiet"
  if (cd "$WORK_DIR" && dotnet build Humans.slnx -v quiet) >>"$log_file" 2>&1; then
    build_result="pass"
  else
    build_result="fail"
    exit_reason="build-failed"
    log "ERROR: build failed — not pushing. See $log_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi

  log "running dotnet test Humans.slnx -v quiet"
  if (cd "$WORK_DIR" && dotnet test Humans.slnx -v quiet) >>"$log_file" 2>&1; then
    test_result="pass"
  else
    test_result="fail"
    exit_reason="test-failed"
    log "ERROR: tests failed — not pushing. See $log_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi
  log "build and test both passed"

  # ---- push (retry on network failure only) -------------------------------
  if ! push_with_retry "$WORK_DIR" "$branch" "$log_file" "$PUSH_RETRIES"; then
    exit_reason="push-failed"
    log "ERROR: push failed after retries. See $log_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi
  log "pushed $branch to origin"

  # ---- open the PR, ready for review --------------------------------------
  local pr_title="Daily tech-debt sweep — $run_date"
  local pr_body_file
  pr_body_file="$(mktemp)"
  if [[ -f "$WORK_DIR/.github/pull_request_template.md" ]]; then
    cat "$WORK_DIR/.github/pull_request_template.md" >"$pr_body_file"
    printf '\n' >>"$pr_body_file"
  fi
  {
    echo "## Automated daily tech-debt run"
    echo
    echo "Unattended overnight run. Build and tests passed before this PR was opened."
    echo
    echo "Commits:"
    git -C "$WORK_DIR" log --pretty='- %s' "origin/$GH_BASE_BRANCH..$branch"
  } >>"$pr_body_file"

  if pr_url="$(cd "$WORK_DIR" && gh pr create --base "$GH_BASE_BRANCH" --head "$branch" \
      --title "$pr_title" --body-file "$pr_body_file" 2>>"$log_file")"; then
    rm -f "$pr_body_file"
    exit_reason="pushed"
    log "opened PR: $pr_url"
  else
    rm -f "$pr_body_file"
    exit_reason="pr-create-failed"
    pr_url="none"
    log "ERROR: gh pr create failed — branch is pushed but no PR was opened. See $log_file"
    write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
    exit 1
  fi

  write_summary "$run_date" "$exit_reason" "$commits_made" "$build_result" "$test_result" "$pr_url"
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
  exit 1
}

write_summary() {
  local run_date="$1" reason="$2" commits="$3" build="$4" test="$5" pr="$6"
  log "SUMMARY date=$run_date exit_reason=$reason commits=$commits build=$build test=$test pr=$pr"
}

prune_old_logs() {
  find "$LOG_DIR" -maxdepth 1 -type f -name 'debt-*.log' -mtime "+$LOG_RETENTION_DAYS" -delete 2>/dev/null || true
}

# Retries a push up to $4 additional times (2s/4s/8s/16s backoff), but only
# when the failure looks like a transient network error. Any other failure
# (auth, non-fast-forward, etc.) fails immediately without retrying.
push_with_retry() {
  local work_dir="$1" branch="$2" log_file="$3" max_retries="$4"
  local attempt=1
  local delay=2
  local err_file
  err_file="$(mktemp)"

  while true; do
    if git -C "$work_dir" push -u origin "$branch" >"$err_file" 2>&1; then
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
