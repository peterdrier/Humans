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
# suffix, and with or without generic type arguments (xUnit v3's
# ClassData<TRows>).
#
# See memory/process/no-pre-existing-failures.md for the policy this enforces.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT_DIR"

FAILURES=$(perl -0777 -ne '
    my $file = $ARGV;
    my @bad;

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

    # Reads a Skip= value out of an attribute argument list. Returns:
    #   ()          -- no static Skip= at all
    #   (undef)     -- Skip= present but not a string literal we can read
    #   ($reason)   -- the literal reason text
    sub skip_reason {
        my ($args) = @_;
        return () unless $args =~ /\bSkip\s*=(.*)$/s;
        return (literal_reason($1));
    }

    sub check_reason {
        my ($bad, $label, $reason) = @_;
        if (!defined $reason) {
            push @$bad, "$label is not a readable string literal -- "
                . "use a literal reason naming nobodies-collective/Humans#NNN";
        } elsif ($reason !~ /nobodies-collective\/Humans#\d+/) {
            push @$bad, "$label missing nobodies-collective/Humans#NNN: \"$reason\"";
        }
    }

    # Builds the matcher for one attribute family. An attribute may sit anywhere
    # in a shared bracketed list ([Trait(...), HumansFact(...)]), so it is
    # anchored on "[" OR "," rather than "[" alone; may carry the optional
    # Attribute suffix; and may be generic (xUnit v3 ClassData<TRows>).
    #
    # The argument list is matched with a recursive balanced-paren pattern, not
    # a non-greedy run to the next ")". Argument values contain parens of their
    # own -- typeof(string), nameof(Enabled) -- and a naive match stops at the
    # first inner ")", truncating the captured list. That silently hides any
    # argument after it: a Skip= followed by typeof(...) then SkipWhen= would
    # lose the SkipWhen and be misreported as an unconditional quarantine.
    # String literals are skipped over so a ")" inside a reason does not throw
    # the paren count off.
    sub attr_pattern {
        my ($names) = @_;
        my $parens = qr/(?<p>\((?:[^()"]++|"(?:[^"\\]|\\.)*+"|(?&p))*+\))/;
        return qr/(?:\[|,)\s*(?:$names)(?:Attribute)?(?:\s*<[^>]*>)?\s*(?<args>$parens)/s;
    }

    # Strips the outer parens off a captured argument list.
    sub inner_args {
        my ($captured) = @_;
        return substr($captured, 1, length($captured) - 2);
    }

    # [BrokenFact("reason")] / [BrokenFact(reason: "reason")] -- the reason is
    # the first constructor argument, read with the same literal rules as Skip=.
    my $broken = attr_pattern("BrokenFact");
    while (/$broken/g) {
        my $args = inner_args($+{args});
        $args =~ s/^\s*reason\s*:\s*//s;
        check_reason(\@bad, "BrokenFact reason", literal_reason($args));
    }

    # [HumansFact(...)] / [HumansTheory(...)] -- inspect the whole argument
    # list so Skip=, SkipUnless=, etc. are seen together regardless of
    # multi-line formatting.
    my $fact = attr_pattern("HumansFact|HumansTheory");
    while (/$fact/g) {
        my $args = inner_args($+{args});
        my @found = skip_reason($args);
        next unless @found;
        next if $args =~ /\bSkipUnless\s*=|\bSkipWhen\s*=/s; # conditional gate, not quarantine
        check_reason(\@bad, "Skip=", $found[0]);
    }

    # Per-row skips on xUnit data attributes quarantine a single theory case.
    my $data = attr_pattern("InlineData|MemberData|ClassData|TheoryData");
    while (/$data/g) {
        my $args = inner_args($+{args});
        my @found = skip_reason($args);
        next unless @found;
        check_reason(\@bad, "data-row Skip=", $found[0]);
    }

    print "$file: $_\n" for @bad;
' -- $(git ls-files 'tests/**/*.cs') || true)

if [[ -n "$FAILURES" ]]; then
  echo "error: quarantined test(s) without a tracking issue reference:" >&2
  echo "$FAILURES" >&2
  echo >&2
  echo "Every permanently-skipped test ([BrokenFact(...)], an unconditional" >&2
  echo "Skip= on [HumansFact]/[HumansTheory], or a Skip= on a theory data row)" >&2
  echo "must name the tracking issue in a string literal:" >&2
  echo '  [BrokenFact("<what broke>. Tracked at nobodies-collective/Humans#NNN.")]' >&2
  echo "Only SkipUnless=/SkipWhen= mark a conditional gate; SkipType= alone does not." >&2
  echo "Fix it right (repair the test) or file/re-reference the tracking issue." >&2
  echo "See memory/process/no-pre-existing-failures.md." >&2
  exit 1
fi

echo "ok: all quarantined tests carry a tracking issue reference."
