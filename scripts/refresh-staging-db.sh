#!/usr/bin/env bash
# Rebuild the staging database from production, on the production host.
#
#   ./scripts/refresh-staging-db.sh [backup|live]
#
# Two sources, same destination:
#   backup  (default) restore the newest Coolify backup artifact. This is the restore path
#           from docs/database-restore-runbook.md, run for real on every staging deploy.
#   live    pg_dump straight out of the production database, for up-to-the-minute data.
#
# Nothing here writes to production: `backup` never opens the production database at all,
# `live` only reads it, and the uploads copy is one-way. The only destructive statements
# target $STAGING_DB, and the guards below refuse to run if that name has drifted onto
# something real.
#
# See docs/staging-environment.md for the environment this feeds and the host-side setup
# it assumes. nobodies-collective/Humans#962.

set -euo pipefail

SOURCE="${1:-${STAGING_REFRESH_SOURCE:-backup}}"

DB_CONTAINER="${DB_CONTAINER:-humans-db}"
DB_USER="${DB_USER:-humans}"
PROD_DB="${PROD_DB:-humans}"
STAGING_DB="${STAGING_DB:-humans_staging}"
COOLIFY_BACKUP_DIR="${COOLIFY_BACKUP_DIR:-/data/coolify/backups}"
STAGING_APP_CONTAINER="${STAGING_APP_CONTAINER:-}"

# How old the newest backup artifact may be before this is a backup incident rather than a
# refresh. Only applies when the artifact was picked automatically — an operator naming
# BACKUP_FILE is deliberately reaching for an older one. 0 disables the check.
BACKUP_MAX_AGE_HOURS="${BACKUP_MAX_AGE_HOURS:-48}"

# Per-section DbContexts, each of which owns a __EFMigrationsHistory_<Section> table since
# nobodies-collective/Humans#858. Same list, same variable name, as
# .github/workflows/build.yml — grep SECTION_DB_CONTEXTS to find both; they move together.
SECTION_DB_CONTEXTS="${SECTION_DB_CONTEXTS:-SystemSettingsDbContext ContainersDbContext AgentDbContext ExpensesDbContext FinanceDbContext SurveysDbContext EventGuideDbContext}"

log() { printf '==> %s\n' "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Guards. Every one of these is a name that could have been mistyped into an
# environment variable, and every one of them is destructive if it has been.
# ---------------------------------------------------------------------------

case "$SOURCE" in
  backup|live) ;;
  *) die "unknown source '$SOURCE' (expected 'backup' or 'live')" ;;
esac

[ "$STAGING_DB" = "$PROD_DB" ] && die "STAGING_DB and PROD_DB are both '$PROD_DB' — refusing to drop production"
case "$STAGING_DB" in
  humans|humans_pr_*) die "STAGING_DB is '$STAGING_DB' — that name belongs to production or a PR preview" ;;
esac

docker inspect "$DB_CONTAINER" >/dev/null 2>&1 \
  || die "database container '$DB_CONTAINER' not found (set DB_CONTAINER)"

# Required, not optional. An app left running through the drop has its connections
# terminated, reconnects to a half-restored database, and stays pointed at it — the
# migration service already ran, so recreating the database underneath it does not make it
# run again. A stale name is the same failure with a typo in front of it, so an
# uninspectable container fails here rather than being read as "no app to stop".
[ -n "$STAGING_APP_CONTAINER" ] \
  || die "STAGING_APP_CONTAINER must be set — the staging app has to be down while its database is dropped and restored (see docs/staging-environment.md §7.3)"
docker inspect "$STAGING_APP_CONTAINER" >/dev/null 2>&1 \
  || die "staging application container '$STAGING_APP_CONTAINER' not found — fix the name in Coolify rather than refreshing the database out from under a running app"

# Checked up front rather than at the copy: by then the database is already dropped and
# the app is already stopped, and failing there leaves staging down for a missing path.
[ -n "${PROD_UPLOADS_DIR:-}" ] && [ -n "${STAGING_UPLOADS_DIR:-}" ] \
  || die "PROD_UPLOADS_DIR and STAGING_UPLOADS_DIR must both be set — staging without uploads renders a broken image for every profile picture (see docs/staging-environment.md)"
