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
#   5. A synthetic diff containing src/Sections/Humans.Teams/Controllers/TeamController.cs
#      marks at least one mechanical entry dirty AND at least one
#      editorial doc dirty (the Team-related docs).
#   6. A synthetic diff containing only docs/* changes marks ZERO
#      entries dirty (docs aren't src/, so no triggers should fire).
#   7. Every freshness:triggers glob in every editorial doc resolves to at
#      least one real file.
#   8. trigger_is_dead (the function test 7 relies on) is proven in BOTH
#      directions with synthetic input, not just whatever today's real docs
#      happen to contain (nobodies-collective/Humans#1021).
#
# Test 7 exists because a dead trigger glob is SILENT: it makes a doc look
# *clean* rather than *unchecked*, so the doc drops out of the sweep's dirty
# list entirely and nobody notices. Five consecutive sweeps found large dead-glob
# batches; the 2026-08-13 sweep found three guide docs that had stopped firing
# months earlier, one with all 9 of its globs dead. Tests 1-6 all passed
# throughout — none of them could see it.
#
# The editorial doc set spans BOTH docs/ and src/Sections/*/Docs/. Sections that
# have gone G5 carry their invariants doc inside their own project, and that is
# now where most section docs live — a walk that only covers docs/ misses them.

set -euo pipefail

CATALOG="docs/architecture/freshness-catalog.yml"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PASS=0
FAIL=0

if [ ! -f "$CATALOG" ]; then
  echo "FAIL: $CATALOG not found. Run from repo root."
  exit 1
fi

# editorial_docs, editorial_entries_unresolved, doc_trigger_lines and
# trigger_is_dead live in lib-editorial-docs.sh, shared with
# verify-triggers.sh (nobodies-collective/Humans#1021) so this script's
# definition of "the editorial doc set" and "a dead trigger" cannot drift
# from the one the sweep itself uses to repair them.
# shellcheck source=./lib-editorial-docs.sh
source "$SCRIPT_DIR/lib-editorial-docs.sh"

# ─── Test 1: Catalog parses (structural smoke) ────────────────────────
N_MECHANICAL=$(grep -cE '^\s+- id:\s+' "$CATALOG" || echo 0)
# NB: the range must start AFTER the editorial_trees: line, because that line
# itself matches /^[a-z]/ and would close the range immediately — which is why
# this reported "0 editorial trees" while the catalog listed ten.
N_TREES=$(awk '/^editorial_trees:/{f=1;next} f&&/^[a-z_]+:/{f=0} f' "$CATALOG" | { grep -cE '^\s+- ' || true; })
N_IGNORE=$(awk '/^ignore:/{f=1;next} f' "$CATALOG" | { grep -cE '^\s+- ' || true; })

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
    # trigger_is_dead (lib-editorial-docs.sh) branches on wildcard vs literal —
    # mechanical entries mix both (e.g. "Directory.Packages.props" alongside
    # "**/*.csproj"), and a plain `matches=( $glob )` word-splits a literal to
    # itself regardless of whether the file exists, same blind spot test 7 had.
    if trigger_is_dead "$glob"; then
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
SEC=$(find docs/sections -name '*.md' -not -name 'SECTION-TEMPLATE.md' -not -name 'G5-SECTION-TEMPLATE.md' | wc -l)
FEAT=$(find docs/features -name '*.md' | wc -l)
GUIDE=$(find docs/guide -name '*.md' -not -name 'README.md' -not -name 'GettingStarted.md' -not -name 'Glossary.md' | wc -l)
INPROJ=$(find src/Sections -path '*/Docs/*.md' -not -path '*/Docs/20*.md' -not -path '*/obj/*' -not -path '*/bin/*' | wc -l)
# Catalog entries listed as single files rather than directories to walk.
SINGLES=$(awk '/^editorial_trees:/{f=1;next} f&&/^[a-z_]+:/{f=0} f' "$CATALOG" \
          | grep -E '^[[:space:]]+- ' | sed 's/^[[:space:]]*-[[:space:]]*//; s/[[:space:]]*$//' \
          | while IFS= read -r e; do if [ -f "$e" ]; then echo "$e"; fi; done | wc -l)
TOTAL=$(editorial_docs | wc -l)
UNRESOLVED=$(editorial_entries_unresolved || true)
N_UNRESOLVED=0
if [ -n "$UNRESOLVED" ]; then
  N_UNRESOLVED=$(printf '%s\n' "$UNRESOLVED" | sed '/^$/d' | wc -l)
  printf '%s\n' "$UNRESOLVED" | sed '/^$/d; s|^|  [test 3]: catalog editorial_trees entry resolves to nothing: |'
fi

if [ "$TOTAL" -lt 50 ] || [ "$INPROJ" -lt 1 ] || [ "$SINGLES" -lt 1 ] || [ "$N_UNRESOLVED" -gt 0 ]; then
  echo "FAIL [test 3]: editorial walk found $TOTAL docs ($INPROJ in-project, $SINGLES single-file, $N_UNRESOLVED dead catalog entries)"
  FAIL=$((FAIL+1))
