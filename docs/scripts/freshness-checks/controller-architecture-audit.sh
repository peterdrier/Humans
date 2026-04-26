#!/bin/bash
# Freshness check: controller-architecture-audit
#
# Verifies docs/controller-architecture-audit.md has:
# 1. A `## <Controller>` heading for every *Controller.cs file under
#    src/Humans.Web/Controllers/, excluding the abstract bases.
# 2. Roughly one row per public action across all controllers — we use a
#    loose 80% floor so consolidated rows (e.g.
#    `CheckEmailRenames / EmailRenames / FixEmailRename`) don't fail us.

set -euo pipefail

DOC="docs/controller-architecture-audit.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [controller-architecture-audit]: $DOC does not exist"
  exit 1
fi

# 1. Concrete controllers.
CONTROLLERS=$(find src/Humans.Web/Controllers -name "*Controller.cs" -type f \
  -not -name "Humans*ControllerBase.cs" \
  | sed 's|.*/||;s|\.cs$||' | sort -u)

MISSING=""
MISS_COUNT=0
TOTAL=0
for CTRL in $CONTROLLERS; do
  TOTAL=$((TOTAL + 1))
  if ! grep -qE "^## ${CTRL}\$" "$DOC"; then
    MISSING="${MISSING}${CTRL}
"
    MISS_COUNT=$((MISS_COUNT + 1))
  fi
done

# 2. Public action floor. Match `public ... ActionResult` or
# `public ... Task<ActionResult>` (the standard MVC return types). This is
# not a perfect parser but a useful floor.
SRC_ACTIONS=0
for FILE in $(find src/Humans.Web/Controllers -name "*Controller.cs" -type f -not -name "Humans*ControllerBase.cs"); do
  COUNT=$(grep -cE '^\s*public\s+(async\s+)?(Task<)?(I?ActionResult|IResult|FileResult|JsonResult|RedirectResult|ContentResult|ViewResult|PartialViewResult|EmptyResult|NoContentResult)' "$FILE" || true)
  SRC_ACTIONS=$((SRC_ACTIONS + COUNT))
done

# Count action rows in doc — lines that look like a markdown table data row
# under a controller heading. We approximate as lines that start with `| `
# and contain ` | ` followed by a verb (GET/POST/PUT/DELETE) OR the literal
# `| OK |` / `| Suggestion` ending.
DOC_ROWS=$(grep -cE '^\| [A-Za-z]+ \| /' "$DOC" || true)
FLOOR=$(( SRC_ACTIONS * 80 / 100 ))

FAIL=false

if [ "$MISS_COUNT" -gt 0 ]; then
  echo "[controller-architecture-audit] missing $MISS_COUNT/$TOTAL controller sections:"
  echo "$MISSING" | sed 's/^/  - /' | grep -v '^  - $' || true
  FAIL=true
fi

if [ "$DOC_ROWS" -lt "$FLOOR" ]; then
  echo "[controller-architecture-audit] action row count too low: doc=$DOC_ROWS src=$SRC_ACTIONS floor=$FLOOR"
  FAIL=true
fi

if [ "$FAIL" = "true" ]; then
  echo "FAIL [controller-architecture-audit]"
  exit 1
fi

echo "PASS [controller-architecture-audit]: all $TOTAL controllers headed; $DOC_ROWS action rows >= $FLOOR (80% of $SRC_ACTIONS src actions)"
exit 0
