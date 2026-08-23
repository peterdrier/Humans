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

- **Server version:** PostgreSQL 16 (`docker-compose.yml` → `image: postgres:16`). Restore
  with a client at least as new as the `pg_dump` that produced the file; an older `pg_restore`
  rejects a newer archive outright. The `postgres:16` container's own client is the safe
  default, which is why every command here runs inside it.
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

The database container is the `postgres:16` one (named `humans-db` in the deployed
environment — that is the host `docker-entrypoint.sh` points preview databases at); the app
container is the one built from this repo. The rest of this runbook calls them `$DB` and
`$APP`.

---

## 1. Get the dump file onto the database container

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
| **The migration failed** — app crash-looping, §4 | the one ending **`.unfinished`** | The suffix means the deploy that took it never finished migrating, so it is the last state before that deploy touched the schema. There is at most one. |
| **The deploy succeeded but was wrong** — bad data, wrong schema, and you want the previous release's state back | the **newest plain `.dump`** | A completed deploy's snapshot loses the suffix, so the newest `.dump` is the state immediately before the most recent schema-changing deploy. |

Both are counterintuitive in the same way, so check the timestamps in `ls -lt` output against
when the deploy happened rather than trusting the ordering. If the deploy you are undoing was
code-only it took no snapshot at all — the database is not the problem and rolling the image back
is the whole fix.

A **`.writing`** file is never a restore candidate: that is an aborted dump. See §5.

Then copy the file you picked to a plain `restore.dump` — the suffix is bookkeeping for the app,
and `pg_restore` does not care what the file is called:

```bash
cp "$SNAPSHOTS"/humans-20260805T155147Z.dump.unfinished ./restore.dump
docker cp ./restore.dump $DB:/tmp/restore.dump
```

**From a Coolify backup:** download it from Coolify's storage to the host, then

```bash
docker cp ./restore.dump $DB:/tmp/restore.dump
```

> **Name the file for its format and keep that name to the end.** Custom-format archives go to
> `/tmp/restore.dump` and are restored with `pg_restore`; plain SQL goes to `/tmp/restore.sql`
> and is restored with `psql -f`. §2 and §3 both have commands for each — use the same one in
> both places. Pre-migration snapshots are always custom format.

---

## 2. Restore into a scratch database first

Always do this before touching the live database. It proves the archive is readable and tells
you what you are about to get, and it costs one command.

```bash
docker exec $DB psql -U humans -d postgres -c "DROP DATABASE IF EXISTS humans_restore"
docker exec $DB psql -U humans -d postgres -c "CREATE DATABASE humans_restore OWNER humans"
docker exec $DB pg_restore -U humans -d humans_restore --exit-on-error /tmp/restore.dump
```

`--exit-on-error` matters: without it `pg_restore` reports problems and carries on, and you get
a partial database that looks fine.

Then verify — row counts per table:

```bash
docker exec $DB psql -U humans -d humans_restore -c "
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
docker exec $DB psql -U humans -d humans_restore -c "
  SELECT table_name,
         (xpath('/row/cnt/text()', query_to_xml(format('select count(*) as cnt from public.%I', table_name), false, true, '')))[1]::text::bigint AS migrations
  FROM information_schema.tables
  WHERE table_schema='public' AND table_name LIKE '\_\_EFMigrationsHistory%'
  ORDER BY table_name;"
```

