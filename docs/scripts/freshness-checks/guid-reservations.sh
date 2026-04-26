#!/bin/bash
# Freshness check: guid-reservations
#
# Two-way check:
#   1. Every leading-4-hex GUID block found in
#      src/Humans.Domain/Constants/ + src/Humans.Infrastructure/Data/Configurations/
#      must appear as a row in the "Current Reservations" table.
#   2. Every block listed in the doc must either correspond to a source block
#      OR be the documented "0000" sentinel (which is intentionally
#      source-less — explained in the row text).

set -euo pipefail

DOC="docs/guid-reservations.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [guid-reservations]: $DOC does not exist"
  exit 1
fi

# Source blocks: every distinct leading-4-hex segment of `00000000-0000-0000-XXXX-...`
SRC_BLOCKS=$(grep -rEho '"00000000-0000-0000-([0-9a-fA-F]{4})-' \
    src/Humans.Domain/Constants/ \
    src/Humans.Infrastructure/Data/Configurations/ 2>/dev/null \
  | sed -E 's/.*-([0-9a-fA-F]{4})-$/\1/' \
  | sort -u)

# Doc blocks: pull from the first column of the Current Reservations table.
# Format: `| `0001` | ... |` — extract the backticked 4-hex token.
DOC_BLOCKS=$(awk '/^## Current Reservations/,/^## [^C]/' "$DOC" \
  | grep -oE '`[0-9a-fA-F]{4}`' \
  | tr -d '`' \
  | sort -u)

# The 0000 sentinel is allowed in the doc without a corresponding source
# block — it's documented as "Migration-generated usage only".
ALLOWED_DOC_ONLY="0000"

FAIL=false

# 1. Every src block must be in doc.
MISSING_DOC=""
for B in $SRC_BLOCKS; do
  if ! echo "$DOC_BLOCKS" | grep -qx "$B"; then
    MISSING_DOC="${MISSING_DOC}${B}
"
  fi
done
if [ -n "$MISSING_DOC" ]; then
  echo "[guid-reservations] source blocks not in doc:"
  echo "$MISSING_DOC" | sed 's/^/  - /' | grep -v '^  - $' || true
  FAIL=true
fi

# 2. Every doc block must be in src or in the allowlist.
MISSING_SRC=""
for B in $DOC_BLOCKS; do
  if echo "$ALLOWED_DOC_ONLY" | grep -qw "$B"; then
    continue
  fi
  if ! echo "$SRC_BLOCKS" | grep -qx "$B"; then
    MISSING_SRC="${MISSING_SRC}${B}
"
  fi
done
if [ -n "$MISSING_SRC" ]; then
  echo "[guid-reservations] doc blocks with no source:"
  echo "$MISSING_SRC" | sed 's/^/  - /' | grep -v '^  - $' || true
  FAIL=true
fi

if [ "$FAIL" = "true" ]; then
  echo "FAIL [guid-reservations]"
  exit 1
fi

SRC_COUNT=$(echo "$SRC_BLOCKS" | grep -cv '^$' || true)
DOC_COUNT=$(echo "$DOC_BLOCKS" | grep -cv '^$' || true)
echo "PASS [guid-reservations]: src=$SRC_COUNT blocks, doc=$DOC_COUNT rows (incl. allowed sentinel '0000')"
exit 0
