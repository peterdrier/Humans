#!/bin/bash
# Regenerate docs/reforge-history.csv — one row per DAY on main (the last
# commit of each day), with semantic codebase metrics from the reforge
# snapshot tool.
#
# Usage: bash docs/scripts/generate-reforge-history.sh [--full]
#
# Modes:
#   default: incremental — append rows for days strictly after the last date
#            already in the CSV.
#   --full:  rebuild from scratch by deleting the existing CSV first.
#
# Requirements:
#   - reforge CLI on PATH (install via `dotnet tool install -g Reforge`)
#   - bash 4+
#   - working tree may be dirty — historical per-day snapshots are computed
#     in a throwaway `git worktree add --detach` checkout, so the caller's
#     tree is never stashed, checked out, or otherwise touched (see
#     nobodies-collective/Humans#971: this used to stash the caller's tree
#     and leave it checked out at whatever commit the loop last visited on
#     interrupt — silently reverting concurrent edits from other processes
#     sharing this worktree, e.g. /freshness-sweep's drift-fix subagents).
#
# Implementation notes:
#   - Historical per-day snapshots are computed in a throwaway
#     `git worktree add --detach` checkout (SNAPSHOT_WORKTREE below). Every
#     `git checkout <commit>` and `reforge snapshot` in the loop is scoped to
#     that throwaway worktree, so the caller's HEAD and tracked files are
#     never touched — including on interrupt, since there's nothing to
#     restore in the caller's tree in the first place.
#   - Snapshots are written to per-iteration temp files OUTSIDE the worktree,
#     then concatenated into the CSV at the end. This avoids dirtying the
#     tracked CSV during the loop (which would block subsequent `git checkout`
#     inside the throwaway worktree).
#   - We avoid setting any shell variable named `TMP` / `TEMP` / `TMPDIR`,
#     because on Windows those names overlap with the env vars MSBuild uses
#     for its temp directory; clobbering them makes Roslyn's project loader
#     crash with "Cannot create '<path>' because a file or directory with the
#     same name already exists."

set -euo pipefail

CSV=docs/reforge-history.csv
SOLUTION=Humans.slnx

FULL=false
if [ "${1:-}" = "--full" ]; then
  FULL=true
fi

ORIG_REF=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "main")

# Use the system temp dir but DO NOT name our variables TMP/TEMP/TMPDIR.
WORK_DIR="${TMPDIR:-/tmp}/reforge-history-$$"
mkdir -p "$WORK_DIR"
GAP_ROWS="$WORK_DIR/gap-rows.csv"
SNAPSHOT_WORKTREE="$WORK_DIR/checkout"
> "$GAP_ROWS"

