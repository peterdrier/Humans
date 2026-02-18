#!/usr/bin/env bash
# QA deployment script for the NUC.
# Sets SOURCE_COMMIT so the footer shows the git hash, then rebuilds and starts.
#
# Usage:
#   ./deploy-qa.sh          # pull, rebuild, and restart
#   ./deploy-qa.sh --no-pull # rebuild without pulling (for local testing)

set -euo pipefail
cd "$(dirname "$0")"

if [[ "${1:-}" != "--no-pull" ]]; then
    git pull --ff-only
fi

export SOURCE_COMMIT
SOURCE_COMMIT=$(git rev-parse --short HEAD)

docker compose up --build -d
echo "Deployed $SOURCE_COMMIT to QA"
