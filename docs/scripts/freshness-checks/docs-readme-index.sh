#!/bin/bash
# Freshness check: docs-readme-index
#
# Verifies that every .md file under docs/sections/, docs/features/, docs/guide/
# (excluding the catalog's ignore list) has a link in docs/README.md.
# docs/features/ holds only the
# cross-section global/ specs; per-section specs live in src/Sections/*/Docs/ and
# are covered by the src/Sections/*/Docs/*.md trigger, not counted here.
#
# Source: docs/sections/**/*.md, docs/features/**/*.md, docs/guide/**/*.md
# Doc:    docs/README.md

set -euo pipefail

DOC="docs/README.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [docs-readme-index]: $DOC does not exist"
  exit 1
fi

# Check exact destinations so duplicates cannot hide missing entries, and multiple
# links on one line each count. Fragment links also index their source document.
FAIL=false
check_tree() {
  local dir="$1"; shift
  local find_args=(-name "*.md")
  local src=0 linked=0 file target files ex
  for ex in "$@"; do
    find_args+=(-not -name "$ex")
  done
  if [ ! -d "$dir" ]; then
    echo "  MISSING tree: $dir"
    FAIL=true
    return
  fi
  if ! files=$(find "$dir" "${find_args[@]}" -print); then
    echo "  FAILED to enumerate: $dir"
    FAIL=true
    return
  fi
  while IFS= read -r file; do
    [ -z "$file" ] && continue
    src=$((src + 1))
    target="${file#docs/}"
    if grep -qF "]($target)" "$DOC" || grep -qF "]($target#" "$DOC"; then
      linked=$((linked + 1))
    else
      echo "  MISSING link: $target"
      FAIL=true
    fi
  done <<< "$files"
  echo "  $dir: src=$src linked=$linked"
}

echo "[docs-readme-index] coverage:"
# Both section templates and these guide entry pages are catalog exemptions.
check_tree docs/sections SECTION-TEMPLATE.md G5-SECTION-TEMPLATE.md
check_tree docs/features
check_tree docs/guide README.md GettingStarted.md Glossary.md

if [ "$FAIL" = "true" ]; then
  echo "FAIL [docs-readme-index]: source documents missing from README"
  exit 1
fi

echo "PASS [docs-readme-index]: README links to every non-exempt source document"
exit 0
