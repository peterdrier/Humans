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
PASS=0
FAIL=0

if [ ! -f "$CATALOG" ]; then
  echo "FAIL: $CATALOG not found. Run from repo root."
  exit 1
fi

# Every editorial doc the sweep is responsible for, derived FROM the catalog's
# editorial_trees rather than hardcoded here. Entries are either a directory to
# walk (docs/sections/, src/Sections/, …) or a single file listed on its own
# (docs/architecture/design-rules.md, docs/seed-data.md, …).
#
# Read the list from the catalog so this check cannot drift away from it. An
# earlier hardcoded version walked only the four directories and silently skipped
# all six individually-listed files — so a dead glob in design-rules.md or
# seed-data.md still produced a green "all trigger globs resolve", which is the
# exact failure test 7 exists to catch.
editorial_docs() {
  awk '/^editorial_trees:/{f=1;next} f&&/^[a-z_]+:/{f=0} f' "$CATALOG" \
    | grep -E '^[[:space:]]+- ' \
    | sed 's/^[[:space:]]*-[[:space:]]*//; s/[[:space:]]*$//' \
    | while IFS= read -r entry; do
        [ -z "$entry" ] && continue
        if [ -f "$entry" ]; then
          echo "$entry"
        elif [ -d "${entry%/}" ]; then
          find "${entry%/}" -name '*.md' \
               -not -name 'SECTION-TEMPLATE.md' -not -name 'G5-SECTION-TEMPLATE.md' \
               -not -name 'README.md' -not -name 'GettingStarted.md' -not -name 'Glossary.md' \
               -not -path '*/obj/*' -not -path '*/bin/*' \
               -not -path '*/Docs/20*.md' \
               -not -name 'health.md' 2>/dev/null
        else
          # Unresolved entry: emit nothing here — test 3 reports it via
          # editorial_entries_unresolved. The explicit `:` matters: without an
          # else branch the `if` returns the failed `[ -d ]` status, which under
          # `set -e` aborts the whole script mid-run instead of failing a test.
          :
        fi
      done
}

# Catalog editorial_trees entries that resolve to nothing. A warning here is not
# enough: if an individually listed file such as docs/seed-data.md is renamed,
# editorial_docs simply omits it, the remaining ~140 docs still clear test 3's
# thresholds, and tests 4 and 7 never inspect the missing file — so the script
# exits green while the catalog points at nothing. Test 3 fails on any output.
editorial_entries_unresolved() {
  awk '/^editorial_trees:/{f=1;next} f&&/^[a-z_]+:/{f=0} f' "$CATALOG" \
    | grep -E '^[[:space:]]+- ' \
    | sed 's/^[[:space:]]*-[[:space:]]*//; s/[[:space:]]*$//' \
    | while IFS= read -r entry; do
        if [ -n "$entry" ] && [ ! -f "$entry" ] && [ ! -d "${entry%/}" ]; then
          echo "$entry"
        fi
      done
}

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
# The one check that can see a dead trigger. See the header note.
dead_globs=0
dead_docs=0
checked_docs=0
for f in $(editorial_docs); do
  # `|| true` is load-bearing: under `set -o pipefail` the grep returns 1 for a
  # doc with no trigger marker, which aborted the whole script at the first such
  # file and left every doc after it unchecked while test 7 printed nothing at
  # all. Silence from a test is not a pass.
  triggers=$(awk '/<!-- freshness:triggers/,/^-->/' "$f" 2>/dev/null \
             | { grep -vE '^\s*<!--|^-->' || true; } \
             | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
  if [ -z "$triggers" ]; then continue; fi
  checked_docs=$((checked_docs+1))
  doc_dead=0
  doc_total=0
  while IFS= read -r glob; do
    [ -z "$glob" ] && continue
    doc_total=$((doc_total+1))
    # A trigger is either a wildcard pattern or a literal path, and they need
    # different checks. `matches=( $glob )` only detects a dead *pattern* (via
    # nullglob); a literal path with no wildcard word-splits to itself, so the
    # array always has one element and the check passes whether or not the file
    # exists. That is how 307 dead literal triggers across 65 docs survived
    # every sweep while test 7 reported green — the same class of bug as a dead
    # glob, one layer down. Branch on the wildcard.
    # Both branches must END on a successful command. Under `set -euo pipefail`
    # a trailing `cond && assign` returns non-zero whenever cond is false, which
    # aborts the whole script mid-test — the same way a bare trailing `if` once
    # killed everything after test 2. Use explicit if/else, never `&&`.
    trigger_dead=0
    case "$glob" in
      *'*'*)
        matches=( $glob )
        if [ ${#matches[@]} -eq 0 ]; then trigger_dead=1; else trigger_dead=0; fi
        ;;
      *)
        if [ -e "$glob" ]; then trigger_dead=0; else trigger_dead=1; fi
        ;;
    esac
    if [ "$trigger_dead" -eq 1 ]; then
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

echo ""
echo "═══ Summary ═══"
echo "Passed: $PASS"
echo "Failed: $FAIL"
exit $FAIL
