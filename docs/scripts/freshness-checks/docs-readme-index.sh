#!/bin/bash
# Freshness check: docs-readme-index
#
# Counts .md files under docs/sections/, docs/features/, docs/guide/ (excluding
# the catalog's ignore list) and verifies that docs/README.md has at least that
# many link occurrences pointing into each folder.
#
# Source: docs/sections/**/*.md, docs/features/**/*.md, docs/guide/**/*.md
# Doc:    docs/README.md

set -euo pipefail

DOC="docs/README.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [docs-readme-index]: $DOC does not exist"
  exit 1
fi

# Count source docs per tree, applying catalog ignore rules.
count_md() {
  local dir="$1"; shift
  local excludes=("$@")
  local find_args=(-name "*.md")
  for ex in "${excludes[@]}"; do
    find_args+=(-not -name "$ex")
  done
  find "$dir" "${find_args[@]}" 2>/dev/null | wc -l
}

SECTIONS_SRC=$(count_md docs/sections SECTION-TEMPLATE.md)
FEATURES_SRC=$(count_md docs/features)
GUIDE_SRC=$(count_md docs/guide README.md GettingStarted.md Glossary.md)

# Count link occurrences in README that point into each folder.
SECTIONS_DOC=$(grep -cE '\]\(sections/[^)]+\.md\)' "$DOC" || true)
FEATURES_DOC=$(grep -cE '\]\(features/[^)]+\.md\)' "$DOC" || true)
GUIDE_DOC=$(grep -cE '\]\(guide/[^)]+\.md\)' "$DOC" || true)

FAIL=false
report() {
  local label="$1" src="$2" doc="$3"
  if [ "$doc" -lt "$src" ]; then
    echo "  $label: src=$src doc=$doc — MISSING $((src - doc))"
    FAIL=true
  else
    echo "  $label: src=$src doc=$doc — ok"
  fi
}

echo "[docs-readme-index] counts:"
report "sections" "$SECTIONS_SRC" "$SECTIONS_DOC"
report "features" "$FEATURES_SRC" "$FEATURES_DOC"
report "guide   " "$GUIDE_SRC"    "$GUIDE_DOC"

if [ "$FAIL" = "true" ]; then
  echo "FAIL [docs-readme-index]: README link count below source count for at least one tree"
  exit 1
fi

echo "PASS [docs-readme-index]: README has >= source count for all three trees"
exit 0
