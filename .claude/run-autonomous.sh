#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: .claude/run-autonomous.sh [options]

Runs Claude Code non-interactively in an isolated worktree. Supports both
classic multi-pass (resume) mode and tiered mode (fresh context per tier,
model escalation from Sonnet to Opus).

Modes:
  bug-hunt   Use the autonomous bug-fix prompt
  tech-debt  Use the autonomous tech-debt prompt

Options:
  --mode <name>                Run mode: bug-hunt or tech-debt
  --model <name>               Claude model override (sonnet, opus, haiku)
  --tiered                     Run in tiered mode (low→medium→high→super)
  --tiers <list>               Comma-separated tier names (default: low,medium,high,super)
  --prompt <path>              Prompt file override
  --runs-dir <path>            Artifact directory root override
  --worktrees-dir <path>       Worktree directory root
  --branch-prefix <prefix>     Branch prefix override
  --allow-dirty                Run even if the root worktree is dirty
  --no-worktree                Run in the current checkout instead
  --no-branch                  Reuse the current branch instead of creating one
  --safe                       Run without --dangerously-skip-permissions
  --max-passes <count>         Total passes (classic mode only, default: 3)
  --max-turns <count>          Max agentic turns per pass
  --push-remote <name>         Remote to push commits to after each pass
  --no-push                    Disable automatic pushing after each pass
  --extra-instruction <text>   Append an extra instruction before the prompt
  --help                       Show this help

Examples:
  .claude/run-autonomous.sh                              # classic bug-hunt
  .claude/run-autonomous.sh --tiered                     # tiered bug-hunt (sonnet→opus)
  .claude/run-autonomous.sh --tiered --mode tech-debt    # tiered tech-debt
  .claude/run-autonomous.sh --tiered --tiers low,medium  # just the cheap tiers
  .claude/run-autonomous.sh --max-passes 4 --model opus  # classic with model override
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
tiered=0
tiers_csv="low,medium,high,super"
prompt_override=""
runs_dir_override=""
worktrees_dir=".worktrees"
branch_prefix_override=""
allow_dirty=0
use_worktree=1
create_branch=1
dangerous=1
max_passes=3
max_turns=""
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
    --tiered)
      tiered=1
      shift
      ;;
    --tiers)
      tiers_csv="${2:?missing tier list}"
      tiered=1
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
    --max-turns)
      max_turns="${2:?missing turn count}"
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

require_command claude
require_command git
require_command date
require_command mkdir
require_command tee
require_command grep
require_command sed
require_command jq

case "$mode" in
  bug-hunt)
    prompt_path="${prompt_override:-.claude/bug-hunt-prompt.md}"
    runs_dir="${runs_dir_override:-local/claude-runs/bug-hunt}"
    branch_prefix="${branch_prefix_override:-claude/weekly-bug-hunt}"
    tiers_file=".claude/tiers/bug-hunt.json"
    ;;
  tech-debt)
    prompt_path="${prompt_override:-.claude/tech-debt-prompt.md}"
    runs_dir="${runs_dir_override:-local/claude-runs/tech-debt}"
    branch_prefix="${branch_prefix_override:-claude/weekly-tech-debt}"
    tiers_file=".claude/tiers/tech-debt.json"
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

if [[ -n "$max_turns" ]] && { ! [[ "$max_turns" =~ ^[0-9]+$ ]] || (( max_turns < 1 )); }; then
  echo "--max-turns must be a positive integer" >&2
  exit 1
fi

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

if [[ ! -f "$prompt_path" ]]; then
  echo "Prompt file not found: $prompt_path" >&2
  exit 1
fi

if (( tiered )) && [[ ! -f "$tiers_file" ]]; then
  echo "Tiers file not found: $tiers_file" >&2
  exit 1
fi

if git remote get-url origin >/dev/null 2>&1; then
  git fetch origin main >/dev/null 2>&1 || true
fi

if (( auto_push )) && ! git remote get-url "$push_remote" >/dev/null 2>&1; then
  echo "Push remote does not exist: $push_remote" >&2
  exit 1
fi

# Parse requested tiers
IFS=',' read -ra requested_tiers <<< "$tiers_csv"

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
# Use absolute paths for all artifacts so they resolve correctly from worktrees
run_dir="$repo_root/$runs_dir/$timestamp"
mkdir -p "$run_dir"

status_file="$run_dir/status.txt"
metadata_file="$run_dir/metadata.txt"
console_log="$run_dir/claude.log"
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
  .claude/run-autonomous.sh --allow-dirty
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
  .claude/run-autonomous.sh --no-worktree --no-branch
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
  echo "tiered=$tiered"
  if (( tiered )); then
    echo "tiers=$tiers_csv"
  fi
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
  echo "max_turns=$max_turns"
  echo "auto_push=$auto_push"
  echo "push_remote=$push_remote"
  if [[ -n "$model" ]]; then
    echo "model_override=$model"
  fi
} >"$metadata_file"
echo "running" >"$status_file"
: >"$pass_history_file"