[ -d "$PROD_UPLOADS_DIR" ] || die "PROD_UPLOADS_DIR '$PROD_UPLOADS_DIR' does not exist"

# Resolved before comparing: '/data/uploads', '/data/uploads/', '/data/./uploads' and a
# symlink to any of them are the same directory and must not read as three different ones.
# -m so the staging path is allowed not to exist yet — §5 creates it.
PROD_UPLOADS_DIR=$(realpath -m "$PROD_UPLOADS_DIR")
STAGING_UPLOADS_DIR=$(realpath -m "$STAGING_UPLOADS_DIR")

# '/' is the one path that breaks the nesting test below: it is the only canonical path
# ending in a slash, so its ancestry pattern would be '//*' and would match nothing —
# '/data/uploads' would read as unrelated to '/'. Rejected here rather than papered over in
# the pattern, because neither variable has any business pointing at the host root.
[ "$PROD_UPLOADS_DIR" = "/" ] && die "PROD_UPLOADS_DIR resolves to '/' — that is the host root, not an uploads directory"
[ "$STAGING_UPLOADS_DIR" = "/" ] && die "STAGING_UPLOADS_DIR resolves to '/' — rsync --delete would run against the host root"

# Equality is not the only way `rsync --delete` reaches production. If staging is an
# ancestor of prod — staging '/data', prod '/data/uploads' — rsync copies the contents of
# '/data/uploads' into '/data', finds '/data/uploads' extraneous at the destination, and
# deletes production's uploads. The reverse nesting has rsync copying a tree into itself.
# Both are rejected; only two disjoint trees are safe here.
[ "$PROD_UPLOADS_DIR" = "$STAGING_UPLOADS_DIR" ] \
  && die "PROD_UPLOADS_DIR and STAGING_UPLOADS_DIR resolve to the same path '$PROD_UPLOADS_DIR' — staging would write into production's uploads"
