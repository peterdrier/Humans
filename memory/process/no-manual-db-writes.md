---
name: No manual DB writes, any environment, any reason
description: HARD RULE. Never modify a DB row by hand in any env — no psql/admin-UI writes, no `__EFMigrationsHistory` patching, no fix-up migrations. Drop and recreate the env DB instead.
---

Direct database writes are never allowed in production, QA, preview or local dev:

- no `INSERT`/`UPDATE`/`DELETE` through `psql`, Coolify's DB UI, pgAdmin or any ad-hoc tool;
- no editing `__EFMigrationsHistory` to match a regenerated migration;
- no no-op "fix-up" migration whose only purpose is to rewrite history;
- no hand-seeding test data.

**Why:** an out-of-band write desyncs code from data. Any environment could then be in any state and the next migration may apply differently. Every state change lives in code, reviewable and reproducible. Peter, on both manual seeding and history patching: "never allowed".

**Instead:**

- QA or preview in a bad state, or carrying an old migration id after a regen: drop and recreate the database. For a preview, closing and reopening the PR re-clones `humans_pr_{N}` from QA.
- Need test data: `DevSeedController`, a stub such as `StubTicketVendorService`, or a real dev seeder.
- Normal application code writing through services and repositories is not a manual write.

Related: [`no-data-backfills`](no-data-backfills.md) · [`no-hand-edited-migrations`](../architecture/no-hand-edited-migrations.md)
