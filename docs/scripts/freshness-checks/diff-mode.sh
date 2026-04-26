#!/bin/bash
# Test the freshness sweep's diff-mode logic in isolation.
#
# Exercises Phase 3 (catalog/marker discovery) and Phase 4 (trigger
# glob matching against a synthetic diff). Does NOT spin up a real
# worktree or invoke subagents — that's an end-to-end integration
# test for a different layer.
#
# Usage: bash docs/scripts/freshness-checks/diff-mode.sh
#
# Asserts:
#   1. freshness-catalog.yml parses cleanly (yaml + structural fields).
#   2. Every mechanical entry's trigger globs match at least one file.
#   3. Editorial walks find the expected docs (sections / features / guide).
#   4. Every marked editorial doc has well-formed marker syntax.
#   5. A synthetic diff containing src/Humans.Web/Controllers/TeamController.cs
#      marks at least one mechanical entry dirty AND at least one
#      editorial doc dirty (the Team-related docs).
#   6. A synthetic diff containing only docs/* changes marks ZERO
#      entries dirty (docs aren't src/, so no triggers should fire).

set -euo pipefail

CATALOG="docs/architecture/freshness-catalog.yml"
PASS=0
FAIL=0

if [ ! -f "$CATALOG" ]; then
  echo "FAIL: $CATALOG not found. Run from repo root."
  exit 1
fi

# ─── Test 1: Catalog parses (structural smoke) ────────────────────────
N_MECHANICAL=$(grep -cE '^\s+- id:\s+' "$CATALOG" || echo 0)
N_TREES=$(awk '/^editorial_trees:/,/^[a-z]/' "$CATALOG" | grep -cE '^\s+- ' || echo 0)
N_IGNORE=$(awk '/^ignore:/,0' "$CATALOG" | grep -cE '^\s+- ' || echo 0)

if [ "$N_MECHANICAL" -lt 5 ]; then
  echo "FAIL [test 1]: only $N_MECHANICAL mechanical entries (expected >= 5)"
  FAIL=$((FAIL+1))
else
  echo "PASS [test 1]: catalog has $N_MECHANICAL mechanical, $N_TREES editorial trees, $N_IGNORE ignore patterns"
  PASS=$((PASS+1))
fi

