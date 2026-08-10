#!/usr/bin/env bash
# PreToolUse(Bash) hook: surface the DB-migration checklist when a `git add`/`git commit`
# runs with migration-ish files staged. Advisory only — always exits 0.
# The checklist lives in db-migration-checklist.txt and is injected with jq --rawfile so
# apostrophes/quotes in the text can never break shell quoting (the failure mode that took
# down the previous inline version of this hook).

cmd=$(jq -r '.tool_input.command // ""')
echo "$cmd" | grep -qE '^git (commit|add)' || exit 0
git diff --cached --name-only 2>/dev/null | grep -qE '(Migrations/|Entities/|DbContext)' || exit 0

dir="$(dirname "${BASH_SOURCE[0]}")"
jq -n --rawfile ctx "$dir/db-migration-checklist.txt" \
  '{hookSpecificOutput:{hookEventName:"PreToolUse",additionalContext:$ctx}}'
exit 0