Expect the main `__EFMigrationsHistory` plus one per section context. The boot log quoted in the
[drill record](#drill-record-2026-08-05) names the section contexts of that release — there were
seven, so eight history tables. A section table that is **absent or empty** is the failure this
check exists to catch. The main table's count must also match the migration count of the release
you are running.

If those look wrong, **stop** — you have the wrong backup, and you have not damaged anything.
Drop `humans_restore` before you walk away, though: the end of §3 says why, and it applies just
as much to an attempt you abandoned here.

### Plain-SQL variant

If the backup is plain SQL rather than custom format, you copied it to `/tmp/restore.sql` in §1.
Replace the `pg_restore` line with:

```bash
docker exec $DB psql -U humans -d humans_restore -v ON_ERROR_STOP=1 -f /tmp/restore.sql
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
docker exec $DB psql -U humans -d postgres -c \
  "SELECT pg_terminate_backend(pid) FROM pg_stat_activity
   WHERE datname='humans' AND pid <> pg_backend_pid()"

docker exec $DB psql -U humans -d postgres -c "DROP DATABASE humans"
docker exec $DB psql -U humans -d postgres -c "CREATE DATABASE humans OWNER humans"
docker exec $DB pg_restore -U humans -d humans --exit-on-error /tmp/restore.dump
```

**If the backup is plain SQL**, the last line is instead — same file, same flag as §2:

```bash
docker exec $DB psql -U humans -d humans -v ON_ERROR_STOP=1 -f /tmp/restore.sql
```

Then start the app and confirm it comes up:

```bash
docker start $APP
docker logs -f $APP
```

You are looking for the migration breadcrumb, which is logged at Warning level so it survives
production log filtering:

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
docker exec $DB psql -U humans -d postgres -c "DROP DATABASE IF EXISTS humans_restore"
docker exec $DB df -h /var/lib/postgresql/data     # confirm the space came back
```

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
   "$SNAPSHOTS" && ls -lt "$SNAPSHOTS"` — the file ending **`.unfinished`** is from this deploy,
   taken *before* the failed migration ran. (`docker cp` rather than `docker exec`: a
   crash-looping container is usually not in a state you can exec into. The fresh directory and
   the trailing `/.` both matter on a second attempt — see §1.)
   - **Take the `.unfinished` one, not the newest one.** The crash loop keeps restarting the
     app, and each restart re-runs the migration against a schema the earlier attempts may have
     already part-changed. The suffix marks the snapshot from *before* any of that; the app
     carries it forward untouched across restarts rather than dumping over it, which is exactly
     why it is still the right file on the tenth restart.
   - The app container survives a crash-loop (Docker restarts the same container, it does not
     replace it), so the file is still there. If the container has been *recreated* since, find
     the volume with `docker volume ls | grep db_snapshots` and read the file from its
     `Mountpoint` (`docker volume inspect <name>`) on the host.
3. **Roll the image back** to the previous release in Coolify. That alone is not enough if the
   migration partially applied — schema changes do not roll back with the image.
4. **Restore the snapshot** using steps 1–3 above.
5. **File the bug** before redeploying. The migration will re-run on the next deploy.

**If there is no snapshot for this deploy,** the deploy did not change the schema — so the
database is not the problem, and rolling the image back is a complete fix.

---

## 5. The pre-deploy snapshot

Implemented in `src/Humans.Infrastructure/Hosting/PreMigrationSnapshot.cs`
(nobodies-collective/Humans#845).

- **What triggers it:** the startup migration path — the only thing committed to this repo that
  runs on every deploy and knows whether *this* deploy changes the schema. The first context
  with something to apply triggers one `pg_dump`; a deploy with no pending migrations never
  dumps.
- **Where the file goes:** `/app/db-snapshots/{database}-{UTC timestamp}.dump`, custom format,
  on the `db_snapshots` volume. It is deliberately **not** under `wwwroot` — that directory is
  web-served.
- **The `.unfinished` suffix:** the snapshot earns it when `pg_dump` exits successfully and loses
  it only once a boot gets all the way through its migrations. So a file still carrying the
  suffix means "the deploy that took this never finished" — it is the live rollback point, and it
  is the file §4 tells you to restore. While it is there, later boots reuse it instead of taking a
  new dump (log line: `Reusing pre-migration snapshot …`), which is what stops a crash loop from
  archiving a part-migrated schema over the good one. It is also never pruned.
- **A `.writing` file is not a backup.** That is the name a dump in flight is written under; it
  is renamed to `.unfinished` only once `pg_dump` succeeds, so a dump that failed or was killed
  can never be mistaken for a rollback point. The next dump attempt deletes it. If you see one,
  the deploy that made it aborted before migrating — the schema is untouched and rolling the
  image back is a complete fix.
- **Retention:** the newest 10 completed snapshots are kept; older ones are deleted after a
  successful dump. These are a fast local rollback point, not the archive — Coolify's scheduled
  backups are the off-host copy.
- **Which environments:** `Production` and `Staging` (QA) only. Every other environment —
  Development, the integration-test host — runs against a disposable local database and skips
  it.
- **If the dump fails, startup aborts and the migration does not run.** This is on purpose:
  the schema is left exactly as the previous release left it, so rolling the image back is a
  complete recovery. Fix the cause (usually the volume mount or a missing `pg_dump`) and
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

- **Every command is `docker exec` into the database container, not a host command.** The
  drill machine had no `pg_dump`/`pg_restore` on the host at all. Assume the same on the
  server: the client binaries you can rely on are the ones inside the `postgres:16` container.
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
