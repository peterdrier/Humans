# Database Restore Runbook

**Read this when the database is wrong and you need it right again.** It covers restoring
Humans' PostgreSQL database from a backup, the automatic pre-deploy snapshot that gives you
something to restore *to* after a bad migration, and the deploy-freeze rule for event week.

The backup, restore, verification, and application-boot steps (§1–§3) were executed
end-to-end on 2026-08-05 against a local `postgres:16` container — the image
`docker-compose.yml` pins — with the schema built by this application's own migrations.
Observed output and timings are quoted verbatim in [Drill record](#drill-record-2026-08-05),
along with what the drill did *not* cover. Everything that could not be verified without the
Coolify console is called out explicitly rather than assumed.

- **Server and client versions:** the historical drill and QA compose file use PostgreSQL 16;
  the runtime image installs client 18 for production. Check the actual database server and
  `pg_dump`/`pg_restore` versions before choosing a restore container. Match the rehearsal
  server to production and verify the selected `pg_restore` can read the archive with
  `pg_restore --list`. Do not assume the QA container's client can read a production snapshot.
- **The database work does not need the application running** — restores go container →
  database — but §3 requires the app *stopped*, because its open connections block
  `DROP DATABASE`.

---

## 0. Which backup are you restoring?

There are two sources, and they are for different situations.

| Source | Taken when | Where it lives | Use it for |
|--------|-----------|----------------|------------|
| **Coolify scheduled backup** | On Coolify's backup schedule | Coolify's configured off-host storage | Data loss, corruption, an old state you need back |
| **Pre-migration snapshot** | Automatically, immediately before any deploy that changes the schema | `db_snapshots` volume, mounted at `/app/db-snapshots` inside the **app** container | Undoing a schema-changing deploy — whether its migration failed outright (§4) or completed and turned out to be wrong. Rolls you back to the moment before that deploy touched the schema |

> **Format assumption.** Coolify's backup format is configured in the Coolify console and is
> not committed to this repo, so it could not be confirmed from here. **Assume custom format
> (`pg_dump --format=custom`, restored with `pg_restore`)** — the usual choice for automated
> backups — and check the file if unsure:
>
> ```bash
> file backup.dump          # "PostgreSQL custom database dump"
> head -c 5 backup.dump     # custom format starts with the magic bytes "PGDMP"
> ```
>
> If it is plain SQL instead, use the [plain-SQL variant](#plain-sql-variant) — both paths are
> exercised in the drill below. **Owner action:** confirm the format in Coolify once and delete
> this box.
>
> The pre-migration snapshots this repo writes are always custom format.

Find the containers you will be working with:

```bash
docker ps --format '{{.Names}}\t{{.Image}}'
```

Identify the database container by the deployed connection configuration and confirm its
server version. `humans-db` is the host used by preview routing; it is not proof that a
container is the intended production or rehearsal target. The rest of this runbook calls
the verified database container `$DB` and the app container `$APP`.

Use a separate, temporary client container for **every restore and verification command**.
Choose `PGCLIENT_IMAGE` for the archive's `pg_dump` version; `postgres:18` below is an example,
not a statement about the target server. Set `PGPORT` to the verified database's internal
PostgreSQL port, not its published host port. Enter that database's existing authorized
`humans` password at the prompt; do not put it in shell history.

Run the commands in the same Bash shell with the fail-fast settings below. If any command
fails, stop and investigate before continuing; a failed drop/create must never be followed
by a restore into the existing database.

```bash
set -euo pipefail
PGCLIENT_IMAGE=postgres:18
PGPORT=5432
RESTORE_CLIENT="humans-restore-client-$(date -u +%Y%m%dT%H%M%SZ)-$$"
read -rsp 'Password for the verified database: ' PGPASSWORD
printf '\n'
export PGPASSWORD
docker run -d --rm --name "$RESTORE_CLIENT" --network "container:$DB" \
  -e PGHOST=127.0.0.1 -e "PGPORT=$PGPORT" -e PGPASSWORD \
  --entrypoint sleep "$PGCLIENT_IMAGE" infinity
unset PGPASSWORD
docker exec "$RESTORE_CLIENT" pg_restore --version
docker exec "$RESTORE_CLIENT" psql -X -U humans -d postgres -v ON_ERROR_STOP=1 \
  -c 'SELECT version(), inet_server_addr(), inet_server_port();'
```

This runs only client tools, sharing the verified database container's network stack.
The password remains available to noninteractive commands in this temporary container;
access to Docker already grants access to its environment. Keep the client until recovery
finishes, then stop it as described in §3. Archive readability alone does not prove that a
newer dump's SQL works on an older server: the actual scratch restore must succeed.

---

## 1. Get the dump file onto the restore client

**From a pre-migration snapshot** (it is inside the *app* container, and snapshots are named
`{database}-{UTC timestamp}.dump`). Use `docker cp` on the directory rather than
`docker exec ls` — `docker cp` works on a stopped or crash-looping container, `docker exec`
does not:

```bash
SNAPSHOTS=$(mktemp -d)
docker cp $APP:/app/db-snapshots/. "$SNAPSHOTS" && ls -lt "$SNAPSHOTS"
```

Two details, both about making a *second* attempt safe — and during an incident there is almost
always a second attempt:

- **A fresh directory every time.** `docker cp` copies into the destination; it never removes
  what is already there. Copying into a reused `./snapshots` leaves last incident's files
  sitting alongside this one's, including an old `.unfinished` that the rules below would
  happily identify as this deploy's rollback point. `mktemp -d` costs nothing and makes that
  impossible.
- **The trailing `/.` on the source.** Without it, `docker cp` of a directory into an existing
  directory nests it, and you get `$SNAPSHOTS/db-snapshots/...` instead of the files.

**Which file — it depends on what went wrong:**

| Situation | File | Why |
|-----------|------|-----|
| **The migration failed** — app crash-looping, §4 | The snapshot named in this deployment's `Pre-migration snapshot written` or `Reusing pre-migration snapshot` log | Correlate its timestamp, source release and `.migrations` sidecar with the failed deployment. An `.unfinished` suffix alone is insufficient: stale markers can survive completed deploys. |
| **The deploy succeeded but was wrong** — bad data, wrong schema, and you want the previous release's state back | The verified snapshot from immediately before that deploy | It normally has a plain `.dump` suffix; failed marker cleanup can leave `.unfinished`. Match the boot record rather than choosing only by filename order. |

Both are counterintuitive in the same way, so check the timestamps in `ls -lt` output against
when the deploy happened rather than trusting the ordering. If the deploy you are undoing was
code-only it took no migration snapshot. Image rollback repairs the code; it is sufficient only
when the schema remains compatible and application writes have not damaged persistent data.

A **`.writing`** file is never a restore candidate: that is an aborted dump. See §5.

Then copy the file you picked to a plain `restore.dump` — the suffix is bookkeeping for the app,
and `pg_restore` does not care what the file is called:

```bash
cp "$SNAPSHOTS"/humans-20260805T155147Z.dump.unfinished ./restore.dump
docker cp ./restore.dump "$RESTORE_CLIENT":/tmp/restore.dump
```

**From a Coolify backup:** download it from Coolify's storage to the host, then

```bash
docker cp ./restore.dump "$RESTORE_CLIENT":/tmp/restore.dump
```

> **Name the file for its format and keep that name to the end.** Custom-format archives go to
> `/tmp/restore.dump` and are restored with `pg_restore`; plain SQL goes to `/tmp/restore.sql`
> and is restored with `psql -f`. §2 and §3 both have commands for each — use the same one in
> both places. Pre-migration snapshots are always custom format.

For a custom-format archive, confirm that the selected client can read it before proceeding:

```bash
docker exec "$RESTORE_CLIENT" pg_restore --list /tmp/restore.dump > restore-contents.txt
```

---

## 2. Restore into a scratch database first

Always do this before touching the live database. It proves the archive is readable and tells
you what you are about to get, and it costs one command.

```bash
docker exec "$RESTORE_CLIENT" psql -X -U humans -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS humans_restore"
docker exec "$RESTORE_CLIENT" psql -X -U humans -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE humans_restore OWNER humans"
docker exec "$RESTORE_CLIENT" pg_restore -U humans -d humans_restore --exit-on-error /tmp/restore.dump
```

`--exit-on-error` matters: without it `pg_restore` reports problems and carries on, and you get
a partial database that looks fine.

Then verify — row counts per table:

```bash
docker exec "$RESTORE_CLIENT" psql -X -U humans -d humans_restore -v ON_ERROR_STOP=1 -c "
  SELECT table_name,
         (xpath('/row/cnt/text()', query_to_xml(format('select count(*) as cnt from %I.%I', table_schema, table_name), false, true, '')))[1]::text::bigint AS rows
  FROM information_schema.tables
  WHERE table_schema='public' AND table_type='BASE TABLE'
  ORDER BY rows DESC LIMIT 20;"
```

and that the migration history is complete. **There is one history table per DbContext**, not
one for the whole database — the per-section split (nobodies-collective/Humans#858) gave each
section its own `__EFMigrationsHistory_<Section>`, and a restore that is missing one of those
looks fine until the app boots and starts applying that section's migrations from scratch. Count
all of them:

```bash
docker exec "$RESTORE_CLIENT" psql -X -U humans -d humans_restore -v ON_ERROR_STOP=1 -c "
  SELECT table_name,
         (xpath('/row/cnt/text()', query_to_xml(format('select count(*) as cnt from public.%I', table_name), false, true, '')))[1]::text::bigint AS migrations
  FROM information_schema.tables
  WHERE table_schema='public' AND table_name LIKE '\_\_EFMigrationsHistory%'
  ORDER BY table_name;"
```

Then export the actual migration identities, ordered by history table and migration ID:

```bash
docker exec -i "$RESTORE_CLIENT" psql -X -U humans -d humans_restore \
  -v ON_ERROR_STOP=1 -At -F $'\t' > restored-migrations.tsv <<'SQL'
SELECT format(
  'SELECT %L, "MigrationId" FROM %I.%I ORDER BY "MigrationId";',
  table_name, table_schema, table_name)
FROM information_schema.tables
WHERE table_schema = 'public'
  AND table_name LIKE '\_\_EFMigrationsHistory%'
ORDER BY table_name
\gexec
SQL
```

Compare these table/ID pairs against the producing release's registered contexts and
migration identities, not just the counts above. Retain the count check: an empty history
has no identity rows, and an absent expected history must also be caught.

Derive the expected histories from the release that produced the backup, including its
registered section contexts and migration files. Current releases use per-section histories;
older backups may also contain the former shared `__EFMigrationsHistory`. Compare migration
identities as well as counts. A history expected for that release being absent or empty needs
investigation; a history introduced only by the candidate is not expected in an older backup.
Record expectations separately before and after upgrading. Boot the candidate normally so
the migration host performs its section-baseline reconciliation; a direct EF update does not
exercise that startup path. The [drill record](#drill-record-2026-08-05) is historical evidence,
not the current context inventory.

If those look wrong, **stop** — you have the wrong backup, and you have not damaged anything.
Drop `humans_restore` before you walk away, though: the end of §3 says why, and it applies just
as much to an attempt you abandoned here.

### Plain-SQL variant

If the backup is plain SQL rather than custom format, you copied it to `/tmp/restore.sql` in §1.
Replace the `pg_restore` line with:

```bash
docker exec "$RESTORE_CLIENT" psql -X -U humans -d humans_restore -v ON_ERROR_STOP=1 -f /tmp/restore.sql
```

`ON_ERROR_STOP=1` is the plain-SQL equivalent of `--exit-on-error`. Without it psql prints
errors and keeps going. The verification queries above are the same either way, and §3 has the
matching live-restore command — carry the format through to the end, do not switch back to
`pg_restore` there.

---

## 3. Restore over the live database

Only after step 2 looked right.

**Stop the app first.** Its connections will block `DROP DATABASE`, and you do not want it
writing into a half-restored database.

```bash
docker stop $APP
```

```bash
# Kick any remaining sessions off the database
docker exec "$RESTORE_CLIENT" psql -X -U humans -d postgres -v ON_ERROR_STOP=1 -c \
  "SELECT pg_terminate_backend(pid) FROM pg_stat_activity
   WHERE datname='humans' AND pid <> pg_backend_pid()"

docker exec "$RESTORE_CLIENT" psql -X -U humans -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE humans"
docker exec "$RESTORE_CLIENT" psql -X -U humans -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE humans OWNER humans"
docker exec "$RESTORE_CLIENT" pg_restore -U humans -d humans --exit-on-error /tmp/restore.dump
```

**If the backup is plain SQL**, the last line is instead — same file, same flag as §2:

```bash
docker exec "$RESTORE_CLIENT" psql -X -U humans -d humans -v ON_ERROR_STOP=1 -f /tmp/restore.sql
```

For rollback, `$APP` must use the previous known-good image before this start; starting the
failed candidate would apply its migrations again. Then start the app and confirm it comes up:

```bash
docker start $APP
docker logs -f $APP
```

Check migration completion for every registered context. Older releases logged the shared
database breadcrumb below; use the current release's section startup messages rather than
expecting this historical count:

```
Database humans: 130 applied migrations, 0 pending
Database humans: schema is up to date
```

and then a healthy app. `curl` is installed in the runtime image, so run the check inside the
container and you do not have to care how the host port or the proxy is wired up:

```bash
docker exec $APP curl -fs http://localhost:8080/health/live
docker exec $APP curl -s  http://localhost:8080/api/version
docker inspect --format '{{.State.Health.Status}}' $APP     # -> healthy
```

**Then drop the scratch database — but not before the checks above pass.** Until they do,
`humans_restore` is your second chance at the same archive without another `pg_restore`. Once
they pass it is dead weight: a full second copy of the database sitting on the same volume, and
at production size that is how an incident turns into a disk-full outage a week later.

```bash
docker exec "$RESTORE_CLIENT" psql -X -U humans -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS humans_restore"
docker exec $DB df -h /var/lib/postgresql/data     # confirm the space came back
docker stop "$RESTORE_CLIENT"                  # --rm removes only this temporary client
```

If you abandon the attempt after §2, stop this client after dropping the scratch database.
Keep the original backup and verification output outside the temporary client.

**If it says pending migrations instead of "up to date",** you restored a backup older than the
running release. That is fine and expected — the app will apply the missing migrations on boot,
and it will take a fresh pre-migration snapshot before doing so.

---

## 4. A migration broke production

This is the situation the pre-migration snapshot exists for. The single instance runs with
`restart: unless-stopped`, and migrations are applied during startup and rethrow on failure, so
a bad migration means the container crash-loops.

1. **Read the logs before doing anything.** `docker logs $APP | tail -100`. The line
   `Applying pending migration: <name>` immediately before the exception names the culprit.
2. **Find the snapshot.** `SNAPSHOTS=$(mktemp -d) && docker cp $APP:/app/db-snapshots/.
   "$SNAPSHOTS" && ls -lt "$SNAPSHOTS"` — match the snapshot path in the failed deployment's
   boot logs and verify its timestamp/source release and migration sidecar. (`docker cp` rather than `docker exec`: a
   crash-looping container is usually not in a state you can exec into. The fresh directory and
   the trailing `/.` both matter on a second attempt — see §1.)
   - **A suffix is not a deployment identity.** The app carries a failed deploy's snapshot
     forward across restarts, but stale `.unfinished` markers can also remain after completed
     deploys. The `.migrations` sidecar records which migrations were pending when the dump was
     taken. If markers are multiple, stale or missing their sidecars, establish the matching
     rollback point from deployment/backup records before restoring. Never edit marker state.
   - The app container survives a crash-loop (Docker restarts the same container, it does not
     replace it), so the file is still there. If the container has been *recreated* since, find
     the volume with `docker volume ls | grep db_snapshots` and read the file from its
     `Mountpoint` (`docker volume inspect <name>`) on the host.
3. **Roll the image back** to the previous release in Coolify. That alone is not enough if the
   migration partially applied — schema changes do not roll back with the image.
4. **Restore the snapshot** using steps 1–3 above.
5. **File the bug** before redeploying. The migration will re-run on the next deploy.

**If there is no snapshot for this deploy, do not infer that the schema is unchanged.** A
container replacement can lose snapshots when the volume was not persistent. Inspect the
migration logs and storage history. Image rollback alone is sufficient only when the deploy
was code-only or logs establish that it stopped before schema changes, and no persistent data
recovery is needed. Otherwise recover a known-good backup with its matching application image.

---

## 5. The pre-deploy snapshot

Implemented in `src/Humans.Base/Hosting/PreMigrationSnapshot.cs`
(nobodies-collective/Humans#845).

- **What triggers it:** the startup migration path — the only thing committed to this repo that
  runs on every deploy and knows whether *this* deploy changes the schema. The first context
  with something to apply triggers one `pg_dump`; a deploy with no pending migrations never
  dumps.
- **Where the file goes:** `/app/db-snapshots/{database}-{UTC timestamp}.dump`, custom format,
  on the `db_snapshots` volume. It is deliberately **not** under `wwwroot` — that directory is
  web-served.
- **The `.unfinished` suffix:** the snapshot earns it when `pg_dump` succeeds. Successful
  migration completion normally removes it; failed cleanup can leave a stale marker. The
  `.migrations` sidecar records the pending migration identities at capture. Later boots retire
  markers whose recorded migrations have finished and carry forward a still-pending marker,
  logging its exact path. Missing/unreadable sidecars are conservatively carried forward, so
  the suffix alone does not prove a snapshot belongs to the current deploy. Unfinished markers
  are not pruned; identify the restore candidate as described in §4.
- **A `.writing` file is not a backup.** That is the name a dump in flight is written under; it
  is renamed to `.unfinished` only once `pg_dump` succeeds, so a dump that failed or was killed
  can never be mistaken for a rollback point. The next dump attempt deletes it. If you see one,
  the dump did not finish. Correlate it with boot logs before concluding that this deployment
  stopped before schema changes; the file alone is not a recovery plan.
- **Retention:** the newest 10 completed snapshots are kept; older ones are deleted after a
  successful dump. These are a fast local rollback point, not the archive — Coolify's scheduled
  backups are the off-host copy.
- **Which environments:** `Production` and `Staging` (QA) only. Every other environment —
  Development, the integration-test host — runs against a disposable local database and skips
  it.
- **If the dump fails, startup aborts and the migration does not run.** This is on purpose:
  this boot leaves the schema as it found it. Image rollback recovers that startup failure;
  any earlier data damage still needs its own recovery. Fix the cause (usually the volume mount or a missing `pg_dump`) and
  redeploy — do not work around it. `postgresql-client-18` is installed in the runtime image by
  the `Dockerfile`; the volume is declared in `docker-compose.yml`.
- **The client major tracks production's server, not `docker-compose.yml`'s.** `pg_dump` refuses
  to dump a server newer than itself but reads older ones fine, so the pinned client must be
  `>=` the production server's major. Production runs Postgres 18; `docker-compose.yml` runs 16
  for QA and local, and one client 18 covers both. Pinning it to the compose file instead is what
  broke the first schema-changing production deploy after this feature shipped
  (nobodies-collective/Humans#1187): the snapshot only runs when a deploy has pending migrations,
  so the mismatch sat unexercised through every deploy that did not touch the schema, and QA —
  on 16, where a client 16 works — could not reproduce it.

> **Owner action — Coolify.** QA gets the `db_snapshots` volume from `docker-compose.yml`.
> Production is deployed through Coolify, whose volume configuration is not in this repo, so it
> could not be confirmed from here. **Add a persistent volume mounted at `/app/db-snapshots` on
> the production app resource.** Without it snapshots still get written, but they live in the
> container's own filesystem: they survive a crash-loop restart (which is the case they are for)
> and are lost when the container is replaced.

---

## 6. Uploads (`wwwroot/uploads`)

**Not confirmed to be in off-host backup scope. Do not assume it is.**

What the repo shows: `docker-compose.yml` bind-mounts `./uploads` on the host into
`/app/wwwroot/uploads` in the container. That is the QA/NUC stack. Production runs through
Coolify and its volume and backup configuration is not committed here, so from the repo alone
there is no way to tell whether that directory is copied off-host.

This matters because the directory holds real user data that exists nowhere else: profile
pictures and camp images (`src/Sections/Humans.Users/Docs/features/profile-pictures-birthdays.md`). Coolify's
database backups do **not** cover it — the bytes are on the filesystem, not in Postgres.

**Owner verification step (needs the Coolify console):**

1. Open the Humans production resource in Coolify → **Storages**. Note the host path backing
   `/app/wwwroot/uploads`.
2. Open **Backups** for that resource. Confirm whether a scheduled backup covers that host path
   — Coolify's scheduled backups target *databases*, so a filesystem path is only covered if
   something was configured explicitly for it.
3. If it is not covered, add an off-host copy of that directory (a scheduled job to the same
   storage as the database backups is enough) and record here that it is done.

Until step 3 is confirmed, treat uploads as **unbacked-up**: a host failure loses every profile
picture and camp image.

---

## 7. Event deploy freeze

**During the live event, no schema-changing deploy without a fresh snapshot and an admin on
hand.** The event is the one time the system is load-bearing in real time and the one time
nobody is at a laptop to fix it.

The freeze window runs from the start of build week through the end of strike.

**Frozen — do not deploy during the window:**

- Anything with a pending EF migration (any `Up()` at all: `AddColumn` is as frozen as
  `DropColumn`).
- Anything that drops or rewrites hard storage. Those already wait for a separate
  post-verification PR anyway — see `memory/architecture/no-drops-until-prod-verified.md`.

**Allowed during the window:** code-only deploys with no pending migrations. They roll back with
the image in minutes, and the pre-migration snapshot correctly does not fire for them.

**If a schema change genuinely cannot wait** — a data-corrupting bug, gate admissions broken —
then all of the following, no exceptions:

1. An admin who can reach the server is awake, at a keyboard, and knows the deploy is happening.
2. **Before:** the snapshot volume is real — `docker exec $APP ls -l /app/db-snapshots` shows the
   previous deploy's files, not an empty directory. You cannot pre-check *this* deploy's
   snapshot: the new image takes it on boot, immediately before it migrates. That it gets taken
   at all is guaranteed by §5 (a failed dump aborts startup before any schema change); what a
   missing volume costs you is the file surviving a container replacement.
3. **After:** a snapshot with this deploy's timestamp actually appeared —
   `docker exec $APP ls -lt /app/db-snapshots | head` — before anyone walks away.
4. Someone has read this runbook *before* deploying, not during the incident.
5. It is not during a peak hour — gate opening, ticket scanning surges, shift changeover.

**Why:** every failure mode above ends in "the single instance crash-loops and someone must
hand-restore." That is a 10-minute job with a snapshot, this runbook, and an awake admin. It is
an outage of unknown length without them, in the middle of the one week the app matters most.

---

## Drill record (2026-08-05)

Executed against a throwaway local `postgres:16` container, schema created by running this
application's own migrations, data seeded through the app's dev personas.

**Dataset:** 123 tables, 1,154 columns, 1,275 rows, 130 applied migrations.

> **Scale caveat.** This dataset is much smaller than production. The *procedure* is proven;
> the *timings* are a floor, not a prediction. Restore time scales with data volume — mostly
> index rebuilds — so budget generously at 3am. The one number that will not change much is the
> pre-deploy snapshot: `pg_dump` of a database this shape is sub-second.

```
source: 123 tables, 1154 columns, 1275 rows

===== 1. Backup (custom format) =====
$ docker exec humans-restore-drill pg_dump -U humans -d humans --format=custom --file=/tmp/humans-20260805T155147Z.dump
exit=0 elapsed=373ms
-rw-r--r-- 1 root root 419929 Aug  5 15:51 /tmp/humans-20260805T155147Z.dump

===== 2. Restore into a scratch database =====
$ docker exec humans-restore-drill pg_restore -U humans -d humans_restore --exit-on-error /tmp/humans-20260805T155147Z.dump
exit=0 elapsed=6883ms

===== 3. Verify scratch restore: row counts =====
IDENTICAL: 123 tables, 1275 rows

===== 4. Verify scratch restore: schema =====
IDENTICAL: 1154 columns

===== 5. Plain-SQL variant =====
$ docker exec humans-restore-drill psql -U humans -d humans_restore_sql -v ON_ERROR_STOP=1 -f /tmp/humans-20260805T155147Z.sql
exit=0 elapsed=5041ms
IDENTICAL: plain-SQL restore matches source

===== 6. Full in-place recovery: drop and recreate the live database =====
$ terminate connections, DROP DATABASE humans, CREATE DATABASE humans, pg_restore
exit=0 elapsed=6158ms
IDENTICAL: in-place restore matches pre-drop source
```

Application boot against the restored database:

```
health/live -> 200
[17:52:23 WRN] Database humans: 130 applied migrations, 0 pending
[17:52:23 INF] Database humans: schema is up to date
[17:52:23 INF] SettingsDbContext: schema is up to date
[17:52:23 INF] ContainersDbContext: schema is up to date
[17:52:24 INF] AgentDbContext: schema is up to date
[17:52:24 INF] ExpensesDbContext: schema is up to date
[17:52:24 INF] FinanceDbContext: schema is up to date
[17:52:24 INF] SurveysDbContext: schema is up to date
[17:52:24 INF] EventGuideDbContext: schema is up to date
[17:52:25 INF] Now listening on: http://localhost:53456
[17:52:25 INF] Application started. Press Ctrl+C to shut down.
```

**Verification method:** row counts and full column/type/nullability lists were dumped from the
source database and from each restored database and compared with `diff` — "IDENTICAL" above
means an empty diff, not a spot check.

### What running it changed

- **The drill used `docker exec` into its database container.** It had no host PostgreSQL
  client. For a new restore, verify the chosen container's client against the actual server
  and archive versions; the historical `postgres:16` image is not a production default.
- **Restore is ~18× slower than backup** (373 ms dump vs 6.9 s restore) because the restore
  rebuilds every index. That ratio, not the dump time, is what an outage estimate should be
  based on.
- **The plain-SQL path was added** once it became clear the Coolify backup format cannot be
  determined from this repo. Both paths are now exercised rather than one being assumed.

### What the drill did *not* prove

- Restoring an actual Coolify-produced backup file — that needs a real backup from the Coolify
  console. The format assumption above stands until someone checks.
- Restore timing at production data volume.
- That `wwwroot/uploads` is backed up anywhere (see §6).
- The in-place restore ran with the application already stopped. `pg_terminate_backend` was
  issued anyway (it is in the procedure above and is standard for `DROP DATABASE`), but the
  drill did not test dropping the database out from under a live app.
