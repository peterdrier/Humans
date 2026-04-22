#!/usr/bin/env bash
# Guardrail: enforce that google_resources is accessed only through
# TeamResourceService (the owning service), per docs/architecture/design-rules.md.
#
# This script fails if any file under src/ touches DbSet<GoogleResource>
# or _dbContext.GoogleResources / dbContext.GoogleResources / db.GoogleResources
# outside the allowlisted owner files. Migration files and the DbContext
# itself are always allowed.
#
# Phase 2 exceptions: GoogleWorkspaceSyncService, SystemTeamSyncJob,
# ProcessGoogleSyncOutboxJob, and GoogleController still touch the table
# directly — they are tracked as temporary exceptions while the remainder
# of the Google sync service is decomposed. Remove entries from
# PHASE2_EXCEPTIONS as each caller migrates to ITeamResourceService reads.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT_DIR"

# Files that are allowed to touch the DbSet<GoogleResource> directly.
# After the §15 Teams sub-task #540c migration the only owner is the
# Application-layer service's repository in Humans.Infrastructure.Repositories.
ALLOWED=(
  "src/Humans.Infrastructure/Repositories/GoogleResourceRepository.cs"
  "src/Humans.Infrastructure/Data/Configurations/TeamConfiguration.cs"
  "src/Humans.Infrastructure/Data/HumansDbContext.cs"
)

# Phase 2: still-pending migrations to services that own writes here.
# Track the upstream issue before adding anything to this list.
PHASE2_EXCEPTIONS=(
  "src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs"
  "src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs"
  "src/Humans.Infrastructure/Jobs/ProcessGoogleSyncOutboxJob.cs"
  "src/Humans.Web/Controllers/GoogleController.cs"
)

build_exclude_args() {
  local args=()
  for f in "${ALLOWED[@]}" "${PHASE2_EXCEPTIONS[@]}"; do
    args+=(":(exclude)$f")
  done
  printf '%s\n' "${args[@]}"
}

mapfile -t EXCLUDE_ARGS < <(build_exclude_args)

# Dangerous patterns: direct DbSet<GoogleResource> access through a DbContext
# variable. We allow view-model/property/route names that also happen to
# contain the word "GoogleResources" — those never look like a DbContext
# member access.
PATTERN='(DbSet<GoogleResource>|(_dbContext|dbContext|db|_db)\.GoogleResources)'

MATCHES=$(git grep -n -E "$PATTERN" -- 'src/**' "${EXCLUDE_ARGS[@]}" || true)

if [[ -n "$MATCHES" ]]; then
  echo "error: google_resources direct access outside TeamResourceService:" >&2
  echo "$MATCHES" >&2
  echo >&2
  echo "google_resources is owned by TeamResourceService (which reads/writes" >&2
  echo "through IGoogleResourceRepository). Use one of the service's read" >&2
  echo "methods (GetTeamResourcesAsync, GetResourcesByTeamIdsAsync," >&2
  echo "GetTeamResourceSummariesAsync, GetActiveResourceCountsByTeamAsync," >&2
  echo "GetUserTeamResourcesAsync, GetActiveDriveFoldersAsync, GetResourceCountAsync)" >&2
  echo "instead of reaching into the DbSet directly. See docs/architecture/design-rules.md." >&2
  exit 1
fi

echo "ok: google_resources access is confined to TeamResourceService."
