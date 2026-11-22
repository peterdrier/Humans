#!/bin/bash
# Regenerate the section-level Mermaid graph in docs/architecture/dependency-graph.md
# from the <ProjectReference> items of src/Sections/*/*.csproj.
#
# Usage: bash docs/scripts/generate-dependency-graph.sh [--check]
#   --check  write nothing; exit 1 if the doc is out of date.
#
# A Humans.<X>.Contracts project folds into section X. Humans.Base (referenced by
# every section) and Humans.Web (references every section) are left out: an edge
# to or from them says nothing about coupling.

set -euo pipefail

DOC="docs/architecture/dependency-graph.md"
BEGIN="<!-- BEGIN GENERATED: bash docs/scripts/generate-dependency-graph.sh -->"
END="<!-- END GENERATED -->"

section_of() { local n="${1#Humans.}"; echo "${n%.Contracts}"; }

nodes=()
edges=()
for proj in src/Sections/*/*.csproj; do
  name="$(basename "$proj" .csproj)"
  from="$(section_of "$name")"
  nodes+=("$from")
  while IFS= read -r ref; do
    [ "$ref" = "Humans.Base" ] && continue
    to="$(section_of "$ref")"
    [ "$to" = "$from" ] && continue
    kind=impl
    [[ "$ref" == *.Contracts ]] && kind=contracts
    edges+=("$from $to $kind")
  done < <(grep -oE '<ProjectReference Include="[^"]+"' "$proj" | sed -E 's/.*[\\/]([^\\/]+)\.csproj"$/\1/')
done

generate() {
  echo "$BEGIN"
  echo
  echo '```mermaid'
  echo 'graph LR'
  printf '%s\n' "${nodes[@]}" | sort -u | sed 's/^/    /'
  # A section pair is solid if any reference reaches the implementation project.
  printf '%s\n' "${edges[@]}" | sort -u | awk '
    { key = $1 " " $2; if (!(key in kind) || $3 == "impl") kind[key] = $3 }
    END { for (k in kind) { split(k, p, " "); print "    " p[1] (kind[k] == "impl" ? " --> " : " -.-> ") p[2] } }' | sort
  echo '```'
  echo
  echo "$END"
}

if ! grep -qxF "$BEGIN" "$DOC" || ! grep -qxF "$END" "$DOC"; then
  echo "FAIL [dependency-graph]: $DOC is missing the generated-block markers" >&2
  exit 1
fi

tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT
{
  sed -n "1,/^$(printf '%s' "$BEGIN" | sed 's/[][\.*^$/]/\\&/g')\$/p" "$DOC" | sed '$d'
  generate
  sed -n "/^$(printf '%s' "$END" | sed 's/[][\.*^$/]/\\&/g')\$/,\$p" "$DOC" | sed '1d'
} > "$tmp"

if [ "${1:-}" = "--check" ]; then
  if cmp -s "$tmp" "$DOC"; then
    echo "PASS [dependency-graph]: graph matches the csproj references"
    exit 0
  fi
  echo "FAIL [dependency-graph]: stale; run bash docs/scripts/generate-dependency-graph.sh"
  exit 1
fi

cp "$tmp" "$DOC"
