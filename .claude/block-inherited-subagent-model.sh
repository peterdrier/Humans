#!/usr/bin/env bash
# PreToolUse hook: block subagent spawns that would inherit the session model.
# A spawn passes when the call names a model, or its agent definition pins one.
# "inherit" (on the call or in the definition) and fork both count as inheriting.
set -e

INPUT=$(cat)

TOOL=$(echo "$INPUT" | jq -r '.tool_name')
[[ "$TOOL" == "Agent" || "$TOOL" == "Task" ]] || exit 0

MODEL=$(echo "$INPUT" | jq -r '.tool_input.model // ""')
TYPE=$(echo "$INPUT" | jq -r '.tool_input.subagent_type // ""')
CWD=$(echo "$INPUT" | jq -r '.cwd // ""')

HOW='Pass an explicit model (haiku | sonnet | opus | fable) or use an agent type that pins one (orch-*). Tier guide: routing.md in the orch skill.'

block() {
  jq -n --arg r "BLOCKED: $1 $HOW" '{decision: "block", reason: $r}'
  exit 0
}

[[ "$TYPE" == "fork" ]] && block "fork always inherits the session model; a model override is ignored."
[[ "$MODEL" == "inherit" ]] && block "model: inherit runs this subagent on the session model."
[[ -n "$MODEL" ]] && exit 0

# No model on the call: does the agent definition pin one? Project definitions win over user ones.
if [[ -n "$TYPE" && "$TYPE" != */* && "$TYPE" != *..* ]]; then
  for DIR in "$CLAUDE_PROJECT_DIR/.claude/agents" "$CWD/.claude/agents" "$HOME/.claude/agents"; do
    DEF="$DIR/$TYPE.md"
    [[ -f "$DEF" ]] || continue
    DEF_MODEL=$(head -20 "$DEF" | tr -d '\r' | grep -m1 -E '^model:' | sed -E 's/^model:[[:space:]]*//; s/[[:space:]]*$//' || true)
    [[ "$DEF_MODEL" == "inherit" ]] && block "agent type '$TYPE' is defined with model: inherit."
    [[ -n "$DEF_MODEL" ]] && exit 0
    break
  done
fi

block "this spawn names no model, so it would not run on a tier anyone chose."