# Build the base preamble (shared across modes)
build_preamble() {
  local tier_label="${1:-}"

  if (( auto_push )); then
    push_instruction="- After every commit you create, push the current branch to $push_remote so progress is visible remotely."
  else
    push_instruction="- Do not push during this run unless explicitly instructed later."
  fi

  cat <<EOF
You are running from the committed wrapper script at .claude/run-autonomous.sh.

Execution context:
- Repository root: $repo_root
- Execution checkout: $run_checkout
- Prompt source: $prompt_path
- Artifact directory for this run: $run_dir
- Current git branch for this run: $run_branch
- Push remote for published progress: $push_remote
EOF

  if [[ -n "$tier_label" ]]; then
    printf '- Current tier: %s\n' "$tier_label"
  fi

  cat <<EOF

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
}

# Build a tier-specific prompt (fresh context with git log awareness)
build_tier_prompt() {
  local tier_name="$1"
  local tier_model="$2"
  local tier_label="$3"
  local tier_focus="$4"
  local prompt_file="$run_dir/prompt-${tier_name}.md"

  {
    build_preamble "$tier_label"

    cat <<EOF
IMPORTANT: This is a FRESH context pass. Previous tiers have already made fixes.
Before starting, run: git log --oneline -20
Review what has already been committed and DO NOT repeat that work.

--- TIER FOCUS: ${tier_label} ---

${tier_focus}

--- END TIER FOCUS ---

Below is the full reference prompt. Use it for context (architecture, coding rules,
exclusion zones, safety checks) but focus your work on the tier-specific phases above.

EOF

    cat "$prompt_path"
  } >"$prompt_file"

  printf '%s' "$prompt_file"
}

write_pass_log_marker() {
  local pass_number="$1"
  local pass_kind="$2"
  {
    printf "\n===== PASS %s (%s) =====\n" "$pass_number" "$pass_kind"
  } >>"$console_log"
}

