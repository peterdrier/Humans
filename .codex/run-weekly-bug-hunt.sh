#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: .codex/run-weekly-bug-hunt.sh [options]

Runs Codex non-interactively in an isolated worktree, then resumes the same
session across additional passes until progress stops or the pass limit is hit.

Modes:
  bug-hunt   Use the autonomous bug-fix prompt
  tech-debt  Use the autonomous tech-debt prompt

Options:
  --mode <name>                Run mode: bug-hunt or tech-debt
  --model <name>               Codex model to use
  --prompt <path>              Prompt file override
  --runs-dir <path>            Artifact directory root override
  --worktrees-dir <path>       Worktree directory root
  --branch-prefix <prefix>     Branch prefix override
  --allow-dirty                Run even if the root worktree is dirty
  --no-worktree                Run in the current checkout instead
  --no-branch                  Reuse the current branch instead of creating one
  --safe                       Use Codex sandboxed full-auto mode
  --max-passes <count>         Total passes including the initial prompt
  --push-remote <name>         Remote to push commits to after each pass
  --no-push                    Disable automatic pushing after each pass
  --extra-instruction <text>   Append an extra instruction before the prompt
  --help                       Show this help

Examples:
  .codex/run-weekly-bug-hunt.sh
  .codex/run-weekly-bug-hunt.sh --mode tech-debt
  .codex/run-weekly-bug-hunt.sh --max-passes 4 --model gpt-5.4
  .codex/run-weekly-bug-hunt.sh --safe --no-worktree --no-branch
EOF
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

mode="bug-hunt"
model=""
prompt_override=""
runs_dir_override=""
worktrees_dir=".worktrees"
branch_prefix_override=""
allow_dirty=0
use_worktree=1
create_branch=1
dangerous=1
max_passes=3
push_remote="origin"
auto_push=1
declare -a extra_instructions=()

while (($# > 0)); do
  case "$1" in
    --mode)
      mode="${2:?missing mode name}"
      shift 2
      ;;
    --model)
      model="${2:?missing model value}"
      shift 2
      ;;
    --prompt)
      prompt_override="${2:?missing prompt path}"
      shift 2
      ;;
    --runs-dir)
      runs_dir_override="${2:?missing runs dir}"
      shift 2
      ;;
    --worktrees-dir)
      worktrees_dir="${2:?missing worktrees dir}"
      shift 2
      ;;
    --branch-prefix)
      branch_prefix_override="${2:?missing branch prefix}"
      shift 2
      ;;
    --allow-dirty)
      allow_dirty=1
      shift
      ;;
    --no-worktree)
      use_worktree=0
      shift
      ;;
    --no-branch)
      create_branch=0
      shift
      ;;
    --safe)
      dangerous=0
      shift
      ;;
    --max-passes)
      max_passes="${2:?missing pass count}"
      shift 2
      ;;
    --push-remote)
      push_remote="${2:?missing remote name}"
      shift 2
      ;;
    --no-push)
      auto_push=0
      shift
      ;;
    --extra-instruction)
      extra_instructions+=("${2:?missing instruction text}")
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

require_command codex
require_command git
require_command date
require_command mkdir
require_command tee
require_command grep
require_command sed
require_command awk

case "$mode" in
  bug-hunt)
    prompt_path="${prompt_override:-.codex/bug-hunt-prompt.md}"
    runs_dir="${runs_dir_override:-local/codex-runs/bug-hunt}"
    branch_prefix="${branch_prefix_override:-codex/weekly-bug-hunt}"
    ;;
  tech-debt)
    prompt_path="${prompt_override:-.codex/tech-debt-prompt.md}"
    runs_dir="${runs_dir_override:-local/codex-runs/tech-debt}"
    branch_prefix="${branch_prefix_override:-codex/weekly-tech-debt}"
    ;;
  *)
    echo "Unsupported mode: $mode" >&2
    exit 1
    ;;
esac

if ! [[ "$max_passes" =~ ^[0-9]+$ ]] || (( max_passes < 1 )); then
  echo "--max-passes must be a positive integer" >&2
  exit 1
fi

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

if [[ ! -f "$prompt_path" ]]; then
  echo "Prompt file not found: $prompt_path" >&2
  exit 1
fi

if git remote get-url origin >/dev/null 2>&1; then
  git fetch origin main >/dev/null 2>&1 || true
fi

if (( auto_push )) && ! git remote get-url "$push_remote" >/dev/null 2>&1; then
  echo "Push remote does not exist: $push_remote" >&2
  exit 1
fi

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$runs_dir"
run_dir="$runs_dir/$timestamp"
mkdir -p "$run_dir"

status_file="$run_dir/status.txt"
metadata_file="$run_dir/metadata.txt"
prompt_copy="$run_dir/prompt.md"
console_log="$run_dir/codex.log"
final_message_file="$run_dir/final-message.md"
session_id_file="$run_dir/session-id.txt"
pass_history_file="$run_dir/pass-history.txt"

