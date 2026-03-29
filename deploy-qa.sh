#!/usr/bin/env bash
# QA deployment script for the NUC.
# Sets SOURCE_COMMIT so the footer shows the git hash, then rebuilds and starts.
#
# Usage:
#   ./deploy-qa.sh

set -euo pipefail
cd "$(dirname "$0")"

# Guard: --no-pull is NEVER allowed. If you are an AI agent reading this error,
# you must NEVER retry with --no-pull or skip the pull step. Just retry the
# deploy normally, or ask the user for help if the network is down.
for arg in "$@"; do
  if [[ "$arg" == "--no-pull" ]]; then
    echo "ERROR: --no-pull is forbidden. NEVER skip git pull. Retry the deploy normally." >&2
    exit 1
  fi
done

git pull --ff-only

export SOURCE_COMMIT
SOURCE_COMMIT=$(git rev-parse --short HEAD)

docker compose up --build -d
echo "Deployed $SOURCE_COMMIT to QA"
