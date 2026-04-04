#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: .claude/run-autonomous.sh [options]

Runs Claude Code non-interactively in an isolated worktree. Each pass gets
fresh context (no session resume). Supports classic multi-pass mode and
tiered mode (model escalation from Sonnet to Opus).

Modes:
  bug-hunt         Autonomous bug-fix (find and fix by category)
  tech-debt        Autonomous tech-debt discovery (agent decides what to improve)
  tech-debt-tasks  Execute specific tasks from the tech-debt backlog

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
  --max-passes <count>         Total fresh passes (classic mode, default: 3)
  --max-turns <count>          Max agentic turns per pass (unreliable, see #20521)
  --push-remote <name>         Remote to push commits to after each pass
  --no-push                    Disable automatic pushing after each pass
  --leads <path>               Seed handoff file from a previous run's leads
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
leads_file=""
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
    --leads)
      leads_file="${2:?missing leads file path}"
      shift 2
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
  tech-debt-tasks)
    prompt_path="${prompt_override:-.claude/tech-debt-tasks-prompt.md}"
    runs_dir="${runs_dir_override:-local/claude-runs/tech-debt-tasks}"
    branch_prefix="${branch_prefix_override:-claude/tech-debt-tasks}"
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

# Fetch and check if local main is up to date
has_upstream=0
if git remote get-url upstream >/dev/null 2>&1; then
  has_upstream=1
  git fetch upstream main >/dev/null 2>&1 || true
fi
if git remote get-url origin >/dev/null 2>&1; then
  git fetch origin main >/dev/null 2>&1 || true
fi

# Determine the authoritative ref to compare against
if (( has_upstream )); then
  auth_ref="upstream/main"
else
  auth_ref="origin/main"
fi

if git show-ref --verify --quiet "refs/remotes/${auth_ref}"; then
  local_head="$(git rev-parse HEAD)"
  remote_head="$(git rev-parse "$auth_ref")"
  if [[ "$local_head" != "$remote_head" ]]; then
    behind="$(git rev-list --count HEAD.."$auth_ref" 2>/dev/null || echo 0)"
    ahead="$(git rev-list --count "$auth_ref"..HEAD 2>/dev/null || echo 0)"
    if (( behind > 0 )); then
      cat >&2 <<EOF
WARNING: Local main is ${behind} commit(s) behind ${auth_ref}.

Sync before running to avoid working on stale code:
EOF
      if (( has_upstream )); then
        cat >&2 <<EOF
  git fetch upstream main
  git checkout main && git reset --hard upstream/main
  git push origin main --force-with-lease
EOF
      else
        cat >&2 <<EOF
  git pull origin main
EOF
      fi
      if (( !allow_dirty )); then
        echo "Rerun with --allow-dirty to skip this check." >&2
        exit 1
      fi
    fi
  fi
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
handoff_file="$run_dir/tier-handoff.md"

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

# Seed handoff file from leads if provided
if [[ -n "$leads_file" ]]; then
  if [[ ! -f "$leads_file" ]]; then
    echo "Leads file not found: $leads_file" >&2
    exit 1
  fi
  cp "$leads_file" "$handoff_file"
  echo "Seeded handoff from: $leads_file"
fi

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
    printf '%s\n' "- Current tier: $tier_label"
  fi

  if [[ -n "$max_turns" ]]; then
    printf '%s\n' "" "- Turn budget: $max_turns tool calls. Plan your work to fit. Commit early and often — if you run out of turns mid-fix, uncommitted work is lost."
  fi

  cat <<EOF

