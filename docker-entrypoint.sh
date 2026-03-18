#!/bin/bash
# If this is a Coolify preview deployment, override the connection string
# to use the PR-specific database instead of the QA database.
# Extract PR number from COOLIFY_CONTAINER_NAME (e.g., "xxx-pr-5" → "5")
# or COOLIFY_FQDN (e.g., "5.n.burn.camp" → "5").
if [ -n "$COOLIFY_CONTAINER_NAME" ] && [ -n "$DB_PASSWORD" ]; then
  PR_ID=$(echo "$COOLIFY_CONTAINER_NAME" | grep -oP 'pr-\K[0-9]+$')
  if [ -n "$PR_ID" ]; then
    export ConnectionStrings__DefaultConnection="Host=humans-db;Database=humans_pr_${PR_ID};Username=humans;Password=${DB_PASSWORD}"
  fi
fi

exec dotnet Humans.Web.dll
