#!/bin/bash
# Append per-day codebase metrics to the Codebase Growth table at the END of
# docs/development-stats.md.
#
# Usage: cd <repo-root> && bash docs/scripts/generate-stats.sh [--full]
#
# Modes:
#   default: incremental — read the last data row in the table, snapshot the
#            LAST commit of every day after that, append rows in place.
#   --full:  drop every existing data row from the table, then regenerate the
#            entire history from scratch. Use after changing the table schema.
#
# Requirements:
#   - bash 4+ (associative arrays)
#   - GNU sed (for the comma-grouping regex; ships with Git Bash on Windows)
#   - working tree may be dirty (script stashes everything and restores after)
#
# Implementation notes:
#   - One row per DAY using the LAST commit of that day on the current ref.
#   - File sizes reported in KB (rounded). Per-language line counts split out.
#   - The Codebase Growth table MUST be the last section in the file. Sections
#     above (Quick Summary, Language Breakdown, Highlights, Column Key) are
#     manually maintained and not touched by this script.
#   - The script never writes to docs/development-stats.md during the
#     iteration loop (writing to a tracked file makes the next `git checkout
#     <commit>` abort with "Your local changes would be overwritten"). The
#     table rows are accumulated in a temp file outside the worktree, then
#     concatenated onto the doc after we restore the original ref.

set -euo pipefail

DOC=docs/development-stats.md

if [ ! -f "$DOC" ]; then
  echo "Error: $DOC not found. Run from the repo root." >&2
  exit 1
fi

FULL=false
if [ "${1:-}" = "--full" ]; then
  FULL=true
fi

ORIG_REF=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "main")
NEEDS_STASH=false

# Capture the doc's manually-maintained content (everything down to and
# including the table separator row) BEFORE we touch the working tree. The
# data rows below the separator will be regenerated (in --full mode) or
# augmented (incremental mode) below.
WORK_DIR="${TMPDIR:-/tmp}/dev-stats-$$"
mkdir -p "$WORK_DIR"
PREAMBLE="$WORK_DIR/preamble.md"
EXISTING_ROWS="$WORK_DIR/existing-rows.md"
NEW_ROWS="$WORK_DIR/new-rows.md"
> "$EXISTING_ROWS"
> "$NEW_ROWS"

# Split $DOC at the first table-separator line under "## Codebase Growth"
# (i.e. the `|------|------|...` line). Everything up to and including that
# separator goes into PREAMBLE; everything after goes into EXISTING_ROWS.
awk '
  BEGIN { in_table = 0; preamble_done = 0 }
  /^## Codebase Growth/ { in_table = 1 }
  in_table && /^\|.*---/ && !preamble_done {
    print > preamble
    preamble_done = 1
    next
  }
  preamble_done {
    print > rows
    next
  }
  { print > preamble }
' preamble="$PREAMBLE" rows="$EXISTING_ROWS" "$DOC"

if [ ! -s "$PREAMBLE" ]; then
  echo "Error: could not split $DOC at the Codebase Growth separator row." >&2
  exit 1
fi

# In incremental mode, we keep the existing data rows and only add new ones.
# In --full mode, we discard them.
if [ "$FULL" = "true" ]; then
  > "$EXISTING_ROWS"
  LAST_DATE=""
else
  # Last data row's date. Date row format: `| YYYY-MM-DD | ...`
  LAST_DATE=$(grep -E '^\| [0-9]{4}-[0-9]{2}-[0-9]{2} ' "$EXISTING_ROWS" | tail -1 | awk -F'|' '{ gsub(/^ +| +$/, "", $2); print $2 }' || true)
fi

