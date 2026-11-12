---
name: Never put data backfills in EF migrations
description: HARD RULE. No data-mutation SQL in EF migrations, no autonomous one-shot backfill runners. Bulk fixes go through an admin screen with a review → confirm UX.
---

Migrations are schema-only. Nothing that mutates data (`UPDATE`, `INSERT`, `DELETE`, "set this column from that one") goes in a migration, however small.

**Forbidden:**

- `migrationBuilder.Sql(...)` data statements in `Up()` or `Down()`. If EF generated one, fix the model and regenerate ([`no-hand-edited-migrations`](../architecture/no-hand-edited-migrations.md)).
- "Run this script once after deploy" in a PR description.
- Fire-and-forget runners that fix rows without the operator seeing what will change first, even behind a button.
- Proposing a backfill alongside a new invariant.

**Default scope when tightening an invariant:** enforce on writes going forward and surface existing violations through a scanner page. Stop there. Rows already wrong get fixed through the UI. Self-healing on read (idempotent, fires when the data is touched) is fine.

**When Peter asks for a bulk fix:** register an `ISectionDataMigrations` contribution in the owning section. `/Debug/DataMigrations` then supplies the every-row review list, the `data-confirm` Apply and the `DataMigrationApplied` audit entry ([Debug.md](../../src/Sections/Humans.Debug/Docs/Debug.md#data-migrations)). Never a new per-section screen.

**Why:** Peter, 2026-05-09: "we never do backfill database things ever in migration.. ever.. never again" and "we're allowed to have admin screens which do backfill with a proper 2 step process." A migration-time write runs unattended at deploy, can't be reviewed per row, and leaves no audit record.
