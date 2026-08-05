#!/usr/bin/env bash
# Guardrail (nobodies-collective/Humans#767): quarantine discipline.
#
# A test that is permanently skipped (quarantined) must carry a tracking
# issue reference so it doesn't rot silently. Concretely:
#
#   - [BrokenFact("...")]                    -- the reason must contain
#                                                nobodies-collective/Humans#NNN
#   - [HumansFact(Skip = "...")]              -- same, UNLESS the skip is
#   - [HumansTheory(Skip = "...")]               conditional (SkipUnless or
#                                                SkipWhen also set on the same
#                                                attribute) -- that's a
#                                                deliberate runtime gate (e.g.
#                                                debugger-only, opt-in
#                                                maintenance sweep), not a
#                                                quarantine.
#   - [InlineData(..., Skip = "...")]        -- xUnit v3 lets a single theory
#   - [MemberData(..., Skip = "...")]            row be skipped via
#   - [ClassData(..., Skip = "...")]             DataAttribute.Skip; that
#                                                quarantines one case and needs
#                                                the same tracking reference.
#                                                DataAttribute also exposes
#                                                SkipUnless/SkipWhen, so the
#                                                same conditional exemption
#                                                applies per row.
#   - row.WithSkip("...")                    -- ITheoryDataRow.Skip set in code
#                                                quarantines a row just as an
#                                                attribute does.
#   - Assert.Skip("...")                     -- a body-level unconditional skip.
#                                                The conditional forms are
#                                                Assert.SkipWhen/SkipUnless,
#                                                which are exempt.
#
# SkipType alone is NOT treated as a conditional gate: in xUnit v3 it only says
# which type the SkipUnless/SkipWhen property is read from, so `Skip` + bare
# `SkipType` is still an unconditional quarantine.
#
# Bare [Fact]/[Theory] need no handling here -- BannedApiAnalyzers RS0030
# forbids them in test code (see docs/architecture/code-review-rules.md), and
# the preceding CI step rejects RS0030 suppressions.
#
# Skip values and BrokenFact reasons must be plain, verbatim or raw string
# literals. Anything else (a const reference, string concatenation, nameof) is
# rejected rather than waved through: the reason text is what carries the issue
# number, so a value this script cannot read is a value it cannot vouch for.
#
# Attributes are recognised anywhere in a shared bracketed list
# ([Trait(...), HumansFact(Skip = "...")]), with or without the `Attribute`
# suffix, with or without generic type arguments (xUnit v3's ClassData<TRows>),
# and with or without a namespace qualifier or attribute target
# ([method: Humans.Testing.BrokenFact("...")]).
#
# Usage:
#   check-skip-attribute-tracking.sh            # CI gate: fail on untracked
#   check-skip-attribute-tracking.sh --list     # report every quarantine found,
#                                               # tracked ones included, for the
#                                               # monthly /maintenance sweep
#
# See memory/process/no-pre-existing-failures.md for the policy this enforces.

set -euo pipefail

MODE=check
case "${1:-}" in
  ""|--check) MODE=check ;;
  --list)     MODE=list ;;
  *) echo "usage: $(basename "$0") [--check|--list]" >&2; exit 2 ;;
esac

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT_DIR"

