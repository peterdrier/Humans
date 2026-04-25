#!/bin/bash
# Regenerate docs/reforge-history.csv — one row per commit on main, with
# semantic codebase metrics from the reforge snapshot tool.
#
# Usage: bash docs/scripts/generate-reforge-history.sh [--full]
#
# Modes:
#   default: incremental — append rows for commits since the last row in the existing CSV.
#   --full:  rebuild from scratch by deleting the existing CSV first.
#
# Requirements:
#   - reforge CLI on PATH (https://github.com/...; install via `dotnet tool install -g Reforge`)
#   - clean working tree (will stash if needed and restore on exit)
#   - Humans.slnx as the analysis solution
#   - bash 4+
#
# Anchored on `main` because the CSV is a per-commit history of the canonical line.

set -euo pipefail

CSV=docs/reforge-history.csv
SOLUTION=Humans.slnx

FULL=false
if [ "${1:-}" = "--full" ]; then
  FULL=true
fi

# Stash any dirty state so checkout iteration is safe.
ORIG_REF=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "main")
NEEDS_STASH=false
if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
  git stash --quiet
  NEEDS_STASH=true
fi

cleanup() {
  git checkout --quiet "$ORIG_REF" || true
  if [ "$NEEDS_STASH" = "true" ]; then
    git stash pop --quiet || true
  fi
}
trap cleanup EXIT

# Determine commit range.
if [ "$FULL" = "true" ] || [ ! -f "$CSV" ]; then
  rm -f "$CSV"
  COMMITS=$(git log --reverse --format=%H main)
else
  LAST_COMMIT=$(tail -n 1 "$CSV" | cut -d, -f2)
  if [ -z "$LAST_COMMIT" ]; then
    echo "Could not read last commit from $CSV — falling back to --full"
    rm -f "$CSV"
    COMMITS=$(git log --reverse --format=%H main)
  else
    # The CSV records short SHAs; resolve to full first.
    LAST_FULL=$(git rev-parse "$LAST_COMMIT" 2>/dev/null || echo "")
    if [ -z "$LAST_FULL" ]; then
      echo "Last commit $LAST_COMMIT in CSV not found in repo — falling back to --full"
      rm -f "$CSV"
      COMMITS=$(git log --reverse --format=%H main)
    else
      COMMITS=$(git log --reverse --format=%H "$LAST_FULL..main")
    fi
  fi
fi

ROWS_BEFORE=$(wc -l < "$CSV" 2>/dev/null || echo 0)
ADDED=0

for COMMIT in $COMMITS; do
  git checkout --quiet "$COMMIT"
  # `reforge snapshot --append` writes a header if the file doesn't exist
  # and otherwise appends a single row reflecting the current working tree.
  if reforge snapshot --solution "$SOLUTION" --append "$CSV" >/dev/null 2>&1; then
    ADDED=$((ADDED + 1))
  else
    echo "reforge snapshot failed on $COMMIT — skipping"
  fi
done

ROWS_AFTER=$(wc -l < "$CSV" 2>/dev/null || echo 0)
echo "Done. Rows: $ROWS_BEFORE → $ROWS_AFTER (+$ADDED)."
