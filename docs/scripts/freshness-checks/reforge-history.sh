#!/bin/bash
# Freshness check: reforge-history
#
# Validates docs/reforge-history.csv:
#   1. Has at least one row per day for which a commit exists in the recent
#      week (allow gaps for days with no commits).
#   2. Header has the expected column count and the canonical first column
#      (`commit_date`).
#   3. No data row has empty cells, and every row has the same column count
#      as the header.

set -euo pipefail

CSV="docs/reforge-history.csv"

if [ ! -f "$CSV" ]; then
  echo "FAIL [reforge-history]: $CSV does not exist"
  exit 1
fi

HEADER=$(head -1 "$CSV")
HEADER_COLS=$(echo "$HEADER" | awk -F, '{print NF}')

if [ "$HEADER_COLS" -lt 5 ]; then
  echo "FAIL [reforge-history]: header has only $HEADER_COLS columns"
  echo "  header: $HEADER"
  exit 1
fi

FIRST_COL=$(echo "$HEADER" | awk -F, '{print $1}')
if [ "$FIRST_COL" != "commit_date" ]; then
  echo "FAIL [reforge-history]: first header column is '$FIRST_COL', expected 'commit_date'"
  exit 1
fi

# 1. Recent-week coverage. For each of the last 7 days, if main has any commit
# on that day, the CSV must have a row for that day.
MISSING_DAYS=""
MISS_DAY_COUNT=0
for OFFSET in 0 1 2 3 4 5 6; do
  # Use git log --since/--until to find a commit on that day.
  DAY=$(git log -1 --before="$(date -d "${OFFSET} days ago 23:59" '+%Y-%m-%d %H:%M' 2>/dev/null || date -v-${OFFSET}d '+%Y-%m-%d 23:59')" \
        --after="$(date -d "${OFFSET} days ago 00:00" '+%Y-%m-%d %H:%M' 2>/dev/null || date -v-${OFFSET}d '+%Y-%m-%d 00:00')" \
        --format='%ad' --date=format:'%Y-%m-%d' main 2>/dev/null || true)
  if [ -z "$DAY" ]; then
    continue  # no commit on that day — gap allowed
  fi
  if ! tail -n +2 "$CSV" | cut -d, -f1 | cut -dT -f1 | grep -qx "$DAY"; then
    MISSING_DAYS="${MISSING_DAYS}${DAY}
"
    MISS_DAY_COUNT=$((MISS_DAY_COUNT + 1))
  fi
done

# 3. Row sanity. Each non-header row: same column count as header, no empty cells.
BAD_ROWS=""
BAD_ROW_COUNT=0
LINE_NO=1
while IFS= read -r ROW; do
  LINE_NO=$((LINE_NO + 1))
  [ -z "$ROW" ] && continue
  ROW_COLS=$(echo "$ROW" | awk -F, '{print NF}')
  if [ "$ROW_COLS" -ne "$HEADER_COLS" ]; then
    BAD_ROWS="${BAD_ROWS}line ${LINE_NO}: column count $ROW_COLS != $HEADER_COLS
"
    BAD_ROW_COUNT=$((BAD_ROW_COUNT + 1))
    continue
  fi
  # Empty cell? (`,,` anywhere, or starts/ends with `,`)
  if echo "$ROW" | grep -qE '(^,|,,|,$)'; then
    BAD_ROWS="${BAD_ROWS}line ${LINE_NO}: empty cell — '$ROW'
"
    BAD_ROW_COUNT=$((BAD_ROW_COUNT + 1))
  fi
done < <(tail -n +2 "$CSV")

FAIL=false

if [ "$MISS_DAY_COUNT" -gt 0 ]; then
  echo "[reforge-history] missing rows for $MISS_DAY_COUNT recent days that have commits:"
  echo "$MISSING_DAYS" | sed 's/^/  - /' | grep -v '^  - $' || true
  FAIL=true
fi

if [ "$BAD_ROW_COUNT" -gt 0 ]; then
  echo "[reforge-history] $BAD_ROW_COUNT malformed rows:"
  echo "$BAD_ROWS" | sed 's/^/  /' | grep -v '^  $' || true
  FAIL=true
fi

if [ "$FAIL" = "true" ]; then
  echo "FAIL [reforge-history]"
  exit 1
fi

ROWS=$(($(wc -l < "$CSV") - 1))
echo "PASS [reforge-history]: $ROWS data rows, $HEADER_COLS columns, recent-week coverage ok"
exit 0
