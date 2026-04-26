#!/bin/bash
# Freshness check: service-data-access-map
#
# Verifies that every service class under src/Humans.Application/Services/
# appears as a `### <ServiceName>` heading (or row) in
# docs/architecture/service-data-access-map.md.

set -euo pipefail

DOC="docs/architecture/service-data-access-map.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [service-data-access-map]: $DOC does not exist"
  exit 1
fi

# Skip non-service helpers (no repository / DI surface, intentionally absent).
SKIP_NAMES="GuideFilter GuideRolePrivilegeMap"

ALL_FILES=$(find src/Humans.Application/Services -name "*.cs" -type f \
  | sed 's|.*/||;s|\.cs$||' | sort -u)

MISSING=""
MISS_COUNT=0
TOTAL=0
for NAME in $ALL_FILES; do
  if echo "$SKIP_NAMES" | grep -qw "$NAME"; then
    continue
  fi
  TOTAL=$((TOTAL + 1))
  # Match either `### <Name>` (heading) or any other mention of the bare name
  # as a token boundary. We require an H3 to enforce per-service narrative.
  if ! grep -qE "^### ${NAME}( |\$)" "$DOC"; then
    MISSING="${MISSING}${NAME}
"
    MISS_COUNT=$((MISS_COUNT + 1))
  fi
done

if [ "$MISS_COUNT" -gt 0 ]; then
  echo "[service-data-access-map] missing $MISS_COUNT/$TOTAL service H3 headings:"
  echo "$MISSING" | sed 's/^/  - /' | grep -v '^  - $' || true
  echo "FAIL [service-data-access-map]"
  exit 1
fi

echo "PASS [service-data-access-map]: all $TOTAL services have ### headings"
exit 0
