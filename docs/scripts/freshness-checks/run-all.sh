#!/bin/bash
# Run every freshness check and report a pass/fail summary.
#
# Usage: bash docs/scripts/freshness-checks/run-all.sh
#
# Exits non-zero if any check failed. Each check is independent and run even
# if previous checks failed (so we report the full state, not just the first
# failure).

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

CHECKS=(
  dev-stats
  about-page-packages
  docs-readme-index
  authorization-inventory
  controller-architecture-audit
  dependency-graph
  service-data-access-map
  data-model-index
  guid-reservations
  code-analysis-suppressions
  reforge-history
)

PASSED=()
FAILED=()
TOTAL=0

for CHECK in "${CHECKS[@]}"; do
  TOTAL=$((TOTAL + 1))
  SCRIPT="$SCRIPT_DIR/${CHECK}.sh"
  if [ ! -f "$SCRIPT" ]; then
    echo "─── $CHECK ───"
    echo "FAIL [$CHECK]: script $SCRIPT does not exist"
    FAILED+=("$CHECK")
    continue
  fi
  echo "─── $CHECK ───"
  if bash "$SCRIPT"; then
    PASSED+=("$CHECK")
  else
    FAILED+=("$CHECK")
  fi
  echo ""
done

echo "═══ Summary ═══"
echo "Passed: ${#PASSED[@]}/$TOTAL"
echo "Failed: ${#FAILED[@]}/$TOTAL"
if [ "${#FAILED[@]}" -gt 0 ]; then
  echo "Failures:"
  for F in "${FAILED[@]}"; do
    echo "  - $F"
  done
  exit 1
fi
echo "All checks passed."
exit 0
