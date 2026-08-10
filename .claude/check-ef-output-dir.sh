#!/usr/bin/env bash
# PreToolUse(Bash) guard: `dotnet ef migrations ... --output-dir <dir>` must name one of the
# two sanctioned locations from memory/process/ef-multi-context-commands.md. Generating a
# section's migrations anywhere else splits them from their snapshot silently.
#
#   peeled, context still in Humans.Infrastructure -> --output-dir Migrations/<Section>
#                                                     --project src/Humans.Infrastructure
#   section at G5, own project (#866)              -> --output-dir Data/Migrations
#                                                     --project src/Sections/Humans.<Section>
#
# Exit 1 blocks the command. Anything without --output-dir is none of this hook's business.
set -uo pipefail

cmd=$(jq -r '.tool_input.command // ""')

case "$cmd" in
  *"dotnet ef migrations"*) ;;
  *) exit 0 ;;
esac
case "$cmd" in
  *--output-dir*) ;;
  *) exit 0 ;;
esac

# Quoting is harmless to EF — the shell strips it before dotnet sees the value — so strip it
# here too, or a legitimate `--output-dir "Data/Migrations"` fails the exact match below.
unquote() { sed -E 's/^["'"'"']//; s/["'"'"']$//'; }

outdir=$(printf '%s' "$cmd" | grep -oE -- '--output-dir[= ]+("[^"]*"|'"'"'[^'"'"']*'"'"'|[^ ]+)' | head -1 | sed -E 's/--output-dir[= ]+//' | unquote)
project=$(printf '%s' "$cmd" | grep -oE -- '--project[= ]+("[^"]*"|'"'"'[^'"'"']*'"'"'|[^ ]+)'    | head -1 | sed -E 's/--project[= ]+//'    | unquote)

# G5: section owns its project; the project already scopes the folder, so no per-section subdir.
if printf '%s' "$project" | grep -qE 'src/Sections/Humans\.[A-Za-z0-9.]+/?$' \
   && printf '%s' "$outdir" | grep -qE '^Data/Migrations/?$'; then
  exit 0
fi

# Peeled but not yet moved: migrations sit beside the main pile in their own named subfolder.
if printf '%s' "$project" | grep -qE 'src/Humans\.Infrastructure/?$' \
   && printf '%s' "$outdir" | grep -qE '^Migrations/[A-Za-z0-9]+/?$'; then
  exit 0
fi

cat <<EOF
BLOCKED: --output-dir "$outdir" with --project "$project" is not a sanctioned combination.

Per memory/process/ef-multi-context-commands.md, exactly two shapes are valid:

  peeled, still in Humans.Infrastructure:
    --output-dir Migrations/<Section>  --project src/Humans.Infrastructure
  section at G5, in its own project:
    --output-dir Data/Migrations       --project src/Sections/Humans.<Section>

Generating elsewhere separates a section's migrations from its snapshot, which does not
fail the build — it fails later, as unexplained model drift.
EOF
exit 1
