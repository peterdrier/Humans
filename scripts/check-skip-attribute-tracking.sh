#!/usr/bin/env bash
# Guardrail (nobodies-collective/Humans#767): quarantine discipline.
#
# A test that is permanently skipped (quarantined) must carry a tracking
# issue reference so it doesn't rot silently. Concretely:
#
#   - [BrokenFact("...")]                    -- the reason must contain
#                                                nobodies-collective/Humans#NNN
#   - [HumansFact(Skip = "...")]              -- same, UNLESS the skip is
#   - [HumansTheory(Skip = "...")]               conditional (SkipUnless /
#                                                SkipWhen / SkipType also set
#                                                on the same attribute) --
#                                                that's a deliberate runtime
#                                                gate (e.g. debugger-only,
#                                                opt-in maintenance sweep),
#                                                not a quarantine.
#
# See memory/process/no-pre-existing-failures.md for the policy this enforces.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT_DIR"

FAILURES=$(perl -0777 -ne '
    my $file = $ARGV;
    my @bad;

    # [BrokenFact("reason")] / [BrokenFact(reason: "reason")]
    while (/\[BrokenFact\(\s*(?:reason\s*:\s*)?"((?:[^"\\]|\\.)*)"/gs) {
        my $reason = $1;
        push @bad, "BrokenFact reason missing nobodies-collective/Humans#NNN: \"$reason\""
            unless $reason =~ /nobodies-collective\/Humans#\d+/;
    }

    # [HumansFact(...)] / [HumansTheory(...)] -- inspect the whole argument
    # list so Skip=, SkipUnless=, etc. are seen together regardless of
    # multi-line formatting.
    while (/\[(?:HumansFact|HumansTheory)\((.*?)\)\s*\]/gs) {
        my $args = $1;
        next unless $args =~ /\bSkip\s*=\s*"((?:[^"\\]|\\.)*)"/s;
        my $reason = $1;
        next if $args =~ /\bSkipUnless\s*=|\bSkipWhen\s*=|\bSkipType\s*=/s; # conditional gate, not quarantine
        push @bad, "Skip=\"$reason\" missing nobodies-collective/Humans#NNN"
            unless $reason =~ /nobodies-collective\/Humans#\d+/;
    }

    print "$file: $_\n" for @bad;
' -- $(git ls-files 'tests/**/*.cs') || true)

if [[ -n "$FAILURES" ]]; then
  echo "error: quarantined test(s) without a tracking issue reference:" >&2
  echo "$FAILURES" >&2
  echo >&2
  echo "Every permanently-skipped test ([BrokenFact(...)] or an unconditional" >&2
  echo "Skip= on [HumansFact]/[HumansTheory]) must name the tracking issue:" >&2
  echo '  [BrokenFact("<what broke>. Tracked at nobodies-collective/Humans#NNN.")]' >&2
  echo "Fix it right (repair the test) or file/re-reference the tracking issue." >&2
  echo "See memory/process/no-pre-existing-failures.md." >&2
  exit 1
fi

echo "ok: all quarantined tests carry a tracking issue reference."
