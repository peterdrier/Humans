#!/usr/bin/env bash
# The claude-review reviewer's only diff. The range is fixed by the workflow
# (PR_MERGE_BASE, PR_HEAD: the two fetched commits), and only a short list
# of options is accepted, so an injected PR cannot turn this into
# `git diff --no-index <any file>` or `git diff --output=<path>`. Pathspecs
# are fine: a tree-to-tree diff never reads outside the object store.
#
#   .claude/pr-diff.sh [--stat|--name-only|--name-status] [-- <pathspec>...]
set -euo pipefail
: "${PR_MERGE_BASE:?}" "${PR_HEAD:?}"
opts=() paths=() seen_dashdash=
for a in "$@"; do
  if [[ -n "$seen_dashdash" ]]; then
    paths+=("$a")
  elif [[ "$a" == -- ]]; then
    seen_dashdash=1
  else
    case "$a" in
      --stat|--name-only|--name-status) opts+=("$a") ;;
      *) echo "pr-diff: not allowed: $a (options: --stat --name-only --name-status; paths after --)" >&2; exit 2 ;;
    esac
  fi
done
exec git diff "${opts[@]}" "$PR_MERGE_BASE" "$PR_HEAD" -- "${paths[@]}"
