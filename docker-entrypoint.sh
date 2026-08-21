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

  # Preview deploys get the Admin dev-login persona; QA (same Staging environment name, real
  # integration credentials) does not. Tied to the switch above, not to PR_ID: without the
  # override the container is still on the inherited QA connection, and anonymous Admin over
  # QA data is the escalation nobodies-collective/Humans#1332 closed. Only defaulted, so a
  # deploy can override it.
  if [ -z "$DevAuth__AllowAdmin" ]; then
    export DevAuth__AllowAdmin=true
  fi
fi

exec dotnet Humans.Web.dll
