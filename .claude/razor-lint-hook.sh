#!/usr/bin/env bash
# razor-lint-hook.sh — PreToolUse hook wrapper for razor-lint.sh
#
# Reads Claude Code tool-call JSON from stdin. If the tool is a `git commit`,
# picks the right linter mode:
#   - plain `git commit`           → --staged     (check git diff --cached)
#   - `git commit -a/-am/--all`    → --commit-all (check git diff HEAD,
#                                    because -a stages tracked edits during commit)
#
# This wrapper exists because a pure-JSON hook command can't cleanly branch
# on the command flags and still handle stdin correctly.

set -euo pipefail

INPUT=$(cat)
CMD=$(echo "$INPUT" | jq -r '.tool_input.command // ""')

# Only act on `git commit` invocations
echo "$CMD" | grep -qE '^\s*git\s+commit\b' || exit 0

# Detect -a/-am/--all: short flag cluster containing 'a', or explicit --all
if echo "$CMD" | grep -qE '(\s-[a-zA-Z]*a[a-zA-Z]*(\s|$|")|\s--all(\s|=|$|"))'; then
    # -a mode: check tracked modifications (staged + unstaged)
    git diff HEAD --name-only 2>/dev/null | grep -qE '\.cshtml$' || exit 0
    bash .claude/razor-lint.sh --commit-all --hook
else
    # plain commit: check only staged files
    git diff --cached --name-only 2>/dev/null | grep -qE '\.cshtml$' || exit 0
    bash .claude/razor-lint.sh --staged --hook
fi
