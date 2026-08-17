#!/bin/bash
# Freshness check: service-data-access-map
#
# Verifies that every service class under src/Sections/*/Services/ and the
# cross-section orchestrators in src/Humans.Web/Services/ appear as a
# `### <ServiceName>` heading (or row) in
# docs/architecture/service-data-access-map.md.
#
# Humans.Application, which this check used to walk, was emptied and
# dereferenced at G5 lane 5c (nobodies-collective/Humans#866).
#
# The service population comes from lib-service-classes.sh, which derives it
# from class declarations that implement an IApplicationService-derived
# interface. It used to come from `.cs` basenames, which counted interfaces,
# DTOs and options as required headings and collapsed the five sections whose
# service class is named `Service`.

set -euo pipefail

DOC="docs/architecture/service-data-access-map.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [service-data-access-map]: $DOC does not exist"
  exit 1
fi

# shellcheck source=lib-service-classes.sh
. "$(cd "$(dirname "$0")" && pwd)/lib-service-classes.sh"

# Skip non-service helpers (no repository / DI surface, intentionally absent).
export SKIP_NAMES="GuideFilter GuideRolePrivilegeMap"
# Stub* are the development-environment fallbacks registered when Google/vendor
# credentials are absent; the map documents the production implementation.
# Caching decorators ARE documented here (unlike the dependency graph), so they
# are not skipped.
export SKIP_PREFIXES="Stub"

MISSING=""
MISS_COUNT=0
TOTAL=0
while IFS='|' read -r PRIMARY NAMES FILE; do
  [ -z "$PRIMARY" ] && continue
  TOTAL=$((TOTAL + 1))
  # Require an H3 under one of the names the doc may key this service by, to
  # enforce a per-service narrative.
  FOUND=false
  for NAME in $(echo "$NAMES" | tr ',' ' '); do
    if grep -qE "^### ${NAME}( |\$)" "$DOC"; then
      FOUND=true
      break
    fi
  done
  if ! $FOUND; then
    MISSING="${MISSING}${PRIMARY} (${FILE})
"
    MISS_COUNT=$((MISS_COUNT + 1))
  fi
done <<< "$(service_classes)"

if [ "$MISS_COUNT" -gt 0 ]; then
  echo "[service-data-access-map] missing $MISS_COUNT/$TOTAL service H3 headings:"
  echo "$MISSING" | sed 's/^/  - /' | grep -v '^  - $' || true
  echo "FAIL [service-data-access-map]"
  exit 1
fi

echo "PASS [service-data-access-map]: all $TOTAL services have ### headings"
exit 0