cleanup() {
  git checkout --quiet "$ORIG_REF" 2>/dev/null || true
  if [ "$NEEDS_STASH" = "true" ]; then
    git stash pop --quiet 2>/dev/null || true
  fi
  if [ -d "$WORK_DIR" ]; then
    rm -- "$WORK_DIR"/*.md 2>/dev/null || true
    rmdir "$WORK_DIR" 2>/dev/null || true
  fi
}
trap cleanup EXIT

# Stash anything dirty so checkouts run cleanly.
if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
  git stash --quiet --include-untracked
  NEEDS_STASH=true
fi

# Format helpers.
fmt() { echo "$1" | sed -e ':a' -e 's/\B[0-9]\{3\}\>/,&/' -e 'ta'; }
to_kb() { awk -v b="$1" 'BEGIN { printf "%d\n", int((b + 512) / 1024) }'; }

# Last-commit-of-each-day on the current ref, oldest first.
DAY_COMMITS=$(git log --reverse --format="%ad %H" --date=format:"%Y-%m-%d" 2>/dev/null \
  | awk '{ last[$1] = $2; if (!seen[$1]++) order[++n] = $1 }
         END { for (i=1; i<=n; i++) print order[i] " " last[order[i]] }')

# Filter to days strictly after LAST_DATE (in incremental mode).
if [ -n "$LAST_DATE" ]; then
  DAY_COMMITS=$(echo "$DAY_COMMITS" | awk -v cutoff="$LAST_DATE" '$1 > cutoff')
fi

if [ -z "$DAY_COMMITS" ]; then
  echo "No new days to snapshot."
  exit 0
fi

# Cumulative commit-count by day across full history (independent of filter).
declare -A cum_commits
total=0
while read -r _hash day; do
  total=$((total + 1))
  cum_commits["$day"]=$total
done < <(git log --reverse --format="%H %ad" --date=format:"%Y-%m-%d")

# Per-day line +/- (excluding migrations) across full history.
declare -A day_adds day_dels
while read -r d a dl; do
  day_adds["$d"]=$a
  day_dels["$d"]=$dl
done < <(
  git log --format="COMMIT %ad" --date=format:"%Y-%m-%d" --numstat | awk '
    /^COMMIT / { day=$2; next }
    /Migrations/ { next }
    NF >= 3 && $1 != "-" { adds[day]+=$1; dels[day]+=$2 }
    END { for (d in adds) print d, adds[d], dels[d] }
  '
)

N=0
TOTAL=$(echo "$DAY_COMMITS" | wc -l)

while IFS=' ' read -r day commit; do
  [ -z "$day" ] && continue
  N=$((N+1))
  git checkout --quiet "$commit"

  # Pipe through `cat` to wc, NOT `xargs -0 wc -l -c | tail -1`. When the file
  # list exceeds the OS command-line limit (~8 KB on Windows), xargs splits
  # into multiple wc invocations and each emits its own "total" line; `tail
  # -1` then only sees the last batch's count. `cat` merges content into a
  # single stream so wc sees the true total.
  cs_data=$(find src -type f -name '*.cs' ! -path '*/Migrations/*' ! -path '*Tests*' -print0 2>/dev/null | xargs -0 cat 2>/dev/null | wc -l -c)
  cshtml_data=$(find src -type f -name '*.cshtml' ! -path '*/Migrations/*' ! -path '*Tests*' -print0 2>/dev/null | xargs -0 cat 2>/dev/null | wc -l -c)
  resx_data=$(find src -type f -name '*.resx' ! -path '*/Migrations/*' ! -path '*Tests*' -print0 2>/dev/null | xargs -0 cat 2>/dev/null | wc -l -c)
  js_data=$(find src -type f -name '*.js' ! -path '*/Migrations/*' ! -path '*Tests*' -print0 2>/dev/null | xargs -0 cat 2>/dev/null | wc -l -c)

  cs_lines=$(echo "$cs_data" | awk '{print $1+0}')
  cshtml_lines=$(echo "$cshtml_data" | awk '{print $1+0}')
  resx_lines=$(echo "$resx_data" | awk '{print $1+0}')
  js_lines=$(echo "$js_data" | awk '{print $1+0}')

  app_lines=$((cs_lines + cshtml_lines + resx_lines + js_lines))
  app_bytes=$(( $(echo "$cs_data" | awk '{print $2+0}') + $(echo "$cshtml_data" | awk '{print $2+0}') + $(echo "$resx_data" | awk '{print $2+0}') + $(echo "$js_data" | awk '{print $2+0}') ))

  test_data=$(find . -type f -name '*.cs' -path '*Tests*' ! -path '*/Migrations/*' -print0 2>/dev/null | xargs -0 cat 2>/dev/null | wc -l -c)
  test_lines=$(echo "$test_data" | awk '{print $1+0}')
  test_bytes=$(echo "$test_data" | awk '{print $2+0}')

  total_lines=$((app_lines + test_lines))
  app_kb=$(to_kb "$app_bytes")
  test_kb=$(to_kb "$test_bytes")

  app_files=$(( $(find src -type f \( -name '*.cs' -o -name '*.cshtml' -o -name '*.resx' -o -name '*.js' \) ! -path '*/Migrations/*' ! -path '*Tests*' 2>/dev/null | wc -l) ))
  test_files=$(find . -type f -name '*.cs' -path '*Tests*' ! -path '*/Migrations/*' 2>/dev/null | wc -l)
  files=$((app_files + test_files))

  classes=$(grep -rE '^\s*(public|internal)\s+(sealed |abstract |static |partial )*(class|record) ' --include='*.cs' src/ 2>/dev/null | grep -v '/Migrations/' | grep -v 'Tests' | wc -l || echo 0)
  interfaces=$(grep -rE '^\s*public\s+interface\s' --include='*.cs' src/ 2>/dev/null | grep -v '/Migrations/' | grep -v 'Tests' | wc -l || echo 0)
  controllers=$(find src -name '*Controller.cs' ! -path '*/Migrations/*' 2>/dev/null | wc -l)
  views=$(find src -name '*.cshtml' 2>/dev/null | wc -l)
  entities=$(find src -path '*/Entities/*.cs' 2>/dev/null | wc -l)
  resx_keys=$(grep -c '<data ' src/Humans.Web/Resources/SharedResource.resx 2>/dev/null || echo 0)

  commits="${cum_commits[$day]:-0}"
  add="${day_adds[$day]:-0}"
  del="${day_dels[$day]:-0}"

  printf '| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |\n' \
    "$day" \
    "$(fmt "$app_lines")" "$(fmt "$test_lines")" "$(fmt "$total_lines")" \
    "$(fmt "$cs_lines")" "$(fmt "$cshtml_lines")" "$(fmt "$resx_lines")" "$(fmt "$js_lines")" \
    "$(fmt "$app_kb")" "$(fmt "$test_kb")" \
    "$(fmt "$files")" "$(fmt "$classes")" "$(fmt "$interfaces")" \
    "$(fmt "$controllers")" "$(fmt "$views")" "$(fmt "$entities")" "$(fmt "$resx_keys")" \
    "$(fmt "$commits")" "$(fmt "$add")" "$(fmt "$del")" >> "$NEW_ROWS"

  echo "[$N/$TOTAL] $day $commit"
done <<< "$DAY_COMMITS"

# Restore branch BEFORE writing back to the doc, so the file we modify is the
# one on the original ref (not a historical commit's version of it).
git checkout --quiet "$ORIG_REF"

# Stitch: preamble + existing rows + new rows.
{
  cat "$PREAMBLE"
  cat "$EXISTING_ROWS"
  cat "$NEW_ROWS"
} > "$DOC.tmp"
mv "$DOC.tmp" "$DOC"

ROWS=$(grep -cE '^\| [0-9]{4}-[0-9]{2}-[0-9]{2} ' "$DOC" || echo 0)
echo "Done. Appended $N rows. Table now has $ROWS data rows."