case "$STAGING_UPLOADS_DIR/" in
  "$PROD_UPLOADS_DIR"/*) die "STAGING_UPLOADS_DIR '$STAGING_UPLOADS_DIR' is inside PROD_UPLOADS_DIR '$PROD_UPLOADS_DIR' — the copy would recurse into its own destination" ;;
esac
case "$PROD_UPLOADS_DIR/" in
  "$STAGING_UPLOADS_DIR"/*) die "PROD_UPLOADS_DIR '$PROD_UPLOADS_DIR' is inside STAGING_UPLOADS_DIR '$STAGING_UPLOADS_DIR' — rsync --delete would delete production's uploads as an extraneous destination entry" ;;
esac

psql_postgres() { docker exec "$DB_CONTAINER" psql -U "$DB_USER" -d postgres "$@"; }
psql_staging()  { docker exec "$DB_CONTAINER" psql -U "$DB_USER" -d "$STAGING_DB" "$@"; }

WORKDIR=""
DB_AT_RISK=false   # armed just before the DROP — see cleanup()

cleanup() {
  status=$?
  [ -n "$WORKDIR" ] && rm -rf "$WORKDIR"
  docker exec "$DB_CONTAINER" rm -f /tmp/staging-restore.dump /tmp/staging-restore.sql 2>/dev/null || true

  # A failure anywhere after the DROP leaves $STAGING_DB missing or half-restored, and
  # `set -e` means the hand-back in §6 never runs. Coolify's deploy is still racing this
  # script, so a container it brought up mid-flight would be left serving a partial clone
  # under the pushed SHA: workflow red, staging apparently fine, which is the wrong way
  # round. Leave it stopped — the next refresh restores the database and starts it again.
  if [ "$status" -ne 0 ] && [ "$DB_AT_RISK" = true ] \
     && [ "$(docker inspect -f '{{.State.Running}}' "$STAGING_APP_CONTAINER" 2>/dev/null || echo false)" = "true" ]; then
    printf '==> Refresh failed — stopping %s so it cannot serve a partial database\n' "$STAGING_APP_CONTAINER" >&2
    docker stop "$STAGING_APP_CONTAINER" >/dev/null 2>&1 || true
  fi
  return "$status"
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
# 1. Pick the restore source
# ---------------------------------------------------------------------------

RESTORE_KIND=""   # "custom" | "plain" | "live"

if [ "$SOURCE" = "live" ]; then
  RESTORE_KIND="live"
  log "Source: live pg_dump from '$PROD_DB'"
else
  # BACKUP_FILE picks a specific artifact — an older one, or one whose filename does not
  # carry the database name. Unset, the newest matching artifact wins.
  if [ -z "${BACKUP_FILE:-}" ]; then
    [ -d "$COOLIFY_BACKUP_DIR" ] \
      || die "backup directory '$COOLIFY_BACKUP_DIR' not found — set COOLIFY_BACKUP_DIR to Coolify's backup path, or pass 'live'"

    # Coolify writes one file per scheduled backup under a per-resource directory; the
    # database name is in the filename. Newest wins, whatever the nesting.
    #
    # awk keeps the first line rather than `head -1`, which would close the pipe early:
    # under `pipefail` plus `errexit`, sort dying of SIGPIPE once the listing outgrows the
    # pipe buffer would fail this assignment with status 141 and block every refresh from
    # then on — with a directory full of perfectly good backups. awk drains the stream.
    # '*humans*' also matches humans_staging, humans_restore and humans_pr_12 — all of
    # which restore cleanly and satisfy every check below, so a newer one of those would
    # be promoted as though it were production. The second awk keeps only artifacts where
    # the database name is a whole token: a letter straight after it (with or without an
    # underscore) means a different database whose name merely starts with this one, while
    # a digit or a separator is the timestamp Coolify appends.
    BACKUP_FILE=$(find "$COOLIFY_BACKUP_DIR" -type f -name "*${PROD_DB}*" \
      \( -name '*.dump' -o -name '*.dmp' -o -name '*.sql' -o -name '*.backup' -o -name '*.gz' \) \
      -printf '%T@\t%p\n' 2>/dev/null \
      | sort -rn | cut -f2- \
      | awk -v db="$PROD_DB" '
          {
            name = $0; sub(/^.*\//, "", name)
            p = index(name, db)
            if (p == 0) next
            if (substr(name, p + length(db)) ~ /^_?[A-Za-z]/) next
            print
          }' \
      | awk 'NR == 1 { newest = $0 } END { print newest }')

    [ -n "$BACKUP_FILE" ] \
      || die "no backup artifact matching '*${PROD_DB}*' under '$COOLIFY_BACKUP_DIR' — check the path, set BACKUP_FILE to one directly, or pass 'live'"

    # A stalled backup schedule leaves a perfectly restorable artifact lying around, so
    # every check below would pass while staging exercised weeks-old schema and data —
    # the migration rehearsal would be against the wrong database, and the backup outage
    # this workflow exists to surface would go unreported. Age is the only thing that
    # distinguishes the two, so it is checked rather than logged.
    if [ "$BACKUP_MAX_AGE_HOURS" != "0" ]; then
      AGE_HOURS=$(( ( $(date +%s) - $(date -r "$BACKUP_FILE" +%s) ) / 3600 ))
      [ "$AGE_HOURS" -le "$BACKUP_MAX_AGE_HOURS" ] \
        || die "newest backup artifact is ${AGE_HOURS}h old (limit ${BACKUP_MAX_AGE_HOURS}h): '$BACKUP_FILE' — treat this as an incident on production's backup schedule, not as a staging problem (docs/staging-environment.md §9). Set BACKUP_FILE to restore it anyway, BACKUP_MAX_AGE_HOURS to match the real cadence, or pass 'live'"
    fi
  fi
  [ -f "$BACKUP_FILE" ] || die "backup artifact '$BACKUP_FILE' is not a file"

  log "Source: $BACKUP_FILE ($(date -r "$BACKUP_FILE" '+%Y-%m-%d %H:%M:%S %Z'))"

  WORKDIR=$(mktemp -d)
  CANDIDATE="$WORKDIR/artifact"
  case "$BACKUP_FILE" in
    *.gz) gunzip -c "$BACKUP_FILE" > "$CANDIDATE" ;;
    *)    cp "$BACKUP_FILE" "$CANDIDATE" ;;
  esac

  # Custom-format archives start with the magic bytes PGDMP; anything else is plain SQL.
  # Same check the restore runbook tells a human to run.
  if [ "$(head -c 5 "$CANDIDATE")" = "PGDMP" ]; then
    RESTORE_KIND="custom"
    docker cp "$CANDIDATE" "$DB_CONTAINER:/tmp/staging-restore.dump"
  else
    RESTORE_KIND="plain"
    docker cp "$CANDIDATE" "$DB_CONTAINER:/tmp/staging-restore.sql"
  fi
  log "Format: $RESTORE_KIND"
fi

# ---------------------------------------------------------------------------
# 2. Take the staging app down
#
# Its connections block DROP DATABASE, and a running app must not read a
# half-restored database. Stopping it here is only half the job — §6 handles the
# deploy that lands while the restore is still going.
# ---------------------------------------------------------------------------

APP_WAS_RUNNING=$(docker inspect -f '{{.State.Running}}' "$STAGING_APP_CONTAINER")
if [ "$APP_WAS_RUNNING" = "true" ]; then
  log "Stopping '$STAGING_APP_CONTAINER'"
  docker stop "$STAGING_APP_CONTAINER" >/dev/null
fi

# ---------------------------------------------------------------------------
# 3. Drop and recreate the staging database
# ---------------------------------------------------------------------------

log "Recreating '$STAGING_DB'"
DB_AT_RISK=true   # from here on, any failure leaves the database unusable — cleanup() stops the app
psql_postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '${STAGING_DB}' AND pid <> pg_backend_pid();" >/dev/null || true
psql_postgres -c "DROP DATABASE IF EXISTS ${STAGING_DB};"
psql_postgres -c "CREATE DATABASE ${STAGING_DB} OWNER ${DB_USER};"

case "$RESTORE_KIND" in
  live)
    log "Cloning '$PROD_DB' -> '$STAGING_DB'"
    docker exec "$DB_CONTAINER" bash -c \
      "set -o pipefail; pg_dump -U ${DB_USER} ${PROD_DB} | psql -U ${DB_USER} -v ON_ERROR_STOP=1 ${STAGING_DB}" >/dev/null
    ;;
  custom)
    log "Restoring custom-format archive"
    # --exit-on-error: without it pg_restore reports problems, carries on, and leaves a
    # partial database that looks fine.
    docker exec "$DB_CONTAINER" pg_restore -U "$DB_USER" -d "$STAGING_DB" --exit-on-error /tmp/staging-restore.dump
    ;;
  plain)
    log "Restoring plain-SQL dump"
    docker exec "$DB_CONTAINER" psql -U "$DB_USER" -d "$STAGING_DB" -v ON_ERROR_STOP=1 -f /tmp/staging-restore.sql >/dev/null
    ;;
esac

# ---------------------------------------------------------------------------
# 4. Verify the restore landed
#
# This is what makes the refresh a restore *test* rather than a copy. An empty or
# missing per-section __EFMigrationsHistory table is the failure worth catching: the
# app boots fine and then re-applies that section's migrations from scratch.
# ---------------------------------------------------------------------------

TABLES=$(psql_staging -tAc "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE';" | tr -d '[:space:]')
[ "${TABLES:-0}" -gt 0 ] || die "restored database has no tables — the archive was empty or the wrong file"
log "Restored $TABLES tables"

# Every expected history table by name, not merely "at least one". A count test passes on an
# archive holding the main __EFMigrationsHistory and none of the per-section ones — and the
# empty-table query below cannot see a table that is absent, so such a restore would be
# reported as verified. The app would then treat that section's post-baseline migrations as
# unapplied and replay them against tables that already exist.
RESTORED_HISTORY=$(psql_staging -tAc "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_name LIKE '\_\_EFMigrationsHistory%';" | tr -d '\r')

EXPECTED_HISTORY="__EFMigrationsHistory"          # HumansDbContext, the original
for ctx in $SECTION_DB_CONTEXTS; do
  EXPECTED_HISTORY="$EXPECTED_HISTORY __EFMigrationsHistory_${ctx%DbContext}"
done

MISSING_HISTORY=""
for expected in $EXPECTED_HISTORY; do
  printf '%s\n' "$RESTORED_HISTORY" | grep -qx -- "$expected" \
    || MISSING_HISTORY="$MISSING_HISTORY $expected"
done
[ -z "$MISSING_HISTORY" ] \
  || die "restored database is missing migration-history table(s):$MISSING_HISTORY — either the archive is not a Humans database, or it predates those sections; restoring it would have the app replay their migrations against existing tables"

log "Migration history:"
psql_staging -c "
  SELECT table_name,
         (xpath('/row/cnt/text()', query_to_xml(format('select count(*) as cnt from public.%I', table_name), false, true, '')))[1]::text::bigint AS migrations
  FROM information_schema.tables
  WHERE table_schema='public' AND table_name LIKE '\_\_EFMigrationsHistory%'
  ORDER BY table_name;"

EMPTY_HISTORY=$(psql_staging -tAc "
  SELECT count(*) FROM (
    SELECT (xpath('/row/cnt/text()', query_to_xml(format('select count(*) as cnt from public.%I', table_name), false, true, '')))[1]::text::bigint AS migrations
    FROM information_schema.tables
    WHERE table_schema='public' AND table_name LIKE '\_\_EFMigrationsHistory%'
  ) h WHERE h.migrations = 0;" | tr -d '[:space:]')
[ "${EMPTY_HISTORY:-0}" = "0" ] \
  || die "$EMPTY_HISTORY migration-history table(s) restored empty — the app would re-apply that section from scratch"

# ---------------------------------------------------------------------------
# 5. Uploads
#
# A copy, never a share: staging writes to its uploads directory, and prod's bind mount
# must not be on the other end of that. --delete is deliberate — staging's own uploads
# are wiped on every refresh, same as its database.
# ---------------------------------------------------------------------------

log "Copying uploads -> $STAGING_UPLOADS_DIR"
mkdir -p "$STAGING_UPLOADS_DIR"
rsync -a --delete "$PROD_UPLOADS_DIR"/ "$STAGING_UPLOADS_DIR"/

# ---------------------------------------------------------------------------
# 6. Hand back
#
# Coolify deploys the staging app on the same push that starts this refresh, and nothing
# orders the two. So the check in §2 is a snapshot, not a lock: a container can come up
# between the DROP and the end of the restore and bind to a partial clone. The state is
# re-read here rather than assumed, and anything found running is restarted — the database
# it is looking at now is the finished one, and a restart is what makes the migration
# service run against it instead of against whatever existed mid-restore.
# ---------------------------------------------------------------------------

APP_IS_RUNNING=$(docker inspect -f '{{.State.Running}}' "$STAGING_APP_CONTAINER")
if [ "$APP_IS_RUNNING" = "true" ]; then
  [ "$APP_WAS_RUNNING" = "true" ] \
    || log "'$STAGING_APP_CONTAINER' came up during the refresh — a deploy landed mid-restore"
  log "Restarting '$STAGING_APP_CONTAINER'"
  docker restart "$STAGING_APP_CONTAINER" >/dev/null
elif [ "$APP_WAS_RUNNING" = "true" ]; then
  log "Starting '$STAGING_APP_CONTAINER'"
  docker start "$STAGING_APP_CONTAINER" >/dev/null
fi

log "Done. '$STAGING_DB' rebuilt from $SOURCE."
