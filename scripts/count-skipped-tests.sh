#!/usr/bin/env bash
# Quarantine-discipline sweep (nobodies-collective/Humans#767,
# memory/process/no-pre-existing-failures.md).
#
# Reports every skipped test under tests/ and the tracking issue its skip
# string references, sorted oldest-issue-first (lowest issue number = oldest
# debt). The recurring /maintenance sweep runs this so quarantined tests
# resurface for repair instead of accumulating silently.
#
# Targets `Skip = "..."` attribute strings (followed by a quote), which
# excludes the `set => base.Skip = value;` property shadows in
# tests/Humans.Testing/.
#
# Exit code is always 0 — this is a report, not a gate. The CI grep step in
# .github/workflows/build.yml is the gate that fails on a missing issue ref.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT_DIR"

# file:line:Skip-string for every skipped test.
SKIPS="$(grep -rnE --include='*.cs' --exclude-dir=Humans.Testing 'Skip[[:space:]]*=[[:space:]]*"' tests/ || true)"

if [[ -z "$SKIPS" ]]; then
  echo "No skipped tests under tests/."
  exit 0
fi

TOTAL="$(printf '%s\n' "$SKIPS" | grep -c '' || true)"
echo "Skipped tests: $TOTAL"
echo

# Emit "issueNumber<TAB>file:line  (issue ref)" for sorting, then strip the
# sort key. Tests with no issue ref sort last (sentinel 999999999) and are
# flagged — CI should already block these, so they only appear if CI was
# bypassed or the check is mid-rollout.
printf '%s\n' "$SKIPS" | while IFS= read -r line; do
  loc="$(printf '%s' "$line" | cut -d: -f1-2)"   # file:line
  issue="$(printf '%s' "$line" | grep -oE 'nobodies-collective/Humans#[0-9]+' | head -n1 || true)"
  if [[ -n "$issue" ]]; then
    num="${issue##*#}"
    printf '%09d\t%s\t%s\n' "$num" "$loc" "$issue"
  else
    printf '%09d\t%s\t%s\n' 999999999 "$loc" "NO TRACKING ISSUE (policy violation)"
  fi
done | sort | while IFS=$'\t' read -r _num loc issue; do
  printf '  %-70s %s\n' "$loc" "$issue"
done
