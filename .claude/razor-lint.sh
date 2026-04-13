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
#   1. Boolean attribute trap: disabled="@var" instead of disabled="@(cond ? "disabled" : null)"
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

lint_cshtml() {
    local file="$1"

    # --- WARNING checks ---

    # 1. Boolean attribute trap
    # Dangerous: disabled="@someVar" renders disabled="True" or disabled="False" — BOTH disable the element.
    # Safe: disabled="@(condition ? "disabled" : null)" — Razor removes the attribute when value is null.
    # Match: boolean attr followed by ="@ then NOT ( (which indicates a Razor expression block).
    while IFS=: read -r num _rest; do
        emit "WARNING" "$file" "$num" "Boolean attribute trap — use: attr=\"@(cond ? \\\"attr\\\" : null)\""
    done < <(grep -nEi '\s(disabled|readonly|checked|selected|required|multiple|autofocus)="@[^(]' "$file" 2>/dev/null || true)

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