cleanup() {
  if [ -d "$SNAPSHOT_WORKTREE" ]; then
    git worktree remove --force "$SNAPSHOT_WORKTREE" 2>/dev/null \
      || { rm -rf -- "$SNAPSHOT_WORKTREE" 2>/dev/null || true; git worktree prune --quiet 2>/dev/null || true; }
  fi
  if [ -d "$WORK_DIR" ]; then
    rm -- "$WORK_DIR"/* 2>/dev/null || true
    rmdir "$WORK_DIR" 2>/dev/null || true
  fi
}
trap cleanup EXIT

# Determine date range to process.
if [ "$FULL" = "true" ] || [ ! -f "$CSV" ]; then
  rm -f "$CSV"
  RANGE="main"
else
  LAST_DATE=$(tail -n 1 "$CSV" | cut -d, -f1 | cut -dT -f1)
  if [ -z "$LAST_DATE" ]; then
    echo "Could not read last date from $CSV — falling back to --full"
    rm -f "$CSV"
    RANGE="main"
  else
    # Find the last commit that's on or before LAST_DATE so we can use it as
    # an exclusive lower bound for the git-log range.
    LAST_COMMIT=$(tail -n 1 "$CSV" | cut -d, -f2)
    LAST_FULL=$(git rev-parse "$LAST_COMMIT" 2>/dev/null || echo "")
    if [ -z "$LAST_FULL" ]; then
      echo "Last commit $LAST_COMMIT in CSV not found in repo — falling back to --full"
      rm -f "$CSV"
      RANGE="main"
    else
      RANGE="$LAST_FULL..main"
    fi
  fi
fi

# For each day in RANGE, pick the LAST commit of that day. `git log --reverse`
# yields commits chronologically (oldest first); the awk overwrites `last[day]`
# on each repeat so the final value is the latest commit of that day.
COMMITS=$(git log --reverse --format="%ad %H" --date=format:"%Y-%m-%d" "$RANGE" 2>/dev/null \
  | awk '
      { last[$1] = $2; if (!seen[$1]++) order[++n] = $1 }
      END { for (i=1; i<=n; i++) print last[order[i]] }
    ')

# In incremental mode, drop the first commit if it falls on LAST_DATE (we
# already have a row for that day).
if [ "$FULL" != "true" ] && [ -n "${LAST_DATE:-}" ]; then
  FILTERED=""
  for COMMIT in $COMMITS; do
    DAY=$(git log -1 --format="%ad" --date=format:"%Y-%m-%d" "$COMMIT")
    if [ "$DAY" != "$LAST_DATE" ]; then
      FILTERED="${FILTERED}${COMMIT}
"
    fi
  done
  COMMITS=$(printf '%s' "$FILTERED")
fi

if [ -z "$COMMITS" ]; then
  echo "No new days to snapshot."
  exit 0
fi

# Create the throwaway worktree that historical checkouts happen in. This
# never touches the caller's tree (see the header comment).
git worktree add --quiet --detach "$SNAPSHOT_WORKTREE" "$ORIG_REF"

TOTAL=$(echo "$COMMITS" | wc -l)
N=0
OK=0
FAIL=0

for COMMIT in $COMMITS; do
  N=$((N+1))
  SNAP="$WORK_DIR/snap-${COMMIT:0:8}.csv"
  > "$SNAP"
  if ! git -C "$SNAPSHOT_WORKTREE" checkout --quiet "$COMMIT" 2>/dev/null; then
    echo "[$N/$TOTAL] $COMMIT — checkout FAILED"
    FAIL=$((FAIL+1))
    continue
  fi
  # Run reforge from INSIDE the throwaway worktree. reforge relays any command
  # to a running hot server when it finds a `.reforge-port` file — first next to
  # `--solution`, then by searching upward from the CWD. The throwaway worktree
  # has no port file, but the caller's repo does whenever `reforge serve` is
  # running there, so invoking from the caller's cwd would relay `snapshot` to a
  # server holding the caller's checkout and silently record rows for the wrong
  # commit. The subshell keeps the caller's cwd unchanged for the merge below.
  if ( cd "$SNAPSHOT_WORKTREE" && reforge snapshot --solution "$SNAPSHOT_WORKTREE/$SOLUTION" --append "$SNAP" >/dev/null 2>&1 ) && [ -s "$SNAP" ]; then
    # Strip header (first line); append the data row to the gap accumulator.
    tail -n +2 "$SNAP" >> "$GAP_ROWS"
    OK=$((OK+1))
  else
    echo "[$N/$TOTAL] $COMMIT — reforge snapshot FAILED"
    FAIL=$((FAIL+1))
  fi
  # Reset the tracked CSV in the throwaway worktree before next checkout
  # (otherwise `git checkout` there aborts on the dirty file).
  git -C "$SNAPSHOT_WORKTREE" checkout --quiet HEAD -- "$CSV" 2>/dev/null || true
done

# Merge: existing CSV (or empty if --full) + gap rows. Dedup by date column
# (latest timestamp wins). Sort by timestamp ascending. The caller's tree was
# never touched, so $CSV is read/written here directly.
HEADER=""
if [ -f "$CSV" ]; then
  HEADER=$(head -1 "$CSV")
fi
if [ -z "$HEADER" ]; then
  # First-time run: take the header from any successful snapshot.
  ANY_SNAP=$(ls -1 "$WORK_DIR"/snap-*.csv 2>/dev/null | head -1)
  if [ -n "$ANY_SNAP" ]; then
    HEADER=$(head -1 "$ANY_SNAP")
  fi
fi

MERGED="$WORK_DIR/merged.csv"
{
  [ -f "$CSV" ] && tail -n +2 "$CSV"
  cat "$GAP_ROWS"
} | sort -t, -k1,1 | awk -F, '
    { date=substr($1,1,10); row[date]=$0 }
    END { for (d in row) print row[d] }
  ' | sort -t, -k1,1 > "$MERGED"

{
  [ -n "$HEADER" ] && echo "$HEADER"
  cat "$MERGED"
} > "$CSV"

ROWS=$(($(wc -l < "$CSV") - 1))
echo "Done. ok=$OK fail=$FAIL — CSV has $ROWS rows ($(tail -n +2 "$CSV" | cut -d, -f1 | cut -dT -f1 | sort -u | wc -l) distinct days)."
