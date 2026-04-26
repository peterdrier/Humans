#!/bin/bash
# Freshness check: data-model-index
#
# Verifies that every Domain entity (`.cs` under src/Humans.Domain/Entities/`)
# that has a corresponding configuration under
# src/Humans.Infrastructure/Data/Configurations/ appears in the entity-index
# table inside the `freshness:auto id="entity-index"` block of
# docs/architecture/data-model.md.
#
# The doc table consolidates related entities into one row using ` / ` as a
# delimiter (e.g. `Camp / CampSeason / CampLead`). We split on `/` when reading
# the table so each name is checked independently.

set -euo pipefail

DOC="docs/architecture/data-model.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [data-model-index]: $DOC does not exist"
  exit 1
fi

# All entity class names.
ENTITIES=$(find src/Humans.Domain/Entities -name "*.cs" -type f \
  | sed 's|.*/||;s|\.cs$||' | sort -u)

# All configuration entity stems — strip the trailing `Configuration` suffix.
CONFIGS=$(find src/Humans.Infrastructure/Data/Configurations -name "*Configuration.cs" -type f \
  | sed 's|.*/||;s|Configuration\.cs$||' | sort -u)

# The set we expect to see indexed: entities that have a configuration file.
EXPECTED=$(comm -12 <(echo "$ENTITIES") <(echo "$CONFIGS"))
EXPECTED_COUNT=$(echo "$EXPECTED" | grep -cv '^$' || true)

# Extract the entity-index block.
BLOCK=$(awk '/freshness:auto id="entity-index"/,/\/freshness:auto/' "$DOC")
if [ -z "$BLOCK" ]; then
  echo "FAIL [data-model-index]: entity-index block not found in $DOC"
  exit 1
fi

# Pull every entity name from table rows. Cell 1 is the entity (or `A / B / C`
# group). Split on `/` to yield individual names.
INDEXED=$(echo "$BLOCK" \
  | grep -E '^\| [A-Z]' \
  | awk -F'|' '{print $2}' \
  | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//' \
  | tr '/' '\n' \
  | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//' \
  | grep -v '^Entity$' \
  | sort -u)

MISSING=""
MISS_COUNT=0
while IFS= read -r ENT; do
  [ -z "$ENT" ] && continue
  if ! echo "$INDEXED" | grep -qx "$ENT"; then
    MISSING="${MISSING}${ENT}
"
    MISS_COUNT=$((MISS_COUNT + 1))
  fi
done <<< "$EXPECTED"

if [ "$MISS_COUNT" -gt 0 ]; then
  echo "[data-model-index] missing $MISS_COUNT/$EXPECTED_COUNT entities from entity-index block:"
  echo "$MISSING" | sed 's/^/  - /' | grep -v '^  - $' || true
  echo "FAIL [data-model-index]"
  exit 1
fi

echo "PASS [data-model-index]: all $EXPECTED_COUNT entities (with EF configurations) appear in entity-index block"
exit 0
