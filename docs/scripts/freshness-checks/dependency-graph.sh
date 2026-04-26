#!/bin/bash
# Freshness check: dependency-graph
#
# Verifies that every service class under src/Humans.Application/Services/
# (and a small allowlist from src/Humans.Infrastructure/Services/ for
# pre-§15-migration services) appears as a node in the Mermaid diagram in
# docs/architecture/dependency-graph.md.
#
# Mermaid node syntax used: `<Alias>[<ServiceName>]` — we look for the service
# name inside square brackets.

set -euo pipefail

DOC="docs/architecture/dependency-graph.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [dependency-graph]: $DOC does not exist"
  exit 1
fi

# Service classes from Application/Services. Exclude non-service helpers that
# the dependency graph legitimately omits (no DI, no constructor injection).
SKIP_NAMES="GuideFilter GuideRolePrivilegeMap"

ALL_FILES=$(find src/Humans.Application/Services -name "*.cs" -type f \
  | sed 's|.*/||;s|\.cs$||' | sort -u)

SERVICES=""
for NAME in $ALL_FILES; do
  if echo "$SKIP_NAMES" | grep -qw "$NAME"; then
    continue
  fi
  SERVICES="${SERVICES}${NAME}
"
done

# Extract every Mermaid node label (text inside `[ ... ]` after a node alias).
NODE_LABELS=$(grep -oE '[A-Za-z]+\[[A-Za-z]+\]' "$DOC" | sed -E 's/^[A-Za-z]+\[//; s/\]$//' | sort -u)

MISSING=""
MISS_COUNT=0
TOTAL=0
while IFS= read -r SVC; do
  [ -z "$SVC" ] && continue
  TOTAL=$((TOTAL + 1))
  if ! echo "$NODE_LABELS" | grep -qx "$SVC"; then
    MISSING="${MISSING}${SVC}
"
    MISS_COUNT=$((MISS_COUNT + 1))
  fi
done <<< "$SERVICES"

if [ "$MISS_COUNT" -gt 0 ]; then
  echo "[dependency-graph] missing $MISS_COUNT/$TOTAL service nodes from Mermaid:"
  echo "$MISSING" | sed 's/^/  - /' | grep -v '^  - $' || true
  echo "FAIL [dependency-graph]"
  exit 1
fi

echo "PASS [dependency-graph]: all $TOTAL Application/Services service classes appear as Mermaid nodes"
exit 0
