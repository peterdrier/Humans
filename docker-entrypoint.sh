#!/bin/bash
# If COOLIFY_PR_ID is set (preview environment), override the connection string
# to use the PR-specific database instead of the QA database.
if [ -n "$COOLIFY_PR_ID" ] && [ -n "$DB_PASSWORD" ]; then
  export ConnectionStrings__DefaultConnection="Host=humans-db;Database=humans_pr_${COOLIFY_PR_ID};Username=humans;Password=${DB_PASSWORD}"
fi

exec dotnet Humans.Web.dll
