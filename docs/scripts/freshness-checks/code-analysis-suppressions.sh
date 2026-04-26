#!/bin/bash
# Freshness check: code-analysis-suppressions
#
# Verifies that every analyzer suppression code from:
#   - <NoWarn> in Directory.Build.props
#   - <NoWarn> in tests/Directory.Build.props
#   - The "RS0030" rule that backs tests/BannedSymbols.txt (BannedApiAnalyzers)
# appears between the `freshness:auto id="suppressions"` markers in
# docs/architecture/code-analysis.md.

set -euo pipefail

DOC="docs/architecture/code-analysis.md"

if [ ! -f "$DOC" ]; then
  echo "FAIL [code-analysis-suppressions]: $DOC does not exist"
  exit 1
fi

# Extract codes from <NoWarn>$(NoWarn);A;B;C</NoWarn> (semicolon-separated).
extract_nowarn() {
  local file="$1"
  [ -f "$file" ] || return 0
  grep -oE '<NoWarn>[^<]*</NoWarn>' "$file" \
    | sed -E 's/<\/?NoWarn>//g' \
    | tr ';' '\n' \
    | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//' \
    | grep -E '^[A-Z]+[0-9]+$' || true
}

CODES=$(
  {
    extract_nowarn Directory.Build.props
    extract_nowarn tests/Directory.Build.props
    # tests/BannedSymbols.txt is enforced by Microsoft.CodeAnalysis.BannedApiAnalyzers,
    # which raises rule RS0030. The doc covers RS0030 in the test attribute
    # policy section.
    if [ -f tests/BannedSymbols.txt ] && [ -s tests/BannedSymbols.txt ]; then
      echo "RS0030"
    fi
  } | sort -u
)

# Extract the suppressions block.
BLOCK=$(awk '/freshness:auto id="suppressions"/,/\/freshness:auto/' "$DOC")
if [ -z "$BLOCK" ]; then
  echo "FAIL [code-analysis-suppressions]: suppressions block not found in $DOC"
  exit 1
fi

MISSING=""
MISS_COUNT=0
TOTAL=0
while IFS= read -r CODE; do
  [ -z "$CODE" ] && continue
  TOTAL=$((TOTAL + 1))
  # RS0030 is described in the surrounding test-attribute-policy section, so
  # accept presence anywhere in the doc rather than strictly inside the block.
  if [ "$CODE" = "RS0030" ]; then
    if ! grep -qF "$CODE" "$DOC"; then
      MISSING="${MISSING}${CODE} (anywhere in doc)
"
      MISS_COUNT=$((MISS_COUNT + 1))
    fi
  else
    if ! echo "$BLOCK" | grep -qF "$CODE"; then
      MISSING="${MISSING}${CODE}
"
      MISS_COUNT=$((MISS_COUNT + 1))
    fi
  fi
done <<< "$CODES"

if [ "$MISS_COUNT" -gt 0 ]; then
  echo "[code-analysis-suppressions] missing $MISS_COUNT/$TOTAL codes:"
  echo "$MISSING" | sed 's/^/  - /' | grep -v '^  - $' || true
  echo "FAIL [code-analysis-suppressions]"
  exit 1
fi

echo "PASS [code-analysis-suppressions]: all $TOTAL suppression codes referenced in $DOC"
exit 0
