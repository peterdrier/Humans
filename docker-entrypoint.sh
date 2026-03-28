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

# Preview environments: make QA uploaded images visible.
# Coolify mounts QA uploads read-only at /app/wwwroot/uploads-qa.
# Create a writable uploads dir and symlink QA content so existing images
# display correctly while new uploads go to the ephemeral container filesystem.
if [ -n "$PR_ID" ] && [ -d "/app/wwwroot/uploads-qa" ]; then
  mkdir -p /app/wwwroot/uploads
  cp -rs /app/wwwroot/uploads-qa/* /app/wwwroot/uploads/ 2>/dev/null || true
fi

exec dotnet Humans.Web.dll
