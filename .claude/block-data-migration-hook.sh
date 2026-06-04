#!/usr/bin/env bash
# block-data-migration-hook.sh — PreToolUse hook.
#
# Hard-blocks authoring an EF *data* migration — a migrationBuilder.Sql(...) call
# carrying UPDATE/INSERT/DELETE in a *.cs file under a Migrations/ directory — at
# two enforcement points:
#   * Write|Edit|MultiEdit : the text this tool call would land in the file.
#   * Bash `git commit`    : the staged/committed Migrations/*.cs content. This is
#                            the backstop that also catches files produced by
#                            cat/tee/sed/python/etc., which never touch the
#                            Write/Edit tools.
#
# Schema migrations are left alone — CreateTable/AddColumn and even raw DDL via
# migrationBuilder.Sql (e.g. CREATE INDEX) pass through; only data-mutating SQL
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

GUIDANCE='An LLM must NEVER author data migrations: backfilling or transforming user data guesses prior state and yields audit-indefensible rows. Schema changes (including raw DDL such as CREATE INDEX) are fine — data-mutating SQL is not. Do this instead: (1) lazy-seed / transform on first interaction in application code, or (2) if a genuine backfill is truly required, STOP and ask Peter — do not write it yourself.'

# True when a blob carries a data-mutating raw-SQL migration call: a
# migrationBuilder.Sql( call AND a word-bounded UPDATE/INSERT/DELETE somewhere in
# the blob. Conservative by design (any DML keyword + any Sql() call trips it),
# which errs toward blocking — the intended bias for this guard.
is_data_migration() {
  printf '%s' "$1" | grep -qE 'migrationBuilder\.Sql\s*\(' \
    && printf '%s' "$1" | grep -qiE '\b(UPDATE|INSERT|DELETE)\b'
}

deny() {
  jq -nc --arg r "$1" '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"deny",permissionDecisionReason:$r}}'
  exit 0
}

case "$TOOL" in
  Write|Edit|MultiEdit)
    FILE=$(echo "$INPUT" | jq -r '.tool_input.file_path // ""')

    # Only C# files inside a Migrations/ directory (handles / and \ separators).
    echo "$FILE" | grep -qE '([/\\])Migrations[/\\].*\.cs$' || exit 0

    # New text this call would introduce: Write -> content; Edit -> new_string;
    # MultiEdit -> edits[].new_string.
    NEW=$(echo "$INPUT" | jq -r '[.tool_input.content, .tool_input.new_string, (.tool_input.edits[]?.new_string)] | map(select(. != null)) | join("\n")')

    # Edit/MultiEdit carry only a fragment, so the Sql( call and the DML keyword
    # can be split across the existing file and the edit — e.g. add an empty
    # Sql("") in one edit, then fill it with UPDATE in a second whose new_string
    # no longer mentions Sql(. Fold in the on-disk file so that split cannot slip
    # through. The pre-edit file over-approximates the post-edit result (an edit
    # only adds tokens to this scan, never hides one), so there are no false
    # negatives. Write replaces the whole file, so its content is self-complete.
    EXISTING=""
    if [ "$TOOL" != "Write" ]; then
      FILE_FS=$(printf '%s' "$FILE" | tr '\\' '/')
      if [ -f "$FILE_FS" ]; then
        EXISTING=$(cat "$FILE_FS")
      fi
    fi
    CONTENT=$(printf '%s\n%s' "$EXISTING" "$NEW")

    is_data_migration "$CONTENT" || exit 0
    deny "BLOCKED (hard rule, no bypass): this writes an EF *data* migration — a migrationBuilder.Sql(...) containing UPDATE/INSERT/DELETE in a Migrations/*.cs file. $GUIDANCE"
    ;;

  Bash)
    CMD=$(echo "$INPUT" | jq -r '.tool_input.command // ""')

    # Backstop for non-Write authoring paths: gate the commit, where the content
    # to be shipped is final regardless of how it was produced. (Optional `-C
    # <path>` is honoured because the permission allowlist permits `git -C`.)
    echo "$CMD" | grep -qE '\bgit([[:space:]]+-C[[:space:]]+[^[:space:]]+)?[[:space:]]+commit\b' || exit 0

    # -a/-am/--all commits tracked working-tree edits → diff HEAD + read worktree;
    # a plain commit ships only the index → diff --cached + read the staged blob.
    # (Mirrors .claude/razor-lint-hook.sh's mode selection.)
    if echo "$CMD" | grep -qE '(\s-[a-zA-Z]*a[a-zA-Z]*(\s|$|"))|(\s--all(\s|=|$|"))'; then
      files=$(git diff HEAD --name-only 2>/dev/null | grep -E '(^|/)Migrations/.*\.cs$' || true)
      staged=0
    else
      files=$(git diff --cached --name-only 2>/dev/null | grep -E '(^|/)Migrations/.*\.cs$' || true)
      staged=1
    fi
    [ -n "$files" ] || exit 0

    hits=""
    while IFS= read -r f; do
      [ -n "$f" ] || continue
      if [ "$staged" -eq 1 ]; then
        body=$(git show ":$f" 2>/dev/null || true)
      else
        body=$(cat "$f" 2>/dev/null || true)
      fi
      if is_data_migration "$body"; then
        hits="$hits $f"
      fi
    done <<< "$files"
    [ -n "$hits" ] || exit 0

    deny "BLOCKED (hard rule, no bypass): this commit would ship an EF *data* migration —$hits contains a migrationBuilder.Sql(...) with UPDATE/INSERT/DELETE. $GUIDANCE"
    ;;

  *)
    exit 0
    ;;
esac
