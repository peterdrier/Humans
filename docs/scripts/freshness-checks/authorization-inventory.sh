#!/bin/bash
# Freshness check: authorization-inventory
#
# Verifies docs/authorization-inventory.md, together with every per-section
# src/Sections/*/Docs/authorization.md (split from the global file
# 2026-08-18), covers:
# 1. Every controller file from src/Humans.Web/Controllers/ and
#    src/Sections/*/Controllers/ — each mentioned in the map that OWNS it.
# 2. Every AuthorizationHandler<...> subclass from src/Humans.Web/Authorization
#    and each section's own src/Sections/*/Authorization/, likewise in its
#    owning map.
# 3. Has at least as many "| ... | Action |" rows as there are [Authorize]
#    occurrences on action-level attributes in controllers (loose floor; the
#    doc may consolidate multi-action attribute groups into one row). This one
#    stays an aggregate across all maps — it is a volume floor, not ownership.
#
# Ownership comes from each type's own path: a section controller or handler
# must appear in its src/Sections/<Project>/Docs/authorization.md, and only
# Humans.Web types belong to the global inventory. Searching every document
# together let a mention in the global tables or a neighbouring section stand in
# for an owning map that had lost its rows entirely.

set -euo pipefail

DOC="docs/authorization-inventory.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [authorization-inventory]: $DOC does not exist"
  exit 1
fi

# The global inventory plus every per-section authorization doc — the row-count
# floor (check 3) is an aggregate over all of them.
DOCS=("$DOC")
while IFS= read -r F; do DOCS+=("$F"); done < <(ls src/Sections/*/Docs/authorization.md 2>/dev/null)

# The map that owns a type, derived from its own source path. A section that
# owns a controller or handler but has no map yet fails here naming the file it
# must create — the global inventory is not a fallback.
owning_map() {
  local file=$1 proj
  proj=$(echo "$file" | sed -nE 's|^src/Sections/([^/]+)/.*|\1|p')
  if [ -n "$proj" ]; then
    echo "src/Sections/$proj/Docs/authorization.md"
  else
    echo "$DOC"
  fi
}

# 1. Controller files (excluding abstract bases), across Humans.Web and every section.
CONTROLLERS=$(find src/Humans.Web/Controllers src/Sections/*/Controllers -name "*Controller.cs" -type f \
  -not -name "Humans*ControllerBase.cs" \
  2>/dev/null | sort -u)

MISSING_CTRL=""
MISS_CTRL_COUNT=0
TOTAL_CTRL=0
for PATH_CTRL in $CONTROLLERS; do
  TOTAL_CTRL=$((TOTAL_CTRL + 1))
  CTRL=$(basename "$PATH_CTRL" .cs)
  MAP=$(owning_map "$PATH_CTRL")
  if ! grep -qF "$CTRL" "$MAP" 2>/dev/null; then
    MISSING_CTRL="${MISSING_CTRL}${CTRL} -> expected in ${MAP}
"
    MISS_CTRL_COUNT=$((MISS_CTRL_COUNT + 1))
  fi
done

# 2. AuthorizationHandler subclasses.
HANDLERS=$(grep -rlE 'AuthorizationHandler<' \
    src/Humans.Web/Authorization/Requirements/ \
    src/Sections/*/Authorization/ 2>/dev/null | sort -u)

MISSING_HND=""
MISS_HND_COUNT=0
TOTAL_HND=0
for PATH_HND in $HANDLERS; do
  TOTAL_HND=$((TOTAL_HND + 1))
  HND=$(basename "$PATH_HND" .cs)
  MAP=$(owning_map "$PATH_HND")
  if ! grep -qF "$HND" "$MAP" 2>/dev/null; then
    MISSING_HND="${MISSING_HND}${HND} -> expected in ${MAP}
"
    MISS_HND_COUNT=$((MISS_HND_COUNT + 1))
  fi
done

# 3. Action-row floor: number of [Authorize ...] occurrences on action-level
# attributes. We count ALL [Authorize occurrences (class + action) in src,
# then count "| ... | Action |" + "| ... | Class |" rows across all docs as
# the combined floor. The docs routinely consolidate groups (e.g.
# `Foo / Bar / Baz`) so we use a 60% floor rather than strict >=.
SRC_AUTHZ=$(grep -hE '\[Authorize' src/Humans.Web/Controllers/*.cs src/Sections/*/Controllers/*.cs | wc -l)
DOC_ROWS=$(grep -chE '^\| .* \| (Action|Class) \|' "${DOCS[@]}" | awk -F: '{sum+=$NF} END {print sum+0}')
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