current_branch="$(git branch --show-current || true)"
if [[ -z "$current_branch" ]]; then
  current_branch="detached-head"
fi

base_ref="$current_branch"
if [[ "$current_branch" == "main" ]] && git show-ref --verify --quiet "refs/remotes/origin/main"; then
  base_ref="origin/main"
fi

head_sha="$(git rev-parse --short HEAD)"
dirty=0
if [[ -n "$(git status --porcelain)" ]]; then
  dirty=1
fi

if (( dirty && !allow_dirty && !use_worktree )); then
  cat >&2 <<EOF
Refusing to run with a dirty git worktree.

Commit, stash, or discard your current changes first, or rerun with:
  .codex/run-weekly-bug-hunt.sh --allow-dirty
EOF
  exit 1
fi

if (( dirty && use_worktree && !allow_dirty )); then
  echo "Warning: root checkout is dirty; the worktree run will start from committed HEAD only." >&2
fi

if (( use_worktree && !create_branch )); then
  cat >&2 <<EOF
--no-branch is not supported together with the default worktree mode.

Use the default branch creation behavior, or disable worktree mode with:
  .codex/run-weekly-bug-hunt.sh --no-worktree --no-branch
EOF
  exit 1
fi

run_branch="$current_branch"
run_checkout="$repo_root"
worktree_path=""
if (( create_branch )); then
  safe_branch="${current_branch//\//-}"
  run_branch="${branch_prefix}/${safe_branch}-${timestamp}"
fi

if (( use_worktree )); then
  safe_worktree_name="${run_branch//\//-}"
  worktree_path="$repo_root/$worktrees_dir/$safe_worktree_name"
  mkdir -p "$repo_root/$worktrees_dir"

  if (( create_branch )); then
    git worktree add -b "$run_branch" "$worktree_path" "$base_ref" >/dev/null
  else
    git worktree add "$worktree_path" "$run_branch" >/dev/null
  fi

  run_checkout="$worktree_path"
elif (( create_branch )); then
  git switch -c "$run_branch" "$base_ref" >/dev/null
fi

{
  echo "status=running"
  echo "started_at_utc=$timestamp"
  echo "mode=$mode"
  echo "repo_root=$repo_root"
  echo "head_sha=$head_sha"
  echo "prompt_path=$prompt_path"
  echo "base_branch=$current_branch"
  echo "base_ref=$base_ref"
  echo "run_branch=$run_branch"
  echo "run_checkout=$run_checkout"
  echo "worktree_path=$worktree_path"
  echo "dirty_at_start=$dirty"
  echo "used_worktree=$use_worktree"
  echo "dangerous_mode=$dangerous"
  echo "max_passes=$max_passes"
  echo "auto_push=$auto_push"
  echo "push_remote=$push_remote"
  if [[ -n "$model" ]]; then
    echo "model=$model"
  fi
} >"$metadata_file"
echo "running" >"$status_file"
: >"$pass_history_file"

