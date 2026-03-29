#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: .codex/cleanup-merged-bug-hunt-worktrees.sh [options]

Finds bug-hunt worktrees created by .codex/run-weekly-bug-hunt.sh and removes
the ones whose branches are already merged into the target branch.

By default this is a dry run. Pass --apply to perform the cleanup.

Options:
  --into <branch>              Branch merged into check target (default: main)
  --branch-prefix <prefix>     Bug-hunt branch prefix
  --worktrees-dir <path>       Worktree directory root
  --apply                      Remove merged worktrees and delete their branches
  --help                       Show this help

Examples:
  .codex/cleanup-merged-bug-hunt-worktrees.sh
  .codex/cleanup-merged-bug-hunt-worktrees.sh --into develop
  .codex/cleanup-merged-bug-hunt-worktrees.sh --apply
EOF
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

into_branch="main"
branch_prefix="codex/weekly-bug-hunt/"
worktrees_dir=".worktrees"
apply=0

while (($# > 0)); do
  case "$1" in
    --into)
      into_branch="${2:?missing branch name}"
      shift 2
      ;;
    --branch-prefix)
      branch_prefix="${2:?missing branch prefix}"
      shift 2
      ;;
    --worktrees-dir)
      worktrees_dir="${2:?missing worktrees dir}"
      shift 2
      ;;
    --apply)
      apply=1
      shift
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

require_command git

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

if ! git show-ref --verify --quiet "refs/heads/$into_branch"; then
  echo "Target branch does not exist locally: $into_branch" >&2
  exit 1
fi

worktrees_root="$repo_root/$worktrees_dir"

current_worktree=""
current_branch_ref=""
declare -a candidates=()

while IFS= read -r line; do
  if [[ "$line" == worktree\ * ]]; then
    current_worktree="${line#worktree }"
    current_branch_ref=""
    continue
  fi

  if [[ "$line" == branch\ * ]]; then
    current_branch_ref="${line#branch }"
    branch_name="${current_branch_ref#refs/heads/}"

    if [[ "$current_worktree" == "$worktrees_root/"* ]] && [[ "$branch_name" == "$branch_prefix"* ]]; then
      candidates+=("$current_worktree|$branch_name")
    fi
  fi
done < <(git worktree list --porcelain)

if ((${#candidates[@]} == 0)); then
  echo "No matching bug-hunt worktrees found under $worktrees_root"
  exit 0
fi

echo "Target merge branch: $into_branch"
echo "Mode: $([[ $apply -eq 1 ]] && echo apply || echo dry-run)"

removed_count=0
skipped_count=0

for candidate in "${candidates[@]}"; do
  worktree_path="${candidate%%|*}"
  branch_name="${candidate#*|}"

  if [[ -n "$(git -C "$worktree_path" status --porcelain)" ]]; then
    echo "SKIP dirty    $branch_name    $worktree_path"
    skipped_count=$((skipped_count + 1))
    continue
  fi

  if ! git merge-base --is-ancestor "$branch_name" "$into_branch"; then
    echo "SKIP unmerged $branch_name    $worktree_path"
    skipped_count=$((skipped_count + 1))
    continue
  fi

  if (( apply )); then
    git worktree remove "$worktree_path"
    git branch -d "$branch_name"
    echo "REMOVED       $branch_name    $worktree_path"
  else
    echo "WOULD REMOVE  $branch_name    $worktree_path"
  fi

  removed_count=$((removed_count + 1))
done

echo "Matched: ${#candidates[@]}"
echo "Eligible: $removed_count"
echo "Skipped: $skipped_count"
