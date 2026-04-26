#!/bin/bash
# Freshness check: authorization-inventory
#
# Verifies docs/authorization-inventory.md covers:
# 1. Every controller file from src/Humans.Web/Controllers/ (the doc must
#    mention the filename — e.g. `AdminController` — at least once).
# 2. Every AuthorizationHandler<...> subclass from src/Humans.Web/Authorization
#    and src/Humans.Application/Authorization.
# 3. Has at least as many "| ... | Action |" rows as there are [Authorize]
#    occurrences on action-level attributes in controllers (loose floor; the
#    doc may consolidate multi-action attribute groups into one row).

set -euo pipefail

DOC="docs/authorization-inventory.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [authorization-inventory]: $DOC does not exist"
  exit 1
fi

# 1. Controller files (excluding abstract bases).
CONTROLLERS=$(find src/Humans.Web/Controllers -name "*Controller.cs" -type f \
  -not -name "Humans*ControllerBase.cs" \
  | sed 's|.*/||;s|\.cs$||' | sort -u)

MISSING_CTRL=""
MISS_CTRL_COUNT=0
TOTAL_CTRL=0
for CTRL in $CONTROLLERS; do
  TOTAL_CTRL=$((TOTAL_CTRL + 1))
  if ! grep -qF "$CTRL" "$DOC"; then
    MISSING_CTRL="${MISSING_CTRL}${CTRL}
"
    MISS_CTRL_COUNT=$((MISS_CTRL_COUNT + 1))
  fi
done

# 2. AuthorizationHandler subclasses.
HANDLERS=$(grep -lE 'AuthorizationHandler<' \
    src/Humans.Web/Authorization/Requirements/*.cs \
    src/Humans.Application/Authorization/*.cs 2>/dev/null \
  | sed 's|.*/||;s|\.cs$||' | sort -u)

MISSING_HND=""
MISS_HND_COUNT=0
TOTAL_HND=0
for HND in $HANDLERS; do
  TOTAL_HND=$((TOTAL_HND + 1))
  if ! grep -qF "$HND" "$DOC"; then
    MISSING_HND="${MISSING_HND}${HND}
"
    MISS_HND_COUNT=$((MISS_HND_COUNT + 1))
  fi
done

# 3. Action-row floor: number of [Authorize ...] occurrences on action-level
# attributes. We count ALL [Authorize occurrences (class + action) in src,
# then count "| ... | Action |" + "| ... | Class |" rows in the doc as the
# combined floor. The doc routinely consolidates groups (e.g.
# `Foo / Bar / Baz`) so we use a 60% floor rather than strict >=.
SRC_AUTHZ=$(grep -hE '\[Authorize' src/Humans.Web/Controllers/*.cs | wc -l)
DOC_ROWS=$(grep -cE '^\| .* \| (Action|Class) \|' "$DOC" || true)
FLOOR=$(( SRC_AUTHZ * 60 / 100 ))

FAIL=false

if [ "$MISS_CTRL_COUNT" -gt 0 ]; then
  echo "[authorization-inventory] missing $MISS_CTRL_COUNT/$TOTAL_CTRL controllers:"
  echo "$MISSING_CTRL" | sed 's/^/  - /' | grep -v '^  - $' || true
  FAIL=true
fi

if [ "$MISS_HND_COUNT" -gt 0 ]; then
  echo "[authorization-inventory] missing $MISS_HND_COUNT/$TOTAL_HND handlers:"
  echo "$MISSING_HND" | sed 's/^/  - /' | grep -v '^  - $' || true
  FAIL=true
fi

if [ "$DOC_ROWS" -lt "$FLOOR" ]; then
  echo "[authorization-inventory] action/class row count too low: doc=$DOC_ROWS source [Authorize]=$SRC_AUTHZ floor=$FLOOR"
  FAIL=true
fi

if [ "$FAIL" = "true" ]; then
  echo "FAIL [authorization-inventory]"
  exit 1
fi

echo "PASS [authorization-inventory]: all $TOTAL_CTRL controllers + $TOTAL_HND handlers referenced; $DOC_ROWS rows >= $FLOOR (60% of $SRC_AUTHZ src [Authorize])"
exit 0