capture_session_id() {
  # In tiered mode, session_id is per-tier (overwritten each tier).
  # In classic mode, it persists across resumes.
  local extracted
  extracted="$(grep -oP '"session_id"\s*:\s*"\K[^"]+' "$console_log" 2>/dev/null | tail -1)" || true
  if [[ -n "$extracted" ]]; then
    printf '%s\n' "$extracted" >"$session_id_file"
    printf 'session_id=%s\n' "$extracted" >>"$metadata_file"
    return 0
  fi

  return 1
}

extract_final_message() {
  # Extract the last assistant text content from stream-json output.
  local last_text
  last_text="$(grep '"type":"assistant"' "$console_log" 2>/dev/null | tail -1 \
    | jq -r '[.message.content[]? | select(.type == "text") | .text] | join("\n")' 2>/dev/null)" || true
  if [[ -n "$last_text" ]]; then
    printf '%s\n' "$last_text" >"$final_message_file"
  fi

  # Also capture run summary from the result line
  local result_line
  result_line="$(grep '"type":"result"' "$console_log" 2>/dev/null | tail -1)" || true
  if [[ -n "$result_line" ]]; then
    {
      echo ""
      echo "---"
      echo "Run metadata:"
      echo "$result_line" | jq '{turns: .num_turns, cost_usd: .total_cost_usd, stop_reason: .stop_reason, subtype: .subtype}' 2>/dev/null || true
    } >>"$final_message_file"
  fi
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

# Run a single claude pass (fresh context — no resume)
run_fresh_pass() {
  local pass_number="$1"
  local pass_label="$2"
  local pass_model="$3"
  local prompt_file="$4"
  local pass_start_head
  local pass_end_head
  local pass_exit
  local -a claude_args

  pass_start_head="$(git -C "$run_checkout" rev-parse HEAD)"
  write_pass_log_marker "$pass_number" "$pass_label"

  claude_args=(-p --verbose --output-format stream-json)
  if [[ -n "$max_turns" ]]; then
    claude_args+=(--max-turns "$max_turns")
  fi
  # Model override from CLI takes precedence, otherwise use the pass-specific model
  if [[ -n "$model" ]]; then
    claude_args+=(--model "$model")
  elif [[ -n "$pass_model" ]]; then
    claude_args+=(--model "$pass_model")
  fi
  if (( dangerous )); then
    claude_args+=(--dangerously-skip-permissions)
  fi

  echo "  Model: ${model:-$pass_model}"
  echo "  Prompt: $prompt_file"

  set +e
  (cd "$run_checkout" && claude "${claude_args[@]}" < "$prompt_file") 2>&1 | tee -a "$console_log"
  pass_exit="${PIPESTATUS[0]}"
  set -e

  capture_session_id || true
  extract_final_message
  pass_end_head="$(git -C "$run_checkout" rev-parse HEAD)"
  record_pass "$pass_number" "$pass_label" "$pass_start_head" "$pass_end_head" "$pass_exit"

  if (( pass_exit == 0 )); then
    push_branch_if_enabled
  fi

  return "$pass_exit"
}

# Run a resume pass (continues existing session)
run_resume_pass() {
  local pass_number="$1"
  local pass_start_head
  local pass_end_head
  local pass_exit
  local -a claude_args

  if [[ ! -s "$session_id_file" ]]; then
    echo "No session ID available for resume" >&2
    return 1
  fi

  local session_id
  session_id="$(<"$session_id_file")"

  pass_start_head="$(git -C "$run_checkout" rev-parse HEAD)"
  write_pass_log_marker "$pass_number" "resume"

  claude_args=(-p --resume "$session_id" --verbose --output-format stream-json)
  if [[ -n "$max_turns" ]]; then
    claude_args+=(--max-turns "$max_turns")
  fi
  if [[ -n "$model" ]]; then
    claude_args+=(--model "$model")
  fi
  if (( dangerous )); then
    claude_args+=(--dangerously-skip-permissions)
  fi

  local resume_prompt
  if (( auto_push )); then
    resume_prompt="Continue the autonomous ${mode} run from where you left off. Do not repeat completed work. Use the existing branch and checkout, continue making small verified commits, and push ${run_branch} to ${push_remote} after each new commit."
  else
    resume_prompt="Continue the autonomous ${mode} run from where you left off. Do not repeat completed work. Use the existing branch and checkout, continue making small verified commits, and do not push during this run."
  fi

  set +e
  (cd "$run_checkout" && claude "${claude_args[@]}" "$resume_prompt") 2>&1 | tee -a "$console_log"
  pass_exit="${PIPESTATUS[0]}"
  set -e

  capture_session_id || true
  extract_final_message
  pass_end_head="$(git -C "$run_checkout" rev-parse HEAD)"
  record_pass "$pass_number" "resume" "$pass_start_head" "$pass_end_head" "$pass_exit"

  if (( pass_exit == 0 )); then
    push_branch_if_enabled
  fi

  return "$pass_exit"
}

echo "Launching Claude in $run_checkout on branch $run_branch"
echo "Artifacts will be written to $run_dir"

last_exit=0

if (( tiered )); then
  # ──────────────────────────────────────────────────────────
  # TIERED MODE: fresh context per tier, model per tier
  # ──────────────────────────────────────────────────────────
  echo "Mode: tiered (${tiers_csv})"

  pass_number=0
  for tier_name in "${requested_tiers[@]}"; do
    # Look up tier config from JSON
    tier_json="$(jq -r --arg name "$tier_name" '.tiers[] | select(.name == $name)' "$tiers_file")"
    if [[ -z "$tier_json" || "$tier_json" == "null" ]]; then
      echo "Warning: tier '$tier_name' not found in $tiers_file, skipping" >&2
      continue
    fi

    tier_model="$(echo "$tier_json" | jq -r '.model')"
    tier_label="$(echo "$tier_json" | jq -r '.label')"
    tier_focus="$(echo "$tier_json" | jq -r '.focus')"

    pass_number=$((pass_number + 1))
    echo ""
    echo "===== TIER $pass_number: $tier_name ($tier_label) ====="

    # Build a fresh prompt for this tier
    prompt_file="$(build_tier_prompt "$tier_name" "$tier_model" "$tier_label" "$tier_focus")"

    printf 'tier=%s model=%s label=%s\n' "$tier_name" "${model:-$tier_model}" "$tier_label" >>"$metadata_file"

    tier_exit=0
    run_fresh_pass "$pass_number" "tier:$tier_name" "$tier_model" "$prompt_file" || tier_exit=$?
    last_exit="$tier_exit"

    if (( tier_exit != 0 )); then
      echo "Tier $tier_name failed with exit code $tier_exit" >&2
      # Continue to next tier — a failure in one tier shouldn't block others
    fi
  done
else
  # ──────────────────────────────────────────────────────────
  # CLASSIC MODE: initial prompt + resume passes
  # ──────────────────────────────────────────────────────────
  echo "Mode: classic (max_passes=$max_passes)"

  # Build the initial prompt
  prompt_copy="$run_dir/prompt.md"
  {
    build_preamble ""
    cat "$prompt_path"
  } >"$prompt_copy"

  pass_number=1
  run_fresh_pass "$pass_number" "initial" "" "$prompt_copy" || last_exit=$?

  if (( last_exit != 0 )); then
    echo "failed" >"$status_file"
  else
    while (( pass_number < max_passes )); do
      capture_session_id || break

      previous_head="$(git -C "$run_checkout" rev-parse HEAD)"
      pass_number=$((pass_number + 1))
      next_exit=0
      run_resume_pass "$pass_number" || next_exit=$?
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
fi

finished_at="$(date -u +%Y%m%dT%H%M%SZ)"
{
  echo "finished_at_utc=$finished_at"
  echo "claude_exit=$last_exit"
  echo "passes_completed=${pass_number:-0}"
  echo "git_status_after_run<<EOF"
  git -C "$run_checkout" status --short
  echo "EOF"
  echo "recent_commits<<EOF"
  git -C "$run_checkout" log --oneline -n 20
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
echo "Console log: $console_log"
echo "Final message: $final_message_file"
echo "Pass history: $pass_history_file"

exit "$last_exit"
