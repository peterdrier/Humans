# Staging Environment

**Production-candidate code, running against a fresh clone of production data, on the
production host.** Every promotion to production passes through it first, so the first time a
migration meets real production data is on staging rather than on production — and because the
clone is normally restored from the newest Coolify backup artifact, every staging deploy is
also a live test of the restore path that
[`database-restore-runbook.md`](database-restore-runbook.md) documents.

> **Not live yet.** The repository half of this — the refresh script, its workflow, this
> document, and the `/pr-prod` promotion step — is committed. The environment itself does not
> exist until someone with Coolify, Google Cloud console, and shell access to the production
> host works through [§7 Host-side steps](#7-host-side-steps-owner).
> nobodies-collective/Humans#962.
>
> **`scripts/refresh-staging-db.sh` has not been executed.** Unlike the restore runbook, whose
> procedure was drilled against a real `postgres:16` container before it was written down, this
> script is reviewed and syntax-checked only. Its commands are the ones `preview-db.yml` runs
> today and the ones the runbook's drill proved, but the script as a whole is unproven. **Run
> §7.3's manual dispatch and read the log before trusting a scheduled refresh** — that run is
> its first real test.

---

## 1. Topology

| Environment | Host | Deploys from | Database | `ASPNETCORE_ENVIRONMENT` |
|---|---|---|---|---|
| Production — `humans.nobodies.team` | prod server | `upstream/main` | `humans` | `Production` |
| **Staging — `staging.nobodies.team`** | **prod server** | **`upstream/staging`** | **`humans_staging`** | **`Staging`** |
| QA — `humans.n.burn.camp` | NUC | `origin/main` | `humans` (on the NUC) | `Staging` |
| PR previews — `{pr}.n.burn.camp` | NUC | the PR branch | `humans_pr_{N}` | `Staging` |

Staging and QA share the `Staging` environment name, and therefore share
`src/Humans.Web/appsettings.Staging.json`. **That file is QA's** — it pins `Email:BaseUrl` and
`AllowedHosts` to `humans.n.burn.camp`. Everything staging-specific is a Coolify environment
variable override, which is why §4 is not optional reading.

Production's deploy semantics do not change: it still auto-deploys `upstream/main` on push.

---

## 2. The promotion cycle

```
origin/main @ SHA  ──push SHA──▶  upstream/staging  ──▶  refresh humans_staging  ──▶  deploy
                                                                                        │
                                                                                   verify (§8)
                                                                                        │
                                   origin/promote/<date>-<sha> @ SHA  ◀── push SHA ─────┘
                                                    │
                                          PR ── rebase merge ──▶  upstream/main
                                                                        │
                                                                production deploy
```

The gate is git-native: `upstream/main` only ever moves to a commit that already deployed and
was verified on staging. `/pr-prod` (`.claude/skills/pr-prod/SKILL.md`) drives it.

**The gate is pinned to a commit, not to a branch.** `/pr-prod` captures the SHA it stages,
pushes that SHA to `staging`, and opens the production PR from a one-shot `promote/<date>-<sha>`
branch pointed at it. Opening the PR from `peterdrier:main` instead would let any feature PR
landing on the fork between verification and merge ride into production unverified, while the
body still named the older SHA — the branch head follows, the claim does not.

**Pushes to `staging` stop being fast-forwards after the first cycle.** Upstream merges by
rebase, so a promoted batch lands on `upstream/main` under rewritten SHAs, and
`memory/process/after-prod-merge-reset.md` resets `origin/main` onto that rewritten history.
`upstream/staging` is left at the pre-rebase commit, which is then nobody's ancestor. That
rejection is the normal state of affairs, not evidence of an abandoned batch. Realigning the
branch is a forced update and needs Peter's approval
(`memory/process/no-destructive-actions-without-approval.md`); whether that approval should
become standing is an open question recorded in `.claude/skills/pr-prod/SKILL.md`.

---

## 3. Database and uploads refresh

`scripts/refresh-staging-db.sh`, run on the production host by
`.github/workflows/staging-db.yml` on every push to `staging`.

It terminates connections to `humans_staging`, drops and recreates it, restores, verifies, and
copies uploads. The recipe is the one `preview-db.yml` already uses for PR previews, with the
restore-source switch and the verification step added.

### Two sources

| Source | What it restores | When |
|---|---|---|
| **`backup`** (default) | The newest Coolify backup artifact **for the `humans` database** under `COOLIFY_BACKUP_DIR` | Every push. Doubles as the restore test — a broken backup fails the workflow here rather than during an incident |
| **`live`** | `pg_dump` straight from the production database | Manual override via **Actions → Staging Database Refresh → Run workflow → source: live**, when you need data newer than the last backup |

**The database name has to match as a whole token, not as a substring.** `humans_staging`,
`humans_restore` and `humans_pr_12` all contain `humans`, and all of them restore cleanly and
satisfy every check below — so a newer artifact for one of those would be promoted as though it
were production, having verified the release against the wrong snapshot. The selector keeps an
artifact only when the character after the database name is a digit or a separator (the
timestamp Coolify appends); a letter, with or without an underscore, means a different database
whose name merely starts with this one. Pointing `COOLIFY_BACKUP_DIR` at the production
resource's own backup directory removes the ambiguity at the source and is worth doing if
Coolify's layout allows it.

Format is detected, not assumed: `.gz` is decompressed, then the `PGDMP` magic bytes select
`pg_restore --exit-on-error` over `psql -v ON_ERROR_STOP=1`. Both paths carry a
stop-on-first-error flag, because the failure mode without one is a partial database that looks
fine.

### What it verifies

After restoring, the script fails the workflow if the restored database has no tables, if any
**expected** `__EFMigrationsHistory*` table is missing, or if any of them came back empty.
There is one history table per DbContext since the per-section split
(nobodies-collective/Humans#858).

**The expected set is read from production's catalog, not from the checkout.** A release that
adds a section arrives at staging with a DbContext whose first migration has not run anywhere
yet, so production's backup legitimately has no history table for it. Deriving the expected set
from the candidate's list of contexts would reject that valid backup — making the gate
impossible to pass for precisely the releases most worth rehearsing. What the backup should
contain is what production's deployed schema contains, so that is what it is compared against.
This is the one statement `backup` mode runs against the production database, and it reads
table names from `information_schema`.

Missing and empty are different failures and both are quiet. An empty history table has the
app boot happily and then apply that section's migrations from scratch against tables that
already exist. A **missing** one cannot be caught by the empty check at all — no row to
inspect — which is how a selective or pre-split archive would otherwise be reported as
verified.

**Staleness is the third.** The newest artifact is rejected if it is older than
`BACKUP_MAX_AGE_HOURS` (default 48). A stalled backup schedule leaves a perfectly restorable
file behind, so without an age bound every check above passes while staging rehearses
migrations against weeks-old schema — and the backup outage goes unreported, which is the one
thing §9 says this workflow is for. Naming `BACKUP_FILE` explicitly bypasses the bound, since
that is a deliberate reach for an older artifact; `BACKUP_MAX_AGE_HOURS=0` disables it.

### Uploads

`rsync -a --delete` from production's uploads directory into staging's. A **copy**, never a
shared mount — staging writes to its uploads directory, and production's bytes must not be on
the other end of that. `--delete` is deliberate: staging's own uploads are wiped on every
refresh, exactly like its database. The script refuses to run if both paths are not set, if
either resolves to `/`, if they are the same directory, or if either is nested inside the
other — the two paths are resolved with `realpath` first, so a trailing slash or a symlink
cannot disguise any of those. Nesting is the one worth spelling out: with staging at `/data`
and production at `/data/uploads`, `rsync --delete` finds production's directory extraneous at
the destination and deletes it. `/` is rejected separately because it is the only canonical
path that already ends in a slash, which is exactly the shape the nesting test cannot see.

### Running it by hand

On the production host, from a checkout:

```bash
PROD_UPLOADS_DIR=/path/to/prod/uploads \
STAGING_UPLOADS_DIR=/path/to/staging/uploads \
STAGING_APP_CONTAINER=<staging app container> \
./scripts/refresh-staging-db.sh backup      # or: live
```

Set `BACKUP_FILE=/path/to/artifact` to restore a specific one — an older backup, or one whose
filename does not carry the database name — instead of the newest match. That also bypasses the
`BACKUP_MAX_AGE_HOURS` bound, which exists to catch a stalled schedule rather than to stop
someone deliberately restoring an old artifact.

Nothing in the script writes to production. `backup` reads only its catalog, to learn which
migration-history tables the backup is expected to carry; `live` reads its data as well; the
uploads copy is one-way. The destructive statements target
`humans_staging`, and the script refuses to start if that name has drifted onto `humans` or a
`humans_pr_*` preview.

---

## 4. Configuration

Set on the Coolify staging application. Values in `appsettings.Staging.json` and
`appsettings.json` are the fallback, and for the first three rows the fallback is **wrong for
this host** — those are QA's and production's values.

| Variable | Value | Why it cannot be left to defaults |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Staging` | Selects the connector-stubbing branches in §5 |
| `ConnectionStrings__DefaultConnection` | `Host=humans-db;Database=humans_staging;Username=humans_staging;Password=<staging role password>` | Same Postgres server as production, different database **and different role**. Not `humans` — see §7.7. The database name in a connection string is a choice the application makes at runtime; the role's grants are what actually stop staging reaching production |
| `POSTGRES_PASSWORD` | the **staging** role's password | Only if the staging resource templates its connection string from it. Never production's |
| `AllowedHosts` | `staging.nobodies.team;localhost` | `appsettings.Staging.json` sets this to `humans.n.burn.camp;localhost`. Leave it and **every request to staging is rejected** before it reaches a controller |
| `Email__BaseUrl` | `https://staging.nobodies.team` | Same file points it at QA. Links in anything staging generates would send people to QA |
| `Email__SmtpHost` | *(empty string — see §5)* | Its default in `appsettings.json` is a real relay. **This is the one that decides whether staging can send mail to real members** |
| `DevAuth__Enabled` | `false` | No dev login, no dev seeding. Sign-in is real Google OAuth only |
| `Authentication__Google__ClientId` | production's value | Same OAuth client, with the staging redirect URI added |
| `Authentication__Google__ClientSecret` | production's value | |
| `GOOGLE_MAPS_API_KEY` | production's value | Read-only autocomplete; safe to share |

### Must stay unset

Absence is the mechanism. Each of these is checked for presence, not for environment, so
setting one turns the real connector on:

| Variable | What setting it would do |
|---|---|
| `GoogleWorkspace__ServiceAccountKeyJson` / `__ServiceAccountKeyPath` | Real Drive/Groups clients. Reconciliation jobs would mutate real Workspace resources from cloned state |
| `HOLDED_API_KEY` | Real `api.holded.com` client. `HoldedExpenseOutboxJob` runs every minute and would push cloned expense rows into the real accounting system |
| `MailerLite__ApiKey` **and** `MAILERLITE_API_KEY` | Real `api.mailerlite.com` client. `MailerSectionExtensions.cs` registers `MailerLiteClient` unconditionally, and `IMailerLiteService` writes — `AssignSubscriberToGroupAsync`, `UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`. Audience membership is computed from the cloned database, so a sync from here adds and removes real subscribers in the real account. **Two spellings, both live**: the dotted key binds through `MailerLiteOptions`, and the flat one is picked up by an explicit `Environment.GetEnvironmentVariable` fallback. Omitting one and copying the other leaves the connector fully armed |
| `MailerLite__AudienceSyncCron` | Turns the audience sync from admin-triggered into scheduled. `RecurringJobExtensions.cs` registers `MailerAudienceSyncJob` only when this is non-empty, so a copied production cron makes staging push audiences on a timer with nobody watching |
| `STRIPE_TICKETS_KEY`, `STRIPE_STORE_KEY`, `STRIPE_STORE_WEBHOOK_SECRET`, `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` | Live payment calls. The registrar key additionally re-points the Store webhook |
| `Anthropic__ApiKey` | Billed agent calls from a full copy of production's data |
| `GITHUB_ACCESS_TOKEN` | Not dangerous — legal-doc sync is a read. Omit anyway; staging does not need the rate limit |
| `TICKET_VENDOR_API_KEY` | Harmless as it happens (§5), but omit it — do not rely on a second gate holding |

---

## 5. Why it is safe, and the one place it is not

The cloned database carries production's live email outbox rows, real member addresses, and
real Workspace/Holded identifiers. A staging environment labelled `Production` would double-send
pending mail on its first boot. Stubbing is the safety requirement here, not a backdoor.

**Stubbing is only half of it.** Everything in this section governs *connectors* — code paths
that reach a third party. It says nothing about the database, and staging shares a Postgres
server with production, so a staging app holding production's role reaches live data directly
with no connector in the path and no stub able to intervene. That boundary is a role grant, not
a code branch, and it is §7.7. Read the two together: §5 is why staging cannot talk to the
outside world, §7.7 is why it cannot talk to production's database.

**Stubbed by environment** — cannot be turned on from Coolify:

- **TicketTailor.** `TicketVendorInfrastructureExtensions.cs` registers `TicketTailorService`
  only when `IsProduction()`. Everything else gets `StubTicketVendorService`, whatever
  `TICKET_VENDOR_API_KEY` says.
- **Dev login and dev seeding.** `DevLoginController` and `DevSeedController` return `NotFound`
  unless `DevAuth:Enabled` is true — and it defaults to false. Real Google OAuth is the only way
  in, and authorization comes from the cloned database, so the same people hold the same roles
  they hold in production.
- **Stripe Store webhook registration.** `StoreWebhookRegistrationService` needs both the
  registrar key and a hostname ending `.n.burn.camp`; `staging.nobodies.team` fails the second
  test regardless of the first.

**Stubbed by absence of credentials** — Google Workspace, via
`GoogleWorkspaceInfrastructureExtensions.cs`: credentials present means real clients in any
environment, absent means stubs everywhere except production (where it throws instead). Leave
the two variables unset and staging gets `StubGoogleSyncService` and friends.

> ### ⚠️ Email does not stub itself. `Email__SmtpHost` must be set empty.
>
> `EmailInfrastructureExtensions.cs` chooses `StubEmailTransport` only when
> `Email:SmtpHost` is empty — and `appsettings.json` gives it a real default,
> `smtp-relay.gmail.com`, in every environment. So "no email credentials" does **not** stub
> email the way it stubs Workspace: staging gets `SmtpEmailTransport` pointed at a real relay
> unless `Email__SmtpHost` is explicitly overridden to an empty string.
>
> This is fail-**open**, and it is the wrong way round for a host whose database is a copy of
> production's outbox. Setting the variable is required, and confirming it is a §8 verification
> step rather than something to assume.
>
> **Open question for Peter (nobodies-collective/Humans#962).** The durable fix is to make
> email fail closed — stub unless SMTP is explicitly configured *for this environment*. The
> obvious change (`"Email": { "SmtpHost": "" }` in `appsettings.Staging.json`) also changes QA,
> which shares that file and may be sending real mail today on purpose. Not made here for that
> reason.

---

## 6. GDPR

**Staging holds full member PII** — every profile, email address, emergency contact, and
consent record that production holds, restored whole.

What makes that defensible under the existing posture:

- **Same host, same operators.** The data does not leave the machine that already holds it, and
  no new person gains access to it.
- **Real-login authorization only.** No dev login, no seeded personas, no anonymous path. Sign-in
  is Google OAuth against the cloned database, so the same humans hold the same roles they hold
  in production — staging grants nobody access they do not already have.
- **No new egress.** Every outbound connector is stubbed (§5), so PII is not forwarded to any
  third party from here. For a few this is enforced by code; for most — Workspace, Holded,
  MailerLite, Stripe — it rests on the credential simply being absent, so the guarantee holds
  exactly as long as §4's must-stay-unset list is honoured. That is a configuration promise,
  not a code one, which is why §8 checks it per deploy instead of assuming it.
- **Wiped on every refresh.** The database is dropped and recreated, and uploads are re-copied
  with `--delete`. Nothing accumulates: staging holds a snapshot, never a divergent second copy
  of member data with its own history.

**It still argues for dropping the staging database when idle.** Between promotion cycles,
staging is a full copy of production's personal data sitting on disk earning nothing. The
minimising step is to drop it and let the next push restore it:

```bash
docker exec humans-db psql -U humans -d postgres -c "DROP DATABASE IF EXISTS humans_staging"
rm -rf <staging uploads dir>/*
```

The next push to `staging` rebuilds both. Do this after a promotion lands, not before verifying
it.

---

## 7. Host-side steps (owner)

None of this can be done from the repository — it needs the Coolify console, the Google Cloud
console, and a shell on the production host. Until every box is ticked, pushing to `staging`
does nothing useful.

### 7.1 Create the `staging` branch

```bash
git push upstream upstream/main:refs/heads/staging
```

**Check:** the branch shows on `nobodies-collective/Humans` at the same SHA as `main`.

### 7.2 Install the self-hosted runner on the production host

GitHub → `nobodies-collective/Humans` → **Settings → Actions → Runners → New self-hosted
runner → Linux**, and follow the generated commands on the production host. This is the only
genuinely new infrastructure.

- **Labels: `self-hosted` and `prod`.** `.github/workflows/staging-db.yml` targets
  `[self-hosted, prod]`. The `nuc` label belongs to QA's runner and must not be added here, or
  `preview-db.yml` will start running preview clones on the production host.
- The runner's user needs to run `docker` (in the `docker` group), read the Coolify backup
  directory, read production's uploads directory, and write staging's.
- Install it as a service (`./svc.sh install && ./svc.sh start`) so it survives a reboot.

**Check:** the runner shows **Idle** with both labels in the Runners list.

### 7.3 Set the repository variables

GitHub → **Settings → Secrets and variables → Actions → Variables**. These are paths the
repository cannot know; the script fails loudly rather than guessing.

| Variable | Value | Required |
|---|---|---|
| `PROD_UPLOADS_DIR` | Host path backing production's `/app/wwwroot/uploads` (Coolify → the production resource → **Storages**) | Yes |
| `STAGING_UPLOADS_DIR` | Host path for staging's uploads — a **new, separate** directory | Yes |
| `COOLIFY_BACKUP_DIR` | Where Coolify writes database backup artifacts, if not `/data/coolify/backups`. Prefer the **production resource's own** backup directory over the shared root — it removes any chance of selecting another database's artifact | Only if different |
| `BACKUP_MAX_AGE_HOURS` | How old the newest artifact may be before the refresh fails as a backup incident. Default 48 — set it to match the real Coolify backup schedule, since a cadence slower than the bound fails every refresh and one much faster than it wastes the signal | Recommended |
| `STAGING_DB_CONTAINER` | Postgres container name, if not `humans-db` | Only if different |
| `STAGING_DB_OWNER` | The restricted role from §7.7, e.g. `humans_staging`. Unset, the refresh leaves the restored objects owned by production's role and logs a warning | Yes, once §7.7 is done |
| `STAGING_APP_CONTAINER` | Staging app container name, stable across deploys. The script stops the app before dropping its database and restarts it afterwards, and refuses to run if the name is unset or does not resolve — an app left running through the drop reconnects to a half-restored database and stays there | Yes |

**Check:** run the workflow manually (**Actions → Staging Database Refresh → Run workflow**) and
watch it find a backup artifact, restore it, print a non-empty migration-history table per
context, and copy uploads.

### 7.4 Create the Coolify staging application

New application on the production server, from `nobodies-collective/Humans`, branch `staging`,
domain `staging.nobodies.team`.

- Environment variables per §4 — including `Email__SmtpHost` **set and empty**, and the §4
  "must stay unset" list genuinely absent rather than blank-but-present where the code checks
  presence.
- Persistent storage mounted at `/app/wwwroot/uploads`, backed by the same host path as
  `STAGING_UPLOADS_DIR`.
- Persistent storage mounted at `/app/db-snapshots`. Staging takes pre-migration snapshots too
  (`DatabaseMigrationHostedService` runs them for `Production` and `Staging`), and without a
  volume they die with the container.
- **No scheduled backups on `humans_staging`.** It is disposable by design, and backing it up
  would create a second off-host copy of member PII (§6).
- TLS certificate for `staging.nobodies.team` (Coolify handles Let's Encrypt once DNS resolves).

**Check:** `https://staging.nobodies.team/api/version` returns JSON rather than a certificate
error or a 400.

### 7.5 Add the staging redirect URI to the OAuth client

Google Cloud console → **APIs & Services → Credentials** → the OAuth 2.0 client whose ID is in
production's `Authentication__Google__ClientId` → **Authorised redirect URIs** → add exactly:

```
https://staging.nobodies.team/signin-google
```

`/signin-google` is the ASP.NET Core Google handler's default callback path; nothing in
`Program.cs` overrides it. Add the origin `https://staging.nobodies.team` to **Authorised
JavaScript origins** if that list is in use.

**Check:** signing in at `staging.nobodies.team` completes instead of failing with
`redirect_uri_mismatch`.

### 7.6 Point DNS at the production host

`staging.nobodies.team` → the production server's address.

**Check:** it resolves to the same address as `humans.nobodies.team`.

### 7.7 Create a restricted database role for staging

**Required.** Staging and production share a Postgres server, and the database named in a
connection string is a runtime choice, not a boundary. If staging connects as `humans` — the
role that owns production — then staged code reaches production data by changing one string,
and an accidental override or a bad candidate release is production-impacting. Every stub in §5
is irrelevant to this: it is a direct database connection, not a connector.

```sql
CREATE ROLE humans_staging LOGIN PASSWORD '<a new password, not production''s>';
REVOKE ALL ON DATABASE humans FROM humans_staging;
REVOKE CONNECT ON DATABASE humans FROM humans_staging, PUBLIC;
GRANT ALL ON DATABASE humans_staging TO humans_staging;
GRANT humans_staging TO humans;
```

Two of those lines are easy to leave out, and each one silently defeats the step:

- **`REVOKE ... FROM PUBLIC`.** Postgres grants `CONNECT` to `PUBLIC` by default, so revoking
  it from the named role alone changes nothing — staging would still reach `humans`.
- **`GRANT humans_staging TO humans`.** Changing an object's owner requires membership of the
  role receiving it. Without this the refresh script cannot hand the restored objects over and
  fails at that step, unless `humans` happens to be a superuser — not something to depend on.

Then set `STAGING_DB_OWNER=humans_staging` as a repository variable (§7.3). The refresh script
transfers ownership of every restored object to that role — **ownership, not privileges**. The
restore runs as `humans`, and EF migrations issue `ALTER TABLE` and `DROP TABLE`, which `GRANT`
does not confer. With grants alone staging would start cleanly and then fail on the first
schema-changing release, which is the release the rehearsal exists for. When `STAGING_DB_OWNER`
is unset the script logs a warning naming this section, because the default is the insecure one.

**Check:** `psql -U humans_staging -d humans` is refused; `psql -U humans_staging -d
humans_staging` succeeds; and after a refresh `\dt` in `humans_staging` shows `humans_staging`
owning the restored tables.

### 7.8 Protect the `staging` branch

**Required, and it is a production-host security control rather than a hygiene one.** The
refresh workflow runs on the production host with Docker access, which is effectively root
there. `.github/workflows/staging-db.yml` checks out `main` rather than the pushed commit, so a
candidate cannot rewrite the *script* it runs — but for `on: push`, GitHub reads the *workflow
file itself* from the pushed ref. Anyone who can push to `staging` can therefore add a step and
run it as root on the production host, before the production PR is reviewed.

GitHub → **Settings → Branches → Add rule** for `staging`: restrict who can push to the people
who run promotions. Do not add required status checks — `/pr-prod` pushes a commit that CI has
already passed on the fork, and a pending check would stall the promotion.

**Check:** a push to `staging` from an account outside the allow-list is rejected.

### 7.9 Confirm the deploy ordering

Coolify auto-deploys the staging app on push to `staging`, and the refresh workflow starts on
the same push. **Nothing orders the two.** In practice the refresh (seconds) finishes long
before the image build (minutes), but that is a timing observation, not a guarantee, and the
case it would leave behind is the quiet one: a container that came up between the `DROP` and
the end of the restore, migrated a partial clone, and now serves the pushed SHA at
`/api/version` — passing §8.1 while §8.2 is a lie.

The script closes that by not trusting its own snapshot. It re-reads the container's state
after the restore and verification, and restarts anything running — whether it stopped it
itself or found it started mid-flight. A restart re-runs `DatabaseMigrationHostedService`
against the finished database, and the log line it prints is what §8.2 reads. The refresh is
therefore idempotent with respect to deploy timing rather than dependent on it.

**On failure it goes the other way.** A restore, verification, or uploads copy that fails after
the `DROP` never reaches that hand-back — `set -e` exits first — so the script's `EXIT` trap
stops the app instead. Otherwise a container that came up mid-flight would be left serving a
partial clone under the pushed SHA: workflow red, staging apparently healthy, which is exactly
the wrong way round for a promotion gate. Staging stays down until the next refresh restores
it, and down is the honest state.

**The durable fix is ordering, not correction.** Turning off Coolify's auto-deploy for the
staging resource and having `staging-db.yml` trigger the deploy through Coolify's API once the
refresh succeeds would remove the window instead of repairing it. That needs a Coolify API
token in repository secrets and the resource's UUID, so it is a deliberate follow-up rather
than part of the repository half — and it is worth doing before this environment carries a
promotion anyone relies on.

**Check:** after the first real push, the workflow log shows the restore completing, and the
staging app's log shows migrations applying to the restored database — not to an empty one. If
the workflow log carries `came up during the refresh`, the deploy raced the restore and the
restart corrected it; that is the signal to do the ordering fix above.

---

## 8. Verifying a staging deploy

Before merging the production PR onto `upstream/main`:

1. **The pushed commit is live.** `curl -s https://staging.nobodies.team/api/version` reports
   the `commit` you pushed.
2. **Migrations applied to the clone.** The staging app log carries the migration breadcrumb —
   `Database humans_staging: N applied migrations, 0 pending` — with the release's migrations
   applied, not re-applied from scratch. Any migration that was going to fail against production
   data has already failed here.
3. **Real sign-in works.** Sign in with Google. You land with the roles you hold in production,
   because that is the database you are looking at.
4. **Email is stubbed.** `/Debug/Configuration` (Admin only) lists `Email:SmtpHost`; on staging
   it must show as unset. It is registered as a critical setting, so an empty value shows up as
   a missing critical one — that is the correct state here, not a problem to fix. This is the §5
   fail-open, and it is worth one look per deploy rather than one assumption.
5. **Staging is on its own database role.** Nothing in the app reports this —
   `/Debug/Configuration` does not carry the connection string — so ask Postgres, on the
   production host:

   ```bash
   docker exec humans-db psql -U humans -d postgres \
     -c "SELECT DISTINCT usename FROM pg_stat_activity WHERE datname = 'humans_staging'"
   ```

   It must show `humans_staging`, never `humans`. Same reasoning as the check above it: §7.7 is
   a configuration boundary rather than a code one, so a Coolify edit undoes it silently and
   nothing goes wrong until staging touches production.
6. **Exercise what the release changed.** Against real data, which is the entire point of the
   environment.

If any of this fails, the promotion stops. The bad commit is on `staging` only, and the next
push overwrites it.

---

## 9. Relationship to the restore runbook

[`database-restore-runbook.md`](database-restore-runbook.md) is what you read when production's
database is wrong. This document is what keeps that runbook honest: the `backup` source restores
a real Coolify artifact on every promotion, so the archive format, the artifact path, and the
restore itself are exercised continuously rather than the once
nobodies-collective/Humans#845 asked for.

A staging refresh that fails on the restore step is telling you something about **production's
backups**, not about staging. Treat it as an incident on the backup path. `BACKUP_MAX_AGE_HOURS`
extends that to the schedule itself: backups that stopped being written are an incident even
though the last one still restores perfectly, and without the age bound that is precisely the
outage this workflow would sail through.