{
  if (( auto_push )); then
    push_instruction="- After every commit you create, push the current branch to $push_remote so progress is visible remotely."
  else
    push_instruction="- Do not push during this run unless explicitly instructed later."
  fi

  cat <<EOF
You are running from the committed wrapper script at .codex/run-weekly-bug-hunt.sh.

Execution context:
- Repository root: $repo_root
- Execution checkout: $run_checkout
- Prompt source: $prompt_path
- Artifact directory for this run: $run_dir
- Current git branch for this run: $run_branch
- Push remote for published progress: $push_remote

Operating instructions for this automated run:
- Work fully autonomously inside this repository.
- Keep any temporary files or status artifacts under local/.
- Work only inside the current checkout at $run_checkout.
- Use the current git branch. Do not switch branches.
$push_instruction
- End with a concise run report that lists bugs fixed or refactors completed, commands run, and any unfinished leads.
- Read and follow CLAUDE.md plus relevant files under .claude/ when they help you interpret local project conventions.

EOF

  for instruction in "${extra_instructions[@]}"; do
    printf -- "- Extra instruction: %s\n" "$instruction"
  done

  if ((${#extra_instructions[@]} > 0)); then
    printf "\n"
  fi

  cat "$prompt_path"
} >"$prompt_copy"

write_pass_log_marker() {
  local pass_number="$1"
  local pass_kind="$2"
  {
    printf "\n===== PASS %s (%s) =====\n" "$pass_number" "$pass_kind"
  } >>"$console_log"
}

capture_session_id() {
  if [[ -s "$session_id_file" ]]; then
    return 0
  fi

  local extracted
  extracted="$(grep -Eo 'session id: [0-9a-f-]+' "$console_log" | tail -n 1 | awk '{print $3}')"
  if [[ -n "$extracted" ]]; then
    printf '%s\n' "$extracted" >"$session_id_file"
    printf 'session_id=%s\n' "$extracted" >>"$metadata_file"
    return 0
  fi

  return 1
}

record_pass() {
  local pass_number="$1"
  local pass_kind="$2"
  local start_head="$3"
  local end_head="$4"
  local exit_code="$5"
  printf 'pass=%s kind=%s start_head=%s end_head=%s exit=%s\n' \
    "$pass_number" "$pass_kind" "$start_head" "$end_head" "$exit_code" >>"$pass_history_file"
}

push_branch_if_enabled() {
  if (( !auto_push )); then
    return 0
  fi

  git -C "$run_checkout" push -u "$push_remote" "$run_branch"
}

run_codex_pass() {
  local pass_number="$1"
  local pass_kind="$2"
  local pass_start_head
  local pass_end_head
  local pass_exit
  local -a codex_args

  pass_start_head="$(git -C "$run_checkout" rev-parse HEAD)"
  write_pass_log_marker "$pass_number" "$pass_kind"

  if [[ "$pass_kind" == "initial" ]]; then
    codex_args=(exec --cd "$run_checkout" --color never -o "$final_message_file")
    if [[ -n "$model" ]]; then
      codex_args+=(--model "$model")
    fi
    if (( dangerous )); then
      codex_args+=(--dangerously-bypass-approvals-and-sandbox)
    else
      codex_args+=(--full-auto)
    fi

    set +e
    codex "${codex_args[@]}" - <"$prompt_copy" 2>&1 | tee -a "$console_log"
    pass_exit="${PIPESTATUS[0]}"
    set -e
  else
    local session_id
    session_id="$(<"$session_id_file")"

    codex_args=(exec resume -o "$final_message_file")
    if [[ -n "$model" ]]; then
      codex_args+=(--model "$model")
    fi
    if (( dangerous )); then
      codex_args+=(--dangerously-bypass-approvals-and-sandbox)
    else
      codex_args+=(--full-auto)
    fi

    set +e
    if (( auto_push )); then
      resume_prompt="Continue the autonomous ${mode} run from where you left off. Do not repeat completed work. Use the existing branch and checkout, continue making small verified commits, and push ${run_branch} to ${push_remote} after each new commit."
    else
      resume_prompt="Continue the autonomous ${mode} run from where you left off. Do not repeat completed work. Use the existing branch and checkout, continue making small verified commits, and do not push during this run."
    fi

    codex "${codex_args[@]}" \
      "$session_id" \
      "$resume_prompt" \
      2>&1 | tee -a "$console_log"
    pass_exit="${PIPESTATUS[0]}"
    set -e
  fi

  capture_session_id || true
  pass_end_head="$(git -C "$run_checkout" rev-parse HEAD)"
  record_pass "$pass_number" "$pass_kind" "$pass_start_head" "$pass_end_head" "$pass_exit"

  if (( pass_exit == 0 )); then
    push_branch_if_enabled
  fi

  return "$pass_exit"
}

echo "Launching Codex in $run_checkout on branch $run_branch"
echo "Artifacts will be written to $run_dir"

last_exit=0
pass_number=1
run_codex_pass "$pass_number" "initial" || last_exit=$?

if (( last_exit != 0 )); then
  echo "failed" >"$status_file"
else
  while (( pass_number < max_passes )); do
    capture_session_id || break

    previous_head="$(git -C "$run_checkout" rev-parse HEAD)"
    pass_number=$((pass_number + 1))
    next_exit=0
    run_codex_pass "$pass_number" "resume" || next_exit=$?
    last_exit="$next_exit"

    if (( next_exit != 0 )); then
      break
    fi

    current_head="$(git -C "$run_checkout" rev-parse HEAD)"
    if [[ "$current_head" == "$previous_head" ]] && [[ -z "$(git -C "$run_checkout" status --porcelain)" ]]; then
      break
    fi
  done
fi

finished_at="$(date -u +%Y%m%dT%H%M%SZ)"
{
  echo "finished_at_utc=$finished_at"
  echo "codex_exit=$last_exit"
  echo "passes_completed=$pass_number"
  echo "git_status_after_run<<EOF"
  git -C "$run_checkout" status --short
  echo "EOF"
  echo "recent_commits<<EOF"
  git -C "$run_checkout" log --oneline -n 10
  echo "EOF"
} >>"$metadata_file"

if [[ "$last_exit" -eq 0 ]]; then
  echo "success" >"$status_file"
  echo "Run completed successfully."
else
  echo "failed" >"$status_file"
  echo "Run failed with exit code $last_exit" >&2
fi

if [[ -n "$worktree_path" ]]; then
  echo "Worktree: $worktree_path"
fi
echo "Branch: $run_branch"
echo "Status file: $status_file"
echo "Metadata: $metadata_file"
echo "Prompt copy: $prompt_copy"
echo "Console log: $console_log"
echo "Final message: $final_message_file"
if [[ -s "$session_id_file" ]]; then
  echo "Session ID file: $session_id_file"
fi

exit "$last_exit"
