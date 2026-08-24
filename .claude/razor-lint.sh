#!/usr/bin/env bash
# razor-lint.sh — Lint Razor views and C# files for common pitfalls
#
# Usage:
#   razor-lint.sh [--staged | --commit-all] [--hook] [file...]
#
# Options:
#   --staged       Check staged files (git diff --cached). Use for plain `git commit`.
#   --commit-all   Check tracked modifications (git diff HEAD). Use for `git commit -a`,
#                  which stages tracked edits during the commit itself.
#   --hook         Output in Claude Code hook JSON format
#   file...        Check specific files (supports .cshtml and .cs)
#
# Checks (WARNING level — should fix):
#   1. Boolean attribute trap: disabled="@var" or disabled="@(expr)" instead of
#      disabled="@(cond ? "disabled" : null)"
#   2. Bootstrap Icons: bi bi-* classes (should be Font Awesome 6)
#   3. Inline event handlers: onclick=, onsubmit= etc. (CSP violation)
#
# Checks (INFO level — review suggested):
#   4. Terminology: "member(s)", "volunteer(s)", "user(s)" in user-facing text
#
# Exit codes:
#   0 — no warnings (infos are OK)
#   1 — warnings found

set -euo pipefail

WARNINGS=0
INFOS=0
OUTPUT=""

emit() {
    local level="$1"
    local file="$2"
    local line="$3"
    local msg="$4"
    OUTPUT+="$level: $file:$line: $msg"$'\n'
    if [ "$level" = "WARNING" ]; then ((WARNINGS++)) || true; fi
    if [ "$level" = "INFO" ]; then ((INFOS++)) || true; fi
}

# HTML boolean attributes: ANY value activates them, "False" and "" included. Only an absent
# attribute is off, which is why the sanctioned form yields null rather than a falsy string.
BOOL_ATTRS='disabled|readonly|checked|selected|required|multiple|autofocus'

# Each occurrence is judged on its own — a per-LINE test would let one already-fixed attribute
# suppress an unsafe one sharing its line. An occurrence runs from its `attr="@` to the next
# `="@` on the line (any attribute, not just a boolean one) or to end of line.
#
# It is safe if that slice holds Razor's escaped `@@` (prose about the rule) or ends the value
# with `: null)"`. Nothing here parses parentheses: the false arm is what carries the safety, so
# the test is what the expression ENDS with, and arbitrarily nested calls in the condition —
# `@(string.IsNullOrEmpty(Model.Name.Trim()) ? "disabled" : null)` — pass without a nesting limit.
BOOL_ATTR_START="[[:space:]]($BOOL_ATTRS)=\"@"
BOOL_ATTR_SAFE='="@@|:[[:space:]]*null[[:space:]]*\)[[:space:]]*"'