# Emits one tab-separated record per quarantine site:
#   BAD<TAB>file:line<TAB>message      -- missing or unreadable tracking ref
#   OK<TAB>file:line<TAB>label<TAB>reason
REPORT=$(perl -0777 -ne '
    my $file = $ARGV;
    my $src  = $_;
    my (@bad, @tracked);

    # C# literal tokens: raw, verbatim, plain string, and char. Used both to
    # balance parens and to step over literal contents when looking for named
    # attribute properties, so text inside a literal is never read as syntax.
    # (\x27 is the single quote -- this program is inside a shell string.)
    sub tok {
        return qr/"""(?s:.*?)"""|\@"(?:[^"]|"")*"|"(?:[^"\\]|\\.)*"|\x27(?:[^\x27\\]|\\.)*\x27/;
    }

    # Reads a C# string literal anchored at the start of $text. Returns the text
    # for plain / verbatim / raw literals, undef for anything else. Raw
    # ("""...""") is tried before plain ("...") so the empty capture between the
    # first two quotes of a raw literal is not mistaken for it.
    sub literal_reason {
        my ($text) = @_;
        return $1 if $text =~ /^\s*"""(.*?)"""/s;
        return $1 if $text =~ /^\s*\@"((?:[^"]|"")*)"/s;
        return $1 if $text =~ /^\s*"((?:[^"\\]|\\.)*)"/s;
        return undef;
    }

    # Finds a named property assignment (Skip=, SkipUnless=, ...) in an argument
    # list and returns the offset just past its "=", or undef. Literal contents
    # are stepped over, so ordinary test data such as
    # [InlineData("Skip = this is just input data")] is not mistaken for a
    # property -- and, in the other direction, a "SkipUnless=" sitting inside a
    # string cannot buy a bogus exemption.
    sub find_prop {
        my ($args, $name) = @_;
        my $tok = tok();
        pos($args) = 0;
        while ($args =~ /\G(?:$tok|(\b\Q$name\E\s*=)|.)/gcs) {
            return pos($args) if defined $1;
        }
        return undef;
    }

    # Reads a Skip= value out of an attribute argument list. Returns:
    #   ()          -- no static Skip= at all
    #   (undef)     -- Skip= present but not a string literal we can read
    #   ($reason)   -- the literal reason text
    sub skip_reason {
        my ($args) = @_;
        my $off = find_prop($args, "Skip");
        return () unless defined $off;
        return (literal_reason(substr($args, $off)));
    }

    # xUnit v3 makes a skip dynamic only when SkipUnless or SkipWhen is set
    # alongside it; that is a deliberate runtime gate, not a quarantine.
    sub conditional {
        my ($args) = @_;
        return 1 if defined find_prop($args, "SkipUnless");
        return 1 if defined find_prop($args, "SkipWhen");
        return 0;
    }

    sub line_at {
        my ($text, $off) = @_;
        return 1 + (substr($text, 0, $off) =~ tr/\n//);
    }

    sub record {
        my ($bad, $tracked, $line, $label, $reason) = @_;
        if (!defined $reason) {
            push @$bad, "$line\t$label is not a readable string literal -- "
                . "use a literal reason naming nobodies-collective/Humans#NNN";
        } elsif ($reason !~ /nobodies-collective\/Humans#\d+/) {
            push @$bad, "$line\t$label missing nobodies-collective/Humans#NNN: \"$reason\"";
        } else {
            push @$tracked, "$line\t$label\t$reason";
        }
    }

    # Builds the matcher for one attribute family. An attribute may sit anywhere
    # in a shared bracketed list ([Trait(...), HumansFact(...)]), so it is
    # anchored on "[" OR "," rather than "[" alone; may carry an attribute
    # target ([method: ...]) and a namespace qualifier; may carry the optional
    # Attribute suffix; and may be generic (xUnit v3 ClassData<TRows>).
    #
    # The argument list is matched with a recursive balanced-paren pattern, not
    # a non-greedy run to the next ")". Argument values contain parens of their
    # own -- typeof(string), nameof(Enabled) -- and a naive match stops at the
    # first inner ")", truncating the captured list. That silently hides any
    # argument after it: a Skip= followed by typeof(...) then SkipWhen= would
    # lose the SkipWhen and be misreported as an unconditional quarantine.
    # String and char literals are stepped over so a ")" inside a reason or an
    # InlineData(\x27)\x27, ...) row does not throw the paren count off.
    sub attr_pattern {
        my ($names) = @_;
        my $tok = tok();
        my $parens = qr/(?<p>\((?:[^()"\x27]++|$tok|(?&p))*+\))/;
        return qr/
            (?:\[|,) \s*
            (?:\w+\s*:\s*)?          # attribute target: [method: ...]
            (?:\w+\s*\.\s*)*         # namespace qualifier
            (?:$names) (?:Attribute)?
            (?:\s*<[^>]*>)?          # generic type arguments
            \s* (?<args>$parens)
        /sx;
    }

    # Strips the outer parens off a captured argument list.
    sub inner_args {
        my ($captured) = @_;
        return substr($captured, 1, length($captured) - 2);
    }

    # [BrokenFact("reason")] / [BrokenFact(reason: "reason")] -- the reason is
    # the first constructor argument, read with the same literal rules as Skip=.
    my $broken = attr_pattern("BrokenFact");
    while ($src =~ /$broken/g) {
        my $line = line_at($src, $-[0]);
        my $args = inner_args($+{args});
        $args =~ s/^\s*reason\s*:\s*//s;
        record(\@bad, \@tracked, $line, "BrokenFact reason", literal_reason($args));
    }

    # [HumansFact(...)] / [HumansTheory(...)] -- inspect the whole argument
    # list so Skip=, SkipUnless=, etc. are seen together regardless of
    # multi-line formatting.
    my $fact = attr_pattern("HumansFact|HumansTheory");
    while ($src =~ /$fact/g) {
        my $line = line_at($src, $-[0]);
        my $args = inner_args($+{args});
        my @found = skip_reason($args);
        next unless @found;
        next if conditional($args);
        record(\@bad, \@tracked, $line, "Skip=", $found[0]);
    }

    # Per-row skips on xUnit data attributes quarantine a single theory case.
    # DataAttribute carries SkipUnless/SkipWhen too, so the same conditional
    # exemption applies.
    my $data = attr_pattern("InlineData|MemberData|ClassData|TheoryData");
    while ($src =~ /$data/g) {
        my $line = line_at($src, $-[0]);
        my $args = inner_args($+{args});
        my @found = skip_reason($args);
        next unless @found;
        next if conditional($args);
        record(\@bad, \@tracked, $line, "data-row Skip=", $found[0]);
    }

    # Programmatic row skips: a data source returning
    # new TheoryDataRow(...).WithSkip("...") quarantines that row in code.
    while ($src =~ /\.\s*WithSkip\s*\(/g) {
        my $line = line_at($src, $-[0]);
        record(\@bad, \@tracked, $line, "WithSkip()", literal_reason(substr($src, pos($src))));
    }

    # Body-level quarantine. Assert.Skip(reason) is unconditional; the
    # conditional forms are Assert.SkipWhen / Assert.SkipUnless, which take a
    # runtime gate and are exempt (the "(" here is required immediately after
    # "Skip", so those two spellings do not match).
    while ($src =~ /\bAssert\s*\.\s*Skip\s*\(/g) {
        my $line = line_at($src, $-[0]);
        record(\@bad, \@tracked, $line, "Assert.Skip()", literal_reason(substr($src, pos($src))));
    }

    print "BAD\t$file:$_\n"  for @bad;
    print "OK\t$file:$_\n"   for @tracked;
' -- $(git ls-files 'tests/**/*.cs') || true)

FAILURES=$(printf '%s\n' "$REPORT" | awk -F'\t' '$1 == "BAD"' | cut -f2-)
TRACKED=$(printf '%s\n'  "$REPORT" | awk -F'\t' '$1 == "OK"'  | cut -f2-)
TRACKED_COUNT=$(printf '%s' "$TRACKED" | grep -c . || true)

if [[ "$MODE" == "list" ]]; then
  if [[ -n "$FAILURES" ]]; then
    echo "untracked quarantines (these fail the CI gate):"
    echo "$FAILURES"
    echo
  fi
  if [[ -n "$TRACKED" ]]; then
    echo "tracked quarantines ($TRACKED_COUNT) -- check each issue is still open:"
    echo "$TRACKED"
  else
    echo "no tracked quarantines."
  fi
  exit 0
fi

if [[ -n "$FAILURES" ]]; then
  echo "error: quarantined test(s) without a tracking issue reference:" >&2
  echo "$FAILURES" >&2
  echo >&2
  echo "Every permanently-skipped test ([BrokenFact(...)], an unconditional" >&2
  echo "Skip= on [HumansFact]/[HumansTheory], a Skip= on a theory data row," >&2
  echo "a .WithSkip(...) row, or a body-level Assert.Skip(...)) must name the" >&2
  echo "tracking issue in a string literal:" >&2
  echo '  [BrokenFact("<what broke>. Tracked at nobodies-collective/Humans#NNN.")]' >&2
  echo "Only SkipUnless=/SkipWhen= mark a conditional gate; SkipType= alone does not." >&2
  echo "For a runtime gate in a test body use Assert.SkipWhen/SkipUnless, not Assert.Skip." >&2
  echo "Fix it right (repair the test) or file/re-reference the tracking issue." >&2
  echo "See memory/process/no-pre-existing-failures.md." >&2
  exit 1
fi

echo "ok: all quarantined tests carry a tracking issue reference ($TRACKED_COUNT tracked; run with --list to review them)."
