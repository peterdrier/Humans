#!/usr/bin/env bash
# block-data-migration-hook.sh — PreToolUse hook (Write|Edit|MultiEdit).
#
# Hard-blocks authoring an EF *data* migration: a migrationBuilder.Sql(...) call
# carrying UPDATE/INSERT/DELETE in a *.cs file under a Migrations/ directory.
# Schema migrations are left alone — CreateTable/AddColumn and even raw DDL via
# migrationBuilder.Sql (e.g. CREATE INDEX) pass untouched; only data-mutating SQL
# (UPDATE/INSERT/DELETE) trips the block.
#
# Rationale: HARD RULE — an LLM must never author data migrations. Backfilling or
# transforming user data requires guessing prior state and produces
# audit-indefensible rows (UpdateSource="DataMigration"). Lazy-seed on first
# interaction instead; if a genuine backfill is truly required, stop and ask Peter.
#
# This is structural enforcement, not a reminder. There is no bypass.

set -euo pipefail

INPUT=$(cat)

TOOL=$(echo "$INPUT" | jq -r '.tool_name // ""')
case "$TOOL" in
  Write|Edit|MultiEdit) ;;
  *) exit 0 ;;
esac

FILE=$(echo "$INPUT" | jq -r '.tool_input.file_path // ""')

# Only C# files inside a Migrations/ directory (handles both / and \ separators).
echo "$FILE" | grep -qE '([/\\])Migrations[/\\].*\.cs$' || exit 0

# New text this call would introduce, across the Write/Edit/MultiEdit shapes:
#   Write     -> .tool_input.content
#   Edit      -> .tool_input.new_string
#   MultiEdit -> .tool_input.edits[].new_string
CONTENT=$(echo "$INPUT" | jq -r '[.tool_input.content, .tool_input.new_string, (.tool_input.edits[]?.new_string)] | map(select(. != null)) | join("\n")')

# Must add a raw-SQL call ...
echo "$CONTENT" | grep -qE 'migrationBuilder\.Sql\s*\(' || exit 0
# ... carrying a data-mutation keyword (case-insensitive, word-bounded).
echo "$CONTENT" | grep -qiE '\b(UPDATE|INSERT|DELETE)\b' || exit 0

REASON='BLOCKED (hard rule, no bypass): this writes an EF *data* migration — a migrationBuilder.Sql(...) containing UPDATE/INSERT/DELETE in a Migrations/*.cs file. An LLM must NEVER author data migrations: backfilling or transforming user data guesses prior state and yields audit-indefensible rows. Schema changes (including raw DDL such as CREATE INDEX) are fine — data-mutating SQL is not. Do this instead: (1) lazy-seed / transform on first interaction in application code, or (2) if a genuine backfill is truly required, STOP and ask Peter — do not write it yourself.'

jq -nc --arg r "$REASON" '{
  hookSpecificOutput: {
    hookEventName: "PreToolUse",
    permissionDecision: "deny",
    permissionDecisionReason: $r
  }
}'
exit 0
