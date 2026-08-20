#!/bin/bash
# Shared helpers for freshness-check scripts: enumerate the editorial doc set
# from the catalog's editorial_trees (filtered by its ignore: list), and the
# per-doc trigger extraction / dead-glob detection used to verify them.
# Sourced by diff-mode.sh and verify-triggers.sh so neither definition can
# drift from the other (nobodies-collective/Humans#1021) — a walk hardcoded
# in one script and reused nowhere else is exactly how a second script's
# editorial coverage silently falls behind the catalog.
#
# Requires the caller to `CATALOG=...` before sourcing.

# Catalog's `ignore:` list, one glob pattern per line.
ignore_patterns() {
  awk '/^ignore:/{f=1;next} f&&/^[a-z_]+:/{f=0} f' "$CATALOG" \
    | grep -E '^[[:space:]]+- ' | sed 's/^[[:space:]]*-[[:space:]]*//; s/[[:space:]]*$//'
}

# True (0) if $1 matches any catalog ignore pattern. `case` glob matching
# treats `*` as unbounded (it matches `/` too), so a catalog pattern written
# as `docs/plans/**` behaves the same as `docs/plans/*` here — both match any
# depth, which is what the catalog author intends by using `**`.
path_ignored() {
  local path="$1" pat
  while IFS= read -r pat; do
    [ -z "$pat" ] && continue
    case "$path" in
      $pat) return 0 ;;
    esac
  done < <(ignore_patterns)
  return 1
}

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
#
# Every candidate is also run through the catalog's `ignore:` list (path_ignored
# above) — the find-level `-not -name`/`-not -path` clauses below cover the
# common cases directly (they run inside `find`, which is faster over a big
# tree), but `path_ignored` is the one filter that cannot drift from the
# catalog: e.g. `src/Sections/*/Docs/data-access.md` is ignore-listed but has
# no matching `-not` clause below, and would otherwise be walked.
editorial_docs() {
  awk '/^editorial_trees:/{f=1;next} f&&/^[a-z_]+:/{f=0} f' "$CATALOG" \
    | grep -E '^[[:space:]]+- ' \
    | sed 's/^[[:space:]]*-[[:space:]]*//; s/[[:space:]]*$//' \
    | while IFS= read -r entry; do
        [ -z "$entry" ] && continue
        if [ -f "$entry" ]; then
          path_ignored "$entry" || echo "$entry"
        elif [ -d "${entry%/}" ]; then
          find "${entry%/}" -name '*.md' \
               -not -name 'SECTION-TEMPLATE.md' -not -name 'G5-SECTION-TEMPLATE.md' \
               -not -name 'README.md' -not -name 'GettingStarted.md' -not -name 'Glossary.md' \
               -not -path '*/obj/*' -not -path '*/bin/*' \
               -not -path '*/Docs/20*.md' \
               -not -name 'health.md' 2>/dev/null \
            | while IFS= read -r f; do path_ignored "$f" || echo "$f"; done
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
# editorial_docs simply omits it, the remaining docs still clear test 3's
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

# Extract the raw freshness:triggers glob/literal entries from one doc, one
# per output line, trimmed. Empty output means the doc carries no marker.
# `|| true` is load-bearing: a doc with no marker makes `grep -v` return 1,
# which under a caller's `set -o pipefail` would otherwise abort mid-walk and
# leave every doc after it unchecked (docs/freshness/last-report.md,
# 2026-08-18 sweep, "the checker's own silence is not evidence").
doc_trigger_lines() {
  awk '/<!-- freshness:triggers/,/^-->/' "$1" 2>/dev/null \
    | { grep -vE '^\s*<!--|^-->' || true; } \
    | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

# True (0 / dead) if the glob/literal trigger $1 resolves to zero real files.
# A trigger is either a wildcard pattern or a literal path, and they need
# different checks: `matches=( $glob )` only detects a dead *pattern* (via
# `shopt -s nullglob`, which the caller must have set) — a literal path with
# no wildcard word-splits to itself, so the array always has one element and
# the check would pass whether or not the file exists. That mismatch let 307
# dead literal triggers across 65 docs survive every sweep while a
# pattern-only check reported green (docs/freshness/last-report.md,
# 2026-08-18 sweep) — the same class of bug as a dead glob, one layer down.
trigger_is_dead() {
  local glob="$1"
  case "$glob" in
    *'*'*)
      local matches=( $glob )
      [ ${#matches[@]} -eq 0 ]
      ;;
    *)
      [ ! -e "$glob" ]
      ;;
  esac
}
