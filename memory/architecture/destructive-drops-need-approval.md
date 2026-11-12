---
name: destructive-drops-need-approval
description: "HARD RULE. Dropping a DB column, table, index or persisted file needs a written case, evidence of no readers, and Peter's per-case approval. Code deletions are exempt."
---

Destructive migrations are allowed, but each one needs proof it destroys nothing in use and Peter's yes
for that specific case. There is no mandatory production soak.

**In scope:** DB columns, tables, indexes, constraints and FKs; persistent files (uploads, blob storage,
volumes); external one-way state (S3 objects, KMS keys, queues); any migration whose `Up()` does one of
these. **Exempt:** code, views, assets, DI, resx, tests, docs — those roll back with `git revert`.

## The gate

1. **A written description** of what is removed: the object, its table, what it held.
2. **Evidence it holds nothing in use.** Not one grep for callers today: a column dead in current code
   can still hold rows a past release wrote. Pickaxe all history for every assignment
   (`git log -S 'RawPayload =' --pickaxe-regex -- '<paths>'`), read each hit, and say in the PR what you
   found. When the object was *replaced*, the replacement has already shipped and carries the traffic.
3. **Peter's written approval for that drop.** An LLM never approves its own drop, never infers approval
   from a general instruction, and never reads an earlier approval as covering a new case.
4. **Its own PR**, not buried in a feature branch.

## Standing approval: a data migration reporting done

Peter: *"the flag that a migration is done is also the permission necessary for the database remove of
the infringing obsolete column/table."* For a legacy column or table replaced through a registered data
migration (`ISectionDataMigrations`, reported by `GET /api/data-migrations`) the gate collapses to:

1. `done: true` for the migration's key on **production**, quoted with the date read.
2. The retirement PR (deleting the migration, the read fallback and the `[Obsolete]` fields) is merged
   and deployed.
3. The drop is still its own generated PR; its baseline entry's `Approval:` cites the production reading
   and this section, its `Evidence:` cites the retirement PR.
4. Peter merges it. Title starts `⚠️ DESTRUCTIVE`, label `db:Destructive`, body opens with a caution
   banner. Nothing auto-merges a drop.

This is the only standing approval.

## Also

- Generate the drop with `dotnet ef migrations add` — [`no-hand-edited-migrations`](no-hand-edited-migrations.md).
- **Check `Down()` would actually run.** For a non-nullable string EF scaffolds `defaultValue: ""`, which
  on a `jsonb` column is a Postgres error. Never hand-fix it: ship the drop alone, record the
  broken-rollback caveat in the approval note, and accept that rollback means re-adding the column.
- [`event-deploy-freeze`](../process/event-deploy-freeze.md) still freezes schema changes during the event.
- The restore procedure is [`docs/database-restore-runbook.md`](../../docs/database-restore-runbook.md).
- For decoupling work the column drop is optional —
  [`no-column-drops-for-decoupling`](no-column-drops-for-decoupling.md).

## Enforcement

`NoDestructiveMigrationOpsRule` (`tests/Humans.Web.Tests/Architecture/Rules/`) fails any `Drop*` in a
migration's `Up()` that is not in `Baselines/NoDestructiveMigrationOps.baseline.txt`. That baseline is
the **approval ledger**: each locator sits under a comment with an `Approval:` + `Evidence:` pair (or
`Pre-existing:`) that names every identifier in the locator, table as well as column, and a second test
fails entries without one. If you cannot write those facts, you do not have approval yet.
