#!/bin/bash
# Generate historical development statistics for docs/development-stats.md
#
# Usage: cd <repo-root> && bash docs/scripts/generate-stats.sh
#
# Requirements:
#   - Clean working tree (will stash if needed)
#   - bash 4+ (for associative arrays)
#
# Output: pipe-delimited rows, one per day with commits.
# Excludes EF Core migrations. Separates test code from app code.
#
# Columns:
#   Date | App Lines | Test Lines | Total Lines | CS Lines | CSHTML Lines |
#   RESX Lines | JS Lines | App Bytes | Test Bytes | Files | Classes |
#   Interfaces | Controllers | Views | Entities | Resx Keys |
#   Commits (cumulative) | Day +Lines | Day -Lines

set -euo pipefail

ORIG_REF=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "main")
NEEDS_STASH=false

# Stash if dirty
if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
  git stash --quiet
  NEEDS_STASH=true
fi

cleanup() {
  git checkout --quiet "$ORIG_REF" 2>/dev/null || true
  if $NEEDS_STASH; then
    git stash pop --quiet 2>/dev/null || true
  fi
}
trap cleanup EXIT

# 1) Day→last-commit map
declare -A day_commit
while read -r hash day; do
  day_commit["$day"]="$hash"
done < <(git log --reverse --format="%H %ad" --date=format:"%Y-%m-%d")
mapfile -t days < <(printf '%s\n' "${!day_commit[@]}" | sort)

# 2) Daily adds/dels via awk (one pass over full history)
tmpfile=$(mktemp)
git log --format="COMMIT %ad" --date=format:"%Y-%m-%d" --numstat | awk '
  /^COMMIT / { day=$2; next }
  /Migrations/ { next }
  NF >= 3 && $1 != "-" { adds[day]+=$1; dels[day]+=$2 }
  END { for (d in adds) print d, adds[d], dels[d] }
' > "$tmpfile"

declare -A day_adds day_dels
for d in "${days[@]}"; do day_adds["$d"]=0; day_dels["$d"]=0; done
while read -r d a dl; do
  day_adds["$d"]=$a
  day_dels["$d"]=$dl
done < "$tmpfile"
rm -f "$tmpfile"

# 3) Cumulative commits per day
declare -A cum_commits
total=0
while read -r hash day; do
  total=$((total + 1))
  cum_commits["$day"]=$total
done < <(git log --reverse --format="%H %ad" --date=format:"%Y-%m-%d")

# 4) Header
echo "Date|App Lines|Test Lines|Total Lines|CS Lines|CSHTML Lines|RESX Lines|JS Lines|App Bytes|Test Bytes|Files|Classes|Interfaces|Controllers|Views|Entities|Resx Keys|Commits|Day +Lines|Day -Lines"

# 5) Snapshot each day
for day in "${days[@]}"; do
  commit="${day_commit[$day]}"
  git checkout --quiet "$commit"

  # App code (exclude Migrations and Tests)
  cs_data=$(find src -type f -name '*.cs' ! -path '*/Migrations/*' ! -path '*Tests*' -print0 2>/dev/null | xargs -0 wc -l -c 2>/dev/null | tail -1 || echo "0 0")
  cshtml_data=$(find src -type f -name '*.cshtml' ! -path '*/Migrations/*' ! -path '*Tests*' -print0 2>/dev/null | xargs -0 wc -l -c 2>/dev/null | tail -1 || echo "0 0")
  resx_data=$(find src -type f -name '*.resx' ! -path '*/Migrations/*' ! -path '*Tests*' -print0 2>/dev/null | xargs -0 wc -l -c 2>/dev/null | tail -1 || echo "0 0")
  js_data=$(find src -type f -name '*.js' ! -path '*/Migrations/*' ! -path '*Tests*' -print0 2>/dev/null | xargs -0 wc -l -c 2>/dev/null | tail -1 || echo "0 0")

  cs_lines=$(echo "$cs_data" | awk '{print $1+0}')
  cshtml_lines=$(echo "$cshtml_data" | awk '{print $1+0}')
  resx_lines=$(echo "$resx_data" | awk '{print $1+0}')
  js_lines=$(echo "$js_data" | awk '{print $1+0}')

  app_lines=$((cs_lines + cshtml_lines + resx_lines + js_lines))
  app_bytes=$(( $(echo "$cs_data" | awk '{print $2+0}') + $(echo "$cshtml_data" | awk '{print $2+0}') + $(echo "$resx_data" | awk '{print $2+0}') + $(echo "$js_data" | awk '{print $2+0}') ))

  # Test code
  test_data=$(find . -type f -name '*.cs' -path '*Tests*' ! -path '*/Migrations/*' -print0 2>/dev/null | \
    xargs -0 wc -l -c 2>/dev/null | tail -1 || echo "0 0")
  test_lines=$(echo "$test_data" | awk '{print $1+0}')
  test_bytes=$(echo "$test_data" | awk '{print $2+0}')

  total_lines=$((app_lines + test_lines))

  # File counts
  app_files=$(( $(find src -type f \( -name '*.cs' -o -name '*.cshtml' -o -name '*.resx' -o -name '*.js' \) \
    ! -path '*/Migrations/*' ! -path '*Tests*' 2>/dev/null | wc -l) ))
  test_files=$(find . -type f -name '*.cs' -path '*Tests*' ! -path '*/Migrations/*' 2>/dev/null | wc -l)

  # Classes + records
  classes=$(grep -rE '^\s*(public|internal)\s+(sealed |abstract |static |partial )*(class|record) ' \
    --include='*.cs' src/ 2>/dev/null | grep -v '/Migrations/' | grep -v 'Tests' | wc -l || echo 0)

  # Interfaces
  interfaces=$(grep -rE '^\s*public\s+interface\s' --include='*.cs' src/ 2>/dev/null | \
    grep -v '/Migrations/' | grep -v 'Tests' | wc -l || echo 0)

  # Controllers, Views, Entities, Resx keys
  controllers=$(find src -name '*Controller.cs' ! -path '*/Migrations/*' 2>/dev/null | wc -l)
  views=$(find src -name '*.cshtml' 2>/dev/null | wc -l)
  entities=$(find src -path '*/Entities/*.cs' 2>/dev/null | wc -l)
  resx_keys=$(grep -c '<data ' src/Humans.Web/Resources/SharedResource.resx 2>/dev/null || echo 0)

  echo "${day}|${app_lines}|${test_lines}|${total_lines}|${cs_lines}|${cshtml_lines}|${resx_lines}|${js_lines}|${app_bytes}|${test_bytes}|$((app_files + test_files))|${classes}|${interfaces}|${controllers}|${views}|${entities}|${resx_keys}|${cum_commits[$day]}|${day_adds[$day]:-0}|${day_dels[$day]:-0}"
done