# ─── Test 2: Mechanical entry trigger globs match real files ──────────
shopt -s globstar nullglob
total=0; bad=0
in_mechanical=false
in_triggers=false
while IFS= read -r line; do
  if [[ "$line" =~ ^mechanical: ]]; then in_mechanical=true; continue; fi
  if [[ "$line" =~ ^[a-z_]+: ]] && [[ ! "$line" =~ ^\s ]]; then in_mechanical=false; continue; fi
  if ! $in_mechanical; then continue; fi
  if [[ "$line" =~ ^[[:space:]]+triggers: ]]; then in_triggers=true; continue; fi
  if [[ "$line" =~ ^[[:space:]]+[a-z_]+: ]] && ! [[ "$line" =~ ^[[:space:]]+- ]]; then in_triggers=false; continue; fi
  if $in_triggers && [[ "$line" =~ ^[[:space:]]+-[[:space:]]+\"(.+)\"[[:space:]]*$ ]]; then
    glob="${BASH_REMATCH[1]}"
    total=$((total+1))
    matches=( $glob )
    if [ ${#matches[@]} -eq 0 ]; then
      echo "  [test 2]: ZERO MATCH glob: $glob"
      bad=$((bad+1))
    fi
  fi
done < "$CATALOG"

if [ "$bad" -eq 0 ]; then
  echo "PASS [test 2]: all $total mechanical-entry trigger globs match real files"
  PASS=$((PASS+1))
else
  echo "FAIL [test 2]: $bad of $total mechanical-entry trigger globs are stale"
  FAIL=$((FAIL+1))
fi

# ─── Test 3: Editorial walks find expected counts ─────────────────────
SEC=$(find docs/sections -name '*.md' -not -name 'SECTION-TEMPLATE.md' | wc -l)
FEAT=$(find docs/features -name '*.md' | wc -l)
GUIDE=$(find docs/guide -name '*.md' -not -name 'README.md' -not -name 'GettingStarted.md' -not -name 'Glossary.md' | wc -l)
TOTAL=$((SEC + FEAT + GUIDE))

if [ "$TOTAL" -lt 50 ]; then
  echo "FAIL [test 3]: editorial walk found only $TOTAL docs (expected >= 50)"
  FAIL=$((FAIL+1))
else
  echo "PASS [test 3]: editorial walk: sections=$SEC features=$FEAT guide=$GUIDE = $TOTAL"
  PASS=$((PASS+1))
fi

# ─── Test 4: Marker syntax well-formedness on every editorial doc ─────
malformed=0
for f in $(find docs/sections docs/features docs/guide -name '*.md' \
           -not -name 'SECTION-TEMPLATE.md' -not -name 'README.md' \
           -not -name 'GettingStarted.md' -not -name 'Glossary.md' 2>/dev/null); do
  has_triggers=$(grep -c '<!-- freshness:triggers' "$f" || true)
  close_count=$(grep -cE '^-->' "$f" || true)
  if [ "$has_triggers" -gt 0 ] && [ "$close_count" -lt "$has_triggers" ]; then
    echo "  [test 4]: marker imbalance in $f (open=$has_triggers close=$close_count)"
    malformed=$((malformed+1))
  fi
done

if [ "$malformed" -eq 0 ]; then
  echo "PASS [test 4]: every marked editorial doc has matched open/close markers"
  PASS=$((PASS+1))
else
  echo "FAIL [test 4]: $malformed editorial docs have malformed markers"
  FAIL=$((FAIL+1))
fi

# ─── Test 5: Synthetic diff (TeamController.cs) marks expected dirty ──
SYNTHETIC="src/Humans.Web/Controllers/TeamController.cs"
mech_dirty=0
for entry in authorization-inventory controller-architecture-audit dependency-graph; do
  in_block=false
  in_triggers=false
  while IFS= read -r line; do
    if [[ "$line" =~ ^[[:space:]]+-[[:space:]]+id:[[:space:]]+$entry$ ]]; then in_block=true; continue; fi
    if $in_block && [[ "$line" =~ ^[[:space:]]+-[[:space:]]+id:[[:space:]] ]]; then break; fi
    if $in_block && [[ "$line" =~ ^[[:space:]]+triggers: ]]; then in_triggers=true; continue; fi
    if $in_block && $in_triggers && [[ "$line" =~ ^[[:space:]]+update: ]]; then in_triggers=false; continue; fi
    if $in_block && $in_triggers && [[ "$line" =~ ^[[:space:]]+-[[:space:]]+\"(.+)\"[[:space:]]*$ ]]; then
      glob="${BASH_REMATCH[1]}"
      matches=( $glob )
      for m in "${matches[@]}"; do
        if [ "$m" = "$SYNTHETIC" ]; then
          mech_dirty=$((mech_dirty+1))
          break 2
        fi
      done
    fi
  done < "$CATALOG"
done

ed_dirty=0
for f in docs/sections/Teams.md docs/features/06-teams.md docs/guide/Teams.md; do
  triggers=$(awk '/<!-- freshness:triggers/,/^-->/' "$f" 2>/dev/null | grep -E '^\s+src/' | sed 's/^\s*//;s/\s*$//')
  while IFS= read -r glob; do
    [ -z "$glob" ] && continue
    matches=( $glob )
    for m in "${matches[@]}"; do
      if [ "$m" = "$SYNTHETIC" ]; then
        ed_dirty=$((ed_dirty+1))
        break 2
      fi
    done
  done <<< "$triggers"
done

if [ "$mech_dirty" -ge 1 ] && [ "$ed_dirty" -ge 1 ]; then
  echo "PASS [test 5]: synthetic TeamController.cs change marks $mech_dirty mechanical + $ed_dirty editorial dirty"
  PASS=$((PASS+1))
else
  echo "FAIL [test 5]: synthetic TeamController.cs change should mark >=1 mechanical and >=1 editorial dirty (got $mech_dirty + $ed_dirty)"
  FAIL=$((FAIL+1))
fi

# ─── Test 6: docs-only diff marks ZERO entries dirty ──────────────────
DOC_ONLY="docs/freshness/last-report.md"
mech_dirty=0
in_mechanical=false
in_triggers=false
while IFS= read -r line; do
  if [[ "$line" =~ ^mechanical: ]]; then in_mechanical=true; continue; fi
  if [[ "$line" =~ ^[a-z_]+: ]] && [[ ! "$line" =~ ^\s ]]; then in_mechanical=false; continue; fi
  if ! $in_mechanical; then continue; fi
  if [[ "$line" =~ ^[[:space:]]+triggers: ]]; then in_triggers=true; continue; fi
  if [[ "$line" =~ ^[[:space:]]+[a-z_]+: ]] && ! [[ "$line" =~ ^[[:space:]]+- ]]; then in_triggers=false; continue; fi
  if $in_triggers && [[ "$line" =~ ^[[:space:]]+-[[:space:]]+\"(.+)\"[[:space:]]*$ ]]; then
    glob="${BASH_REMATCH[1]}"
    matches=( $glob )
    for m in "${matches[@]}"; do
      if [ "$m" = "$DOC_ONLY" ]; then mech_dirty=$((mech_dirty+1)); break; fi
    done
  fi
done < "$CATALOG"

if [ "$mech_dirty" -eq 0 ]; then
  echo "PASS [test 6]: docs-only diff ($DOC_ONLY) marks 0 mechanical entries dirty"
  PASS=$((PASS+1))
else
  echo "FAIL [test 6]: docs-only diff should mark 0 dirty (got $mech_dirty)"
  FAIL=$((FAIL+1))
fi

echo ""
echo "═══ Summary ═══"
echo "Passed: $PASS"
echo "Failed: $FAIL"
exit $FAIL
