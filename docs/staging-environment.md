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
| **`backup`** (default) | The newest Coolify backup artifact under `COOLIFY_BACKUP_DIR` | Every push. Doubles as the restore test — a broken backup fails the workflow here rather than during an incident |
| **`live`** | `pg_dump` straight from the production database | Manual override via **Actions → Staging Database Refresh → Run workflow → source: live**, when you need data newer than the last backup |

Format is detected, not assumed: `.gz` is decompressed, then the `PGDMP` magic bytes select
`pg_restore --exit-on-error` over `psql -v ON_ERROR_STOP=1`. Both paths carry a
stop-on-first-error flag, because the failure mode without one is a partial database that looks
fine.

### What it verifies

After restoring, the script fails the workflow if the restored database has no tables, or if
**any** `__EFMigrationsHistory*` table came back empty. There is one history table per
DbContext since the per-section split (nobodies-collective/Humans#858), and an empty one is the
quiet failure worth catching: the app boots happily and then applies that section's migrations
from scratch against tables that already exist.

### Uploads

`rsync -a --delete` from production's uploads directory into staging's. A **copy**, never a
shared mount — staging writes to its uploads directory, and production's bytes must not be on
the other end of that. `--delete` is deliberate: staging's own uploads are wiped on every
refresh, exactly like its database. The script refuses to run if both paths are not set, or if
they are the same path.

### Running it by hand

On the production host, from a checkout:

```bash
PROD_UPLOADS_DIR=/path/to/prod/uploads \
STAGING_UPLOADS_DIR=/path/to/staging/uploads \
STAGING_APP_CONTAINER=<staging app container> \
./scripts/refresh-staging-db.sh backup      # or: live
```

Set `BACKUP_FILE=/path/to/artifact` to restore a specific one — an older backup, or one whose
filename does not carry the database name — instead of the newest match.

Nothing in the script writes to production. `backup` never opens the production database at
all; `live` only reads it; the uploads copy is one-way. The destructive statements target
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
| `ConnectionStrings__DefaultConnection` | `Host=humans-db;Database=humans_staging;Username=humans;Password=<prod DB password>` | Same Postgres server as production, different database |
| `AllowedHosts` | `staging.nobodies.team;localhost` | `appsettings.Staging.json` sets this to `humans.n.burn.camp;localhost`. Leave it and **every request to staging is rejected** before it reaches a controller |
| `Email__BaseUrl` | `https://staging.nobodies.team` | Same file points it at QA. Links in anything staging generates would send people to QA |
| `Email__SmtpHost` | *(empty string — see §5)* | Its default in `appsettings.json` is a real relay. **This is the one that decides whether staging can send mail to real members** |
| `DevAuth__Enabled` | `false` | No dev login, no dev seeding. Sign-in is real Google OAuth only |
| `Authentication__Google__ClientId` | production's value | Same OAuth client, with the staging redirect URI added |
| `Authentication__Google__ClientSecret` | production's value | |
| `GOOGLE_MAPS_API_KEY` | production's value | Read-only autocomplete; safe to share |
| `POSTGRES_PASSWORD` | production's value | Only if the staging resource templates the connection string from it |

### Must stay unset

Absence is the mechanism. Each of these is checked for presence, not for environment, so
setting one turns the real connector on:

| Variable | What setting it would do |
|---|---|
| `GoogleWorkspace__ServiceAccountKeyJson` / `__ServiceAccountKeyPath` | Real Drive/Groups clients. Reconciliation jobs would mutate real Workspace resources from cloned state |
| `HOLDED_API_KEY` | Real `api.holded.com` client. `HoldedExpenseOutboxJob` runs every minute and would push cloned expense rows into the real accounting system |
| `STRIPE_TICKETS_KEY`, `STRIPE_STORE_KEY`, `STRIPE_STORE_WEBHOOK_SECRET`, `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` | Live payment calls. The registrar key additionally re-points the Store webhook |
| `Anthropic__ApiKey` | Billed agent calls from a full copy of production's data |
| `GITHUB_ACCESS_TOKEN` | Not dangerous — legal-doc sync is a read. Omit anyway; staging does not need the rate limit |
| `TICKET_VENDOR_API_KEY` | Harmless as it happens (§5), but omit it — do not rely on a second gate holding |

---

## 5. Why it is safe, and the one place it is not

The cloned database carries production's live email outbox rows, real member addresses, and
real Workspace/Holded identifiers. A staging environment labelled `Production` would double-send
pending mail on its first boot. Stubbing is the safety requirement here, not a backdoor.

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
  third party from here.
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
| `COOLIFY_BACKUP_DIR` | Where Coolify writes database backup artifacts, if not `/data/coolify/backups` | Only if different |
| `STAGING_DB_CONTAINER` | Postgres container name, if not `humans-db` | Only if different |
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

### 7.7 Confirm the deploy ordering

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
5. **Exercise what the release changed.** Against real data, which is the entire point of the
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
backups**, not about staging. Treat it as an incident on the backup path.