lint_cshtml() {
    local file="$1"

    # --- WARNING checks ---

    # 1. Boolean attribute trap
    # Dangerous: disabled="@someVar" AND disabled="@(someExpr)" both render disabled="True"/"False"
    # — either value activates the attribute.
    # Safe: disabled="@(condition ? "disabled" : null)" — Razor omits the attribute when null.
    # Keying the include on the character after `@` cannot work: the dangerous and the safe form
    # both write `@(`. See the BOOL_ATTR_* definitions above for how an occurrence is sliced.
    while IFS=: read -r num _rest; do
        emit "WARNING" "$file" "$num" "Boolean attribute trap — use: attr=\"@(cond ? \\\"attr\\\" : null)\""
    done < <(awk -v start="$BOOL_ATTR_START" -v safe="$BOOL_ATTR_SAFE" '
        {
            # tolower preserves byte offsets, so we match case-insensitively without gawk IGNORECASE
            line = tolower($0)

            # every `="@` on the line — these bound one attribute value from the next
            nb = 0; off = 0; s = line
            while (match(s, "=\"@")) {
                nb++; bound[nb] = off + RSTART
                off += RSTART + 2; s = substr(line, off + 1)
            }

            # each boolean-attribute occurrence, sliced to the next boundary after its own
            off = 0; s = line
            while (match(s, start)) {
                from = off + RSTART
                off = from + RLENGTH - 1     # past this occurrence own `="@`
                s = substr(line, off + 1)
                end = length(line) + 1
                for (i = 1; i <= nb; i++) if (bound[i] > off) { end = bound[i]; break }
                slice = substr(line, from, end - from)
                if (slice !~ safe) print NR ":" slice
            }
        }
    ' "$file" 2>/dev/null || true)

    # 2. Bootstrap Icons (project uses Font Awesome 6 only)
    while IFS=: read -r num _rest; do
        emit "WARNING" "$file" "$num" "Bootstrap Icon — use Font Awesome 6 (fa-solid, fa-regular, fa-brands)"
    done < <(grep -nE '\bbi\s+bi-' "$file" 2>/dev/null || true)

    # 3. Inline event handlers (CSP violation — use data-* attributes + addEventListener)
    # Require whitespace before on* to match HTML attributes, not JS property access (e.g. s.onload = ...)
    while IFS=: read -r num _rest; do
        emit "WARNING" "$file" "$num" "Inline event handler — use data-* attributes + addEventListener (CSP)"
    done < <(grep -nEi '\son(click|submit|change|load|focus|blur|keydown|keyup|keypress|input|reset)\s*=' "$file" 2>/dev/null || true)

    # --- INFO checks ---

    # 4. Terminology — flag lines with "member(s)", "volunteer(s)", "user(s)" for review
    # Exclude lines that are clearly C# code, Razor directives, tag helper attributes, or localizer calls.
    # Also exclude CamelCase compound identifiers (TeamMember, VolunteerProfiles, UserService, etc.)
    while IFS=: read -r num _rest; do
        emit "INFO" "$file" "$num" "Terminology — verify 'humans' should be used instead of member/volunteer/user"
    done < <(grep -niE '(^|[^A-Za-z])(members?|volunteers?|users?)([^A-Za-z]|$)' "$file" 2>/dev/null \
        | grep -viE '@(if|for|foreach|while|using|model|inject|section)\b|@\{|@\*|@member|@user|@volunteer|\.Members|\.Volunteers|\.Users|\.Member|\.User|\.Volunteer|Model\.|ViewData\[|ViewBag\.|asp-(action|controller|route|area)=|@Localizer|@SharedLocalizer|MembershipTier|TeamMember|IsVolunteer|VolunteerProfile|VolunteerCoordinator|volunteer-|UserId|UserName|UserEmail|userName|userId|userEmail|user-|member-|GetUser|AddUser|RemoveUser|CurrentUser|fa-users|fa-user|AuthorizeAsync|ClaimsPrincipal|HttpContext|var\s|await\s|return\s|<!--' \
        | cut -d: -f1 || true)
}

lint_cs() {
    local file="$1"

    # Skip generated files (migrations, designer files)
    if echo "$file" | grep -qE '(\.Designer\.cs|\.g\.cs|Migrations/)'; then
        return
    fi

    # String methods without StringComparison parameter
    # Match: .Contains("...") .StartsWith("...") .EndsWith("...") with only a string argument
    # Skip: lines that are clearly LINQ/EF queries (these can't use StringComparison)
    while IFS=: read -r num _rest; do
        emit "INFO" "$file" "$num" "String method without StringComparison — consider adding StringComparison.Ordinal[IgnoreCase]"
    done < <(grep -nE '\.(Contains|StartsWith|EndsWith)\("[^"]*"\)' "$file" 2>/dev/null \
        | grep -vE '\.Where\(|\.Any\(|\.All\(|\.Count\(|\.First\(|\.Single\(|\.Select\(|\.OrderBy\(|Include\(' \
        | cut -d: -f1 || true)
}

# --- Parse arguments ---

FILES=()
STAGED=false
COMMIT_ALL=false
HOOK_FORMAT=false

for arg in "$@"; do
    case "$arg" in
        --staged)     STAGED=true ;;
        --commit-all) COMMIT_ALL=true ;;
        --hook)       HOOK_FORMAT=true ;;
        *)            FILES+=("$arg") ;;
    esac
done

# Collect files to check. --commit-all takes precedence (git commit -a mode).
if [ "$COMMIT_ALL" = true ]; then
    # git commit -a will stage tracked modifications during the commit — check all tracked changes
    while IFS= read -r f; do
        [ -n "$f" ] && FILES+=("$f")
    done < <(git diff HEAD --name-only --diff-filter=ACM -- '*.cshtml' '*.cs' 2>/dev/null || true)
elif [ "$STAGED" = true ]; then
    while IFS= read -r f; do
        [ -n "$f" ] && FILES+=("$f")
    done < <(git diff --cached --name-only --diff-filter=ACM -- '*.cshtml' '*.cs' 2>/dev/null || true)
fi

# Nothing to check
if [ ${#FILES[@]} -eq 0 ]; then
    exit 0
fi

# --- Run checks ---

for file in "${FILES[@]}"; do
    [ -f "$file" ] || continue
    case "$file" in
        *.cshtml) lint_cshtml "$file" ;;
        *.cs)     lint_cs "$file" ;;
    esac
done

# --- Output ---

if [ -z "$OUTPUT" ]; then
    exit 0
fi

if [ "$HOOK_FORMAT" = true ]; then
    # Escape for JSON: newlines and quotes
    ESCAPED=$(echo "$OUTPUT" | sed 's/\\/\\\\/g; s/"/\\"/g' | tr '\n' '|' | sed 's/|/\\n/g')
    echo "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"additionalContext\":\"RAZOR LINT found issues in staged files:\\n${ESCAPED}\\n${WARNINGS} warning(s), ${INFOS} info(s). Fix WARNINGs before committing. Review INFOs.\"}}"
else
    echo "$OUTPUT"
    echo "--- razor-lint: $WARNINGS warning(s), $INFOS info(s) ---"
fi

if [ "$WARNINGS" -gt 0 ]; then
    exit 1
fi

exit 0