else
  echo "PASS [test 3]: editorial walk: sections=$SEC features=$FEAT guide=$GUIDE in-project=$INPROJ single-file=$SINGLES = $TOTAL total, 0 dead catalog entries"
  PASS=$((PASS+1))
fi

# ─── Test 4: Marker syntax well-formedness on every editorial doc ─────
malformed=0
for f in $(editorial_docs); do
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
SYNTHETIC="src/Sections/Humans.Teams/Controllers/TeamController.cs"
# A synthetic probe that does not exist is a test that silently proves nothing:
# it reports 0 dirty and reads as a real failure, or worse, gets ignored. Assert
# the probe itself first — this test pointed at the pre-G5 Humans.Web path for
# several sweeps after the file moved.
if [ ! -f "$SYNTHETIC" ]; then
  echo "FAIL [test 5]: synthetic probe path $SYNTHETIC does not exist — update it to a real file"
  FAIL=$((FAIL+1))
fi
mech_dirty=0
for entry in authorization-inventory dependency-graph; do
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
for f in src/Sections/Humans.Teams/Docs/Teams.md src/Sections/Humans.Teams/Docs/features/Teams-feature.md docs/guide/Teams.md; do
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

# ─── Test 7: Every editorial trigger glob resolves to a real file ─────
# The one check that can see a dead trigger. See the header note. Uses the
# shared doc_trigger_lines/trigger_is_dead from lib-editorial-docs.sh — the
# same functions verify-triggers.sh uses to repair these, so a fix to the
# detection logic can't land in one script and not the other.
dead_globs=0
dead_docs=0
checked_docs=0
for f in $(editorial_docs); do
  # `doc_trigger_lines` already `|| true`-guards the no-marker case — see its
  # definition for why that matters under `set -o pipefail`.
  triggers=$(doc_trigger_lines "$f")
  if [ -z "$triggers" ]; then continue; fi
  checked_docs=$((checked_docs+1))
  doc_dead=0
  doc_total=0
  while IFS= read -r glob; do
    [ -z "$glob" ] && continue
    doc_total=$((doc_total+1))
    # Both branches must END on a successful command. Under `set -euo pipefail`
    # a trailing `cond && assign` returns non-zero whenever cond is false, which
    # aborts the whole script mid-test — the same way a bare trailing `if` once
    # killed everything after test 2. Use explicit if/else, never `&&`.
    if trigger_is_dead "$glob"; then
      echo "  [test 7]: DEAD trigger in $f -> $glob"
      dead_globs=$((dead_globs+1))
      doc_dead=$((doc_dead+1))
    fi
  done <<< "$triggers"
  if [ "$doc_dead" -gt 0 ]; then
    dead_docs=$((dead_docs+1))
    if [ "$doc_dead" -eq "$doc_total" ]; then
      echo "  [test 7]: ** $f is FULLY DEAD ($doc_dead/$doc_total) — it has stopped firing entirely **"
    fi
  fi
done

if [ "$dead_globs" -eq 0 ]; then
  echo "PASS [test 7]: all triggers across $checked_docs editorial docs resolve"
  PASS=$((PASS+1))
else
  echo "FAIL [test 7]: $dead_globs dead triggers across $dead_docs of $checked_docs docs"
  FAIL=$((FAIL+1))
fi

# ─── Test 8: Synthetic proof — trigger_is_dead sees BOTH directions ───────
# Test 7 only ever exercises trigger_is_dead against docs' CURRENT triggers —
# whatever happens to be alive or dead in the repo today. That proves nothing
# about the function itself: every dead trigger test 7 finds gets repaired
# and stops proving the "reports a real failure" direction tomorrow, and if
# every trigger in the repo happened to be alive, test 7 would never have
# exercised the dead-detection branch at all (nobodies-collective/Humans#1021
# — "a test that only ever sees passing input proves nothing"). This test is
# independent of doc content: it feeds trigger_is_dead synthetic real and
# fake paths directly, one pair per branch (wildcard, literal), and asserts
# each direction explicitly.
real_glob="docs/architecture/**"          # real dir — must NOT be reported dead
dead_glob="src/Humans.NoSuchSection.DoesNotExist/**"   # never existed — must be reported dead
real_literal="$CATALOG"                   # real file — must NOT be reported dead
dead_literal="docs/architecture/this-file-does-not-exist-1021.md"  # must be reported dead

t8_fail=""
if trigger_is_dead "$real_glob"; then t8_fail="$t8_fail glob-real-passed-as-dead"; fi
if ! trigger_is_dead "$dead_glob"; then t8_fail="$t8_fail glob-dead-passed-as-real"; fi
if trigger_is_dead "$real_literal"; then t8_fail="$t8_fail literal-real-passed-as-dead"; fi
if ! trigger_is_dead "$dead_literal"; then t8_fail="$t8_fail literal-dead-passed-as-real"; fi

if [ -z "$t8_fail" ]; then
  echo "PASS [test 8]: trigger_is_dead proves both directions (glob + literal, real + dead)"
  PASS=$((PASS+1))
else
  echo "FAIL [test 8]: trigger_is_dead direction check(s) broken:$t8_fail"
  FAIL=$((FAIL+1))
fi

echo ""
echo "═══ Summary ═══"
echo "Passed: $PASS"
echo "Failed: $FAIL"
exit $FAIL