Operating instructions for this automated run:
- Work fully autonomously inside this repository.
- Keep any temporary files or status artifacts under local/.
- Work only inside the current checkout at $run_checkout.
- Use the current git branch. Do not switch branches.
$push_instruction
- End with a concise run report that lists bugs fixed or refactors completed, commands run, and any unfinished leads.
- If the file ${handoff_file} exists, read it at the start for notes from previous passes.
- Read and follow CLAUDE.md plus relevant files under .claude/ when they help you interpret local project conventions.
- Work in BITE-SIZED pieces: find one issue, fix it, build, commit. Do not accumulate uncommitted changes across multiple fixes.
- Aim to wrap up within ~50 tool calls. Commit and write handoff notes before context gets too large. Another fresh pass may follow.

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
  local tier_current="$5"
  local tier_total="$6"
  local prompt_file="$run_dir/prompt-${tier_name}.md"
  local is_last=0
  if (( tier_current == tier_total )); then
    is_last=1
  fi

  {
    build_preamble "$tier_label"

    cat <<EOF
IMPORTANT: This is a FRESH context pass. Previous tiers have already made fixes.
This is tier ${tier_current} of ${tier_total}.
EOF

    if (( is_last )); then
      cat <<'EOF'
THIS IS THE FINAL TIER. There are no more passes after this one. Prioritize:
- Committing any in-progress work before you run out of context
- Writing a thorough handoff file with all remaining leads
- Do not start large changes you cannot finish — commit what you have
EOF
    else
      cat <<EOF
There are $((tier_total - tier_current)) more tier(s) after this one.
EOF
    fi

    cat <<EOF

Before starting:
1. Check if ${handoff_file} exists — if so, read it first. It contains notes from
   the previous tier: what was investigated and found clean, promising leads not yet
   fixed, partially analyzed files, and patterns worth knowing about.
2. Run: git log --oneline -20
   Review what has already been committed and DO NOT repeat that work.

Before you finish, write your handoff notes to ${handoff_file} (append, don't overwrite):
- Section header: "## Tier: ${tier_name} (${tier_label})"
- What you investigated and found clean (no bug/no debt) — so the next tier skips it
- Leads you found but didn't fix (with file paths and line numbers)
- Files you partially analyzed (how far you got)
- Patterns worth noting for the next tier
- Any issues you hit (build failures, test failures, ambiguous code)

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
  # Session ID captured for metadata only (no resume).
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

# Build a classic-mode fresh pass prompt (with handoff awareness)
build_classic_pass_prompt() {
  local pass_number="$1"
  local total_passes="$2"
  local prompt_file="$run_dir/prompt-pass${pass_number}.md"

  {
    build_preamble ""

    if (( pass_number > 1 )); then
      cat <<EOF
IMPORTANT: This is pass ${pass_number} of ${total_passes} (FRESH context — no prior conversation).

Before starting:
1. Check if ${handoff_file} exists — read it for notes from previous passes.
2. Run: git log --oneline -20
   Review what has already been committed and DO NOT repeat that work.

EOF
    fi

    if (( pass_number == total_passes )); then
      cat <<'EOF'
THIS IS THE FINAL PASS. There are no more passes after this one. Prioritize:
- Committing any in-progress work before you run out of context
- Writing a thorough handoff file with all remaining leads
- Do not start large changes you cannot finish — commit what you have

EOF
    fi

    cat <<EOF
Before you finish, write your handoff notes to ${handoff_file} (append, don't overwrite):
- Section header: "## Pass ${pass_number}"
- What you investigated and found clean
- Leads you found but didn't fix (with file paths and line numbers)
- Files you partially analyzed
- Any issues you hit

EOF

    cat "$prompt_path"
  } >"$prompt_file"

  printf '%s' "$prompt_file"
}

echo "Launching Claude in $run_checkout on branch $run_branch"
echo "Artifacts will be written to $run_dir"

last_exit=0

if (( tiered )); then
  # ──────────────────────────────────────────────────────────
  # TIERED MODE: fresh context per tier, model per tier
  # ──────────────────────────────────────────────────────────
  tier_total=${#requested_tiers[@]}
  echo "Mode: tiered (${tiers_csv}, ${tier_total} tiers, max_passes=${max_passes}/tier)"

  pass_number=0
  for tier_name in "${requested_tiers[@]}"; do
    # Look up tier config from JSON
    tier_json="$(jq -r --arg name "$tier_name" '.tiers[] | select(.name == $name)' "$tiers_file")"
    if [[ -z "$tier_json" || "$tier_json" == "null" ]]; then
      echo "Warning: tier '$tier_name' not found in $tiers_file, skipping" >&2
      tier_total=$((tier_total - 1))
      continue
    fi

    tier_model="$(echo "$tier_json" | jq -r '.model')"
    tier_label="$(echo "$tier_json" | jq -r '.label')"
    tier_focus="$(echo "$tier_json" | jq -r '.focus')"

    tier_pass=0
    while (( tier_pass < max_passes )); do
      previous_head="$(git -C "$run_checkout" rev-parse HEAD)"
      tier_pass=$((tier_pass + 1))
      pass_number=$((pass_number + 1))

      echo ""
      if (( max_passes > 1 )); then
        echo "===== TIER $tier_name pass $tier_pass/$max_passes ($tier_label) ====="
      else
        echo "===== TIER $tier_name ($tier_label) ====="
      fi

      # Build a fresh prompt for this tier pass
      prompt_file="$(build_tier_prompt "$tier_name" "$tier_model" "$tier_label" "$tier_focus" "$pass_number" "$((pass_number + tier_total - 1))")"

      printf 'tier=%s pass=%s model=%s label=%s\n' "$tier_name" "$tier_pass" "${model:-$tier_model}" "$tier_label" >>"$metadata_file"

      tier_exit=0
      run_fresh_pass "$pass_number" "tier:${tier_name}:${tier_pass}" "$tier_model" "$prompt_file" || tier_exit=$?
      last_exit="$tier_exit"

      if (( tier_exit != 0 )); then
        echo "Tier $tier_name pass $tier_pass failed with exit code $tier_exit" >&2
        break
      fi

      # Stop this tier if no progress
      current_head="$(git -C "$run_checkout" rev-parse HEAD)"
      if [[ "$current_head" == "$previous_head" ]] && [[ -z "$(git -C "$run_checkout" status --porcelain)" ]]; then
        echo "No progress in tier $tier_name pass $tier_pass — moving to next tier"
        break
      fi
    done
  done
else
  # ──────────────────────────────────────────────────────────
  # CLASSIC MODE: fresh context per pass with progress detection
  # ──────────────────────────────────────────────────────────
  echo "Mode: classic (max_passes=$max_passes)"

  pass_number=0
  while (( pass_number < max_passes )); do
    previous_head="$(git -C "$run_checkout" rev-parse HEAD)"
    pass_number=$((pass_number + 1))

    echo ""
    echo "===== PASS $pass_number/$max_passes ====="

    prompt_file="$(build_classic_pass_prompt "$pass_number" "$max_passes")"

    pass_exit=0
    run_fresh_pass "$pass_number" "pass:$pass_number" "" "$prompt_file" || pass_exit=$?
    last_exit="$pass_exit"

    if (( pass_exit != 0 )); then
      echo "Pass $pass_number failed with exit code $pass_exit" >&2
      break
    fi

    # Stop if no progress (no new commits and clean working dir)
    current_head="$(git -C "$run_checkout" rev-parse HEAD)"
    if [[ "$current_head" == "$previous_head" ]] && [[ -z "$(git -C "$run_checkout" status --porcelain)" ]]; then
      echo "No progress in pass $pass_number — stopping"
      break
    fi
  done
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
