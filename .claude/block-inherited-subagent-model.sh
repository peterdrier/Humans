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

# No model on the call: does the agent definition pin one? Candidates in priority order:
# project, cwd, then user definitions (bare names only), then plugin agents -- the active
# installPath from installed_plugins.json (local, then project, then user scope, as settings
# precedence), then, when the registry is empty or silent, the newest cached version and the
# marketplace checkout.
# A "plugin:name" type looks only in that plugin. Plugin files may be absent (fresh machine,
# cloud session); a missing registry or unmatched glob yields nothing.
agent_defs() {
  local plugin="$1" name="$2" root="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/plugins"
  if [[ -z "$plugin" ]]; then
    printf '%s\n' "$CLAUDE_PROJECT_DIR/.claude/agents/$name.md" "$CWD/.claude/agents/$name.md" "$HOME/.claude/agents/$name.md"
    plugin='*'
  fi
  jq -r --arg p "$plugin" --arg proj "$CLAUDE_PROJECT_DIR" --arg n "$name" '
    [(.plugins // {}) | to_entries[] | select($p == "*" or (.key | split("@")[0]) == $p)
      | .value | if type == "array" then .[] else . end | objects
      | select(.scope == "user" or .scope == null or .projectPath == $proj)]
    | sort_by({local: 0, project: 1}[.scope // "user"] // 2) | .[] | .installPath // empty | "\(.)/agents/\($n).md"
  ' "$root/installed_plugins.json" 2>/dev/null || true
  shopt -s nullglob
  local cached=("$root"/cache/*/$plugin/*/agents/"$name".md) market=("$root"/marketplaces/*/plugins/$plugin/agents/"$name".md)
  shopt -u nullglob
  (( ${#cached[@]} )) && printf '%s\n' "${cached[@]}" | sort -rV
  (( ${#market[@]} )) && printf '%s\n' "${market[@]}"
  return 0
}

PLUGIN=""
NAME="$TYPE"
if [[ "$TYPE" == *:* ]]; then
  PLUGIN="${TYPE%%:*}"
  NAME="${TYPE#*:}"
fi
if [[ -n "$NAME" && "$NAME$PLUGIN" != */* && "$NAME$PLUGIN" != *..* && "$NAME$PLUGIN" != *[*?[]* && "$NAME" != *:* ]]; then
  while IFS= read -r DEF; do
    [[ -f "$DEF" ]] || continue
    DEF_MODEL=$(head -20 "$DEF" | tr -d '\r' | grep -m1 -E '^model:' | sed -E 's/^model:[[:space:]]*//; s/[[:space:]]*$//' || true)
    [[ "$DEF_MODEL" == "inherit" ]] && block "agent type '$TYPE' is defined with model: inherit."
    [[ -n "$DEF_MODEL" ]] && exit 0
    break
  done < <(agent_defs "$PLUGIN" "$NAME")
fi

block "this spawn names no model, so it would not run on a tier anyone chose."
