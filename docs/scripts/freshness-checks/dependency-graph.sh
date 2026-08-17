#!/bin/bash
# Freshness check: dependency-graph
#
# Verifies that every service class under src/Sections/*/Services/ and the
# cross-section orchestrators in src/Humans.Web/Services/ appears as a node in
# the Mermaid diagram in docs/architecture/dependency-graph.md.
#
# Humans.Application and Humans.Infrastructure, which this check used to walk,
# were emptied and deleted across G5 (nobodies-collective/Humans#866).
#
# The service population comes from lib-service-classes.sh, which derives it
# from class declarations that implement an IApplicationService-derived
# interface. It used to come from `.cs` basenames, which counted interfaces,
# DTOs and options as required nodes and collapsed the five sections whose
# service class is named `Service`.
#
# Mermaid node syntax used: `<Alias>[<ServiceName>]` — we look for the service
# name inside square brackets.

set -euo pipefail

DOC="docs/architecture/dependency-graph.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [dependency-graph]: $DOC does not exist"
  exit 1
fi

# shellcheck source=lib-service-classes.sh
. "$(cd "$(dirname "$0")" && pwd)/lib-service-classes.sh"

# Non-service helpers the dependency graph legitimately omits (no DI, no
# constructor injection).
export SKIP_NAMES="GuideFilter GuideRolePrivilegeMap"
# Stub*  — the development-environment fallbacks registered when Google/vendor
#          credentials are absent; the graph plots the production implementation.
# Caching* — decorators delegate to the inner service and are collapsed onto its
#          node (see "How to read" in the doc, and design-rules §15).
export SKIP_PREFIXES="Stub Caching"

# Extract every Mermaid node label (text inside `[ ... ]` after a node alias).
NODE_LABELS=$(grep -oE '[A-Za-z]+\[[A-Za-z]+\]' "$DOC" | sed -E 's/^[A-Za-z]+\[//; s/\]$//' | sort -u)

MISSING=""
MISS_COUNT=0
TOTAL=0
while IFS='|' read -r PRIMARY NAMES FILE; do
  [ -z "$PRIMARY" ] && continue
  TOTAL=$((TOTAL + 1))
  FOUND=false
  for NAME in $(echo "$NAMES" | tr ',' ' '); do
    if echo "$NODE_LABELS" | grep -qx "$NAME"; then
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
  echo "[dependency-graph] missing $MISS_COUNT/$TOTAL service nodes from Mermaid:"
  echo "$MISSING" | sed 's/^/  - /' | grep -v '^  - $' || true
  echo "FAIL [dependency-graph]"
  exit 1
fi

echo "PASS [dependency-graph]: all $TOTAL section/Web service classes appear as Mermaid nodes"
exit 0
