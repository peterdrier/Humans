#!/bin/bash
# If COOLIFY_PR_ID is set (or derivable from a preview container name/FQDN),
# override the connection string to use the PR-specific database instead of the QA
# database.
PR_ID="${COOLIFY_PR_ID}"

if [ -z "$PR_ID" ] && [ -n "$COOLIFY_CONTAINER_NAME" ]; then
  PR_ID=$(echo "$COOLIFY_CONTAINER_NAME" | grep -oP 'pr-\K[0-9]+$')
fi

if [ -z "$PR_ID" ] && [ -n "$COOLIFY_FQDN" ]; then
  PR_ID=$(echo "$COOLIFY_FQDN" | awk -F. '{print $1}' | grep -Eo '^[0-9]+$')
fi

if [ -n "$PR_ID" ] && [ -n "$DB_PASSWORD" ]; then
  export ConnectionStrings__DefaultConnection="Host=humans-db;Database=humans_pr_${PR_ID};Username=humans;Password=${DB_PASSWORD}"
fi

exec dotnet Humans.Web.dll
