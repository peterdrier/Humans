#!/usr/bin/env bash
# Guardrail: two hand-maintained lists that silently skip a new section if
# forgotten. Both used to fail quiet — this makes them fail loud.
#
# 1. slnx completeness: every *.csproj under src/ and tests/ must be listed
#    in Humans.slnx. A project missing from the slnx gets no build, no test,
#    no format check in CI, and no error.
# 2. SECTION_DB_CONTEXTS completeness: build.yml's MAIN_DB_CONTEXT +
#    SECTION_DB_CONTEXTS env pairs must list every DbContext-derived class in
#    the codebase's own Data folders. Discovery is a filename heuristic keyed
#    to the *DbContext.cs naming convention (real class declarations here use
#    primary constructors with the base type — e.g. IdentityDbContext<...> —
#    on a separate line, so a reliable declaration-based grep isn't
#    practical). Kept discovery-driven on purpose: when HumansDbContext is
#    deleted (nobodies-collective/Humans#866), this check needs no edit.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT_DIR"

FAIL=0

# --- Check 1: every src/ and tests/ csproj is listed in Humans.slnx ---

MISSING_PROJECTS=()
while IFS= read -r -d '' csproj; do
  rel="${csproj#./}"
  rel="${rel//\\//}"
  if ! grep -qi "Path=\"${rel}\"" Humans.slnx; then
    MISSING_PROJECTS+=("$rel")
  fi
done < <(find src tests -name '*.csproj' -print0 | sort -z)

if [[ ${#MISSING_PROJECTS[@]} -gt 0 ]]; then
  echo "::error::Project(s) on disk missing from Humans.slnx (no build/test/format in CI):" >&2
  printf '  %s\n' "${MISSING_PROJECTS[@]}" >&2
  FAIL=1
else
  echo "ok: every src/ and tests/ csproj is listed in Humans.slnx."
fi

# --- Check 2: SECTION_DB_CONTEXTS (+ MAIN_DB_CONTEXT) lists every DbContext ---

BUILD_YML=".github/workflows/build.yml"

DISCOVERED_CONTEXTS=$(find src/Sections/*/Data src/Humans.Infrastructure/Data -maxdepth 1 -name '*DbContext.cs' 2>/dev/null \
  | xargs -n1 basename | sed 's/\.cs$//' | sort -u)

# Context names appear as `FooDbContext:<project-path>` tokens inside the env
# block only (between `env:` and `jobs:`), both in MAIN_DB_CONTEXT's single
# pair and in SECTION_DB_CONTEXTS' multi-line list.
LISTED_CONTEXTS=$(awk '/^env:/{p=1} /^jobs:/{p=0} p' "$BUILD_YML" \
  | grep -oE '[A-Za-z][A-Za-z0-9]*DbContext:' | tr -d ':' | sort -u)

MISSING_CONTEXTS=()
while IFS= read -r ctx; do
  [[ -z "$ctx" ]] && continue
  if ! grep -qx "$ctx" <<< "$LISTED_CONTEXTS"; then
    MISSING_CONTEXTS+=("$ctx")
  fi
done <<< "$DISCOVERED_CONTEXTS"

if [[ ${#MISSING_CONTEXTS[@]} -gt 0 ]]; then
  echo "::error::DbContext(s) found in Data/ but missing from MAIN_DB_CONTEXT/SECTION_DB_CONTEXTS in $BUILD_YML:" >&2
  printf '  %s\n' "${MISSING_CONTEXTS[@]}" >&2
  FAIL=1
else
  echo "ok: every discovered DbContext is listed in MAIN_DB_CONTEXT/SECTION_DB_CONTEXTS."
fi

exit "$FAIL"
