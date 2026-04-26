#!/bin/bash
# Freshness check: dev-stats
#
# Verifies that docs/development-stats.md has a Codebase Growth row for the
# latest commit date on main, and that every column in the most recent row is
# numeric (no empty cells, no non-numeric values).
#
# Source: git log -1 --format=%ad --date=format:%Y-%m-%d main
# Doc:    docs/development-stats.md (last data row in Codebase Growth table)

set -euo pipefail

DOC="docs/development-stats.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [dev-stats]: $DOC does not exist"
  exit 1
fi

# Latest commit date on main. Fall back to origin/main for shallow CI clones
# where the local `main` ref isn't fetched (PR-only fetch).
LATEST_DATE=$(git log -1 --format="%ad" --date=format:"%Y-%m-%d" main 2>/dev/null \
  || git log -1 --format="%ad" --date=format:"%Y-%m-%d" origin/main 2>/dev/null \
  || echo "")
if [ -z "$LATEST_DATE" ]; then
  echo "FAIL [dev-stats]: could not determine latest commit date on main (neither 'main' nor 'origin/main' resolves)"
  exit 1
fi

# Last data row of the doc (last line that starts with `| 2`).
LAST_ROW=$(grep -E '^\| [0-9]{4}-[0-9]{2}-[0-9]{2} \|' "$DOC" | tail -1 || true)
if [ -z "$LAST_ROW" ]; then
  echo "FAIL [dev-stats]: no date rows found in Codebase Growth table"
  exit 1
fi

LAST_ROW_DATE=$(echo "$LAST_ROW" | sed -E 's/^\| ([0-9]{4}-[0-9]{2}-[0-9]{2}) \|.*/\1/')

if [ "$LAST_ROW_DATE" != "$LATEST_DATE" ]; then
  echo "FAIL [dev-stats]: latest commit date is $LATEST_DATE but last row in $DOC is $LAST_ROW_DATE"
  echo "  hint: re-run bash docs/scripts/generate-stats.sh"
  exit 1
fi

# Verify every column in the last row is non-empty and numeric (digits, commas,
# and an optional leading `-` for the "Day -Lines" column).
# Strip leading/trailing pipes, split on `|`.
IFS='|' read -r -a COLS <<< "$(echo "$LAST_ROW" | sed -E 's/^\| //; s/ \|$//')"

EXPECTED_COLS=20  # Date + 19 numeric columns
if [ "${#COLS[@]}" -lt "$EXPECTED_COLS" ]; then
  echo "FAIL [dev-stats]: row for $LAST_ROW_DATE has ${#COLS[@]} columns, expected $EXPECTED_COLS"
  echo "  row: $LAST_ROW"
  exit 1
fi

# Skip column 0 (date), check the rest are numeric.
INDEX=0
for COL in "${COLS[@]}"; do
  INDEX=$((INDEX + 1))
  if [ "$INDEX" -eq 1 ]; then
    continue  # date column
  fi
  TRIMMED=$(echo "$COL" | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//')
  if [ -z "$TRIMMED" ]; then
    echo "FAIL [dev-stats]: column $INDEX in row $LAST_ROW_DATE is empty"
    echo "  row: $LAST_ROW"
    exit 1
  fi
  # Allow digits, commas, optional leading minus.
  if ! echo "$TRIMMED" | grep -qE '^-?[0-9][0-9,]*$'; then
    echo "FAIL [dev-stats]: column $INDEX in row $LAST_ROW_DATE is not numeric: '$TRIMMED'"
    echo "  row: $LAST_ROW"
    exit 1
  fi
done

echo "PASS [dev-stats]: latest commit date $LATEST_DATE present with all 19 numeric columns populated"
exit 0
