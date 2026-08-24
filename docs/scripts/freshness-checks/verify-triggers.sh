#!/bin/bash
# Verify every freshness:triggers entry across the catalog's editorial_trees
# docs; auto-repair the ones with an unambiguous new target in place; report
# the rest. This is the mechanism nobodies-collective/Humans#1021 asked for:
# a dead trigger glob makes a doc look CLEAN instead of UNCHECKED — it
# silently matches zero files, so the doc never enters the sweep's dirty
# list. Two sweeps (2026-08-08, 2026-08-10) found large batches of these ad
# hoc, with a throwaway verifier, AFTER the dirty list had already been
# computed from the broken globs — so the sweep that found them had already
# skipped the affected docs that run.
#
# Called from /freshness-sweep Phase 3.5, BEFORE Phase 4 computes the dirty
# list, so a glob this script repairs fires as dirty in the same run.
#
# Usage (run from repo root):
#   bash docs/scripts/freshness-checks/verify-triggers.sh [--check]
#
# Default mode REPAIRS: rewrites resolvable dead trigger lines in the doc
# files themselves. --check reports only and makes no edits (a dry run —
# diff-mode.sh test 7 already covers read-only verification by hand; this
# flag exists for scripting a preview of what this script would change).
#
# Exit code is always 0 — this is sweep hygiene, not a CI gate (per the
# issue's Problem/Motivation: "a dead trigger glob doesn't break anything at
# runtime ... caught and repaired by the sweep itself, not gated in CI").
# Callers read stdout, not the exit code.
#
# Output, one line per finding:
#   REPAIRED <doc> | <old> -> <new>
#   UNRESOLVED <doc> | <old>
# then a summary line:
#   SUMMARY repaired=<n> unresolved=<n> docs_forced_dirty=<n>
# followed by one line per doc carrying >=1 UNRESOLVED trigger:
#   FORCE-DIRTY <doc>
# Phase 4 unions FORCE-DIRTY docs into its dirty list regardless of whether
# anything in the diff window matches — an unresolved dead trigger means the
# doc needs a human-legible review this run, not silent skipping.

set -uo pipefail  # deliberately no -e: one bad doc must not silence the rest
                   # (docs/freshness/last-report.md, 2026-08-18 sweep — "the
                   # checker's own silence is not evidence").

CATALOG="docs/architecture/freshness-catalog.yml"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=./lib-editorial-docs.sh
source "$SCRIPT_DIR/lib-editorial-docs.sh"

if [ ! -f "$CATALOG" ]; then
  echo "ERROR: $CATALOG not found. Run from repo root." >&2
  exit 1
fi

MODE="repair"
if [ "${1:-}" = "--check" ]; then MODE="check"; fi

shopt -s globstar nullglob

# Resolve a dead trigger to its unambiguous new target, or print nothing.
#
# Wildcard triggers (contain `*`): anchor on the last static directory
# segment before the first `*` (e.g. "Guest" in
# src/Humans.Web/Views/Guest/**), find it by basename anywhere under src/,
# and reattach the original wildcard suffix. Literal triggers: anchor on the
# basename and find it the same way.
#
# "Unambiguous" means exactly one hit — zero or multiple is left unresolved,
# never guessed. This is also how "the section-project equivalent" resolves:
# a section's uniquely-named directory or file has exactly one hit under its
# new src/Sections/Humans.<X>/ home, so no section-specific logic is needed —
# it falls out of the same basename search the literal case uses.
resolve_target() {
  local glob="$1" prefix suffix seg hits n base
  case "$glob" in
    *'*'*)
      prefix="${glob%%\**}"
      suffix="${glob#"$prefix"}"
      seg="${prefix%/}"; seg="${seg##*/}"
      [ -z "$seg" ] && return
      hits=$(find src -type d -name "$seg" -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null)
      n=$(printf '%s\n' "$hits" | sed '/^$/d' | wc -l)
      [ "$n" -ne 1 ] && return
      echo "${hits}/${suffix}"
      ;;
    *)
      base="${glob##*/}"
      hits=$(find src -name "$base" -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null)
      n=$(printf '%s\n' "$hits" | sed '/^$/d' | wc -l)
      [ "$n" -ne 1 ] && return
      echo "$hits"
      ;;
  esac
}

# Rewrite the first not-yet-touched line in $1 whose trimmed content equals
# $2, to $3, preserving the original leading whitespace. awk (not sed) so the
# old/new strings never need regex escaping — both routinely contain `*`,
# `.` and `/`.
repair_line() {
  local file="$1" old="$2" new="$3"
  awk -v old="$old" -v new="$new" '
    {
      line = $0
      trimmed = line
      sub(/^[ \t]+/, "", trimmed)
      sub(/[ \t]+$/, "", trimmed)
      if (!done && trimmed == old) {
        match(line, /^[ \t]*/)
        indent = substr(line, RSTART, RLENGTH)
        print indent new
        done = 1
        next
      }
      print line
    }
  ' "$file" > "$file.verify-triggers.tmp" && mv "$file.verify-triggers.tmp" "$file"
}

repaired=0
unresolved=0
declare -A dirty_docs
# Seed and unset so the array is *declared with storage* — on Bash < 4.4 an
# associative array that never held a key expands as unbound under `set -u`,
# crashing the clean case (zero unresolved triggers) with no SUMMARY line.
dirty_docs["__seed__"]=1
unset "dirty_docs[__seed__]"

for f in $(editorial_docs); do
  triggers=$(doc_trigger_lines "$f")
  [ -z "$triggers" ] && continue
  while IFS= read -r glob; do
    [ -z "$glob" ] && continue
    if trigger_is_dead "$glob"; then
      target=$(resolve_target "$glob")
      if [ -n "$target" ]; then
        echo "REPAIRED $f | $glob -> $target"
        repaired=$((repaired + 1))
        if [ "$MODE" = "repair" ]; then
          repair_line "$f" "$glob" "$target"
        fi
      else
        echo "UNRESOLVED $f | $glob"
        unresolved=$((unresolved + 1))
        dirty_docs["$f"]=1
      fi
    fi
  done <<< "$triggers"
done

# `${#dirty_docs[@]}` / `${!dirty_docs[@]}` on an associative array that never had a
# key assigned is an unbound-variable error under `set -u` in Bash < 4.4 (Git Bash on
# Windows ships one). So the clean case — zero unresolved triggers, the state every
# sweep is working toward — crashed instead of printing SUMMARY, exactly when the
# script has nothing to report. `${x[@]:-}` makes the empty expansion safe.
forced=0
forced_list=""
if [ ${#dirty_docs[@]} -gt 0 ] 2>/dev/null; then
  forced=${#dirty_docs[@]}
  forced_list=$(printf '%s
' "${!dirty_docs[@]}")
fi
echo "SUMMARY repaired=$repaired unresolved=$unresolved docs_forced_dirty=$forced"
if [ -n "$forced_list" ]; then
  while IFS= read -r d; do
    [ -z "$d" ] && continue
    echo "FORCE-DIRTY $d"
  done <<< "$forced_list"
fi

exit 0
