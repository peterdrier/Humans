---
name: A destructive migration needs a written description of what it removes and Peter's approval for that case
description: HARD RULE. DB columns/tables/indexes/constraints, persistent filesystem data and external persisted state may be dropped — but only with a written description of what is being removed, evidence it holds nothing in use, and Peter's per-case written approval. An LLM never approves its own drop. Code-only deletions are exempt.
---

> **Filename is historical.** The slug still says `no-drops-until-prod-verified` because ~20 files link
> to it; the rule it names changed on 2026-08-18 (Peter). Drops are no longer gated on a production
> soak — they are gated on evidence and approval. Read this file, not the slug.

**Destructive migrations are allowed.** What they need is proof they are not destroying something in
use, and a human saying yes to that specific case. Code drops (classes, methods, files) are exempt —
code rolls back via `git revert` + redeploy.

**In scope (need the gate below):**
- DB columns, tables, indexes, unique constraints, check constraints, FKs
- Persistent filesystem data (uploaded user files, blob storage, mounted volumes)
- External persisted state (S3 objects, KMS keys, queue contents — anything where deletion is one-way)
- EF migrations whose `Up()` performs any of the above

**Out of scope:**
- C# code: classes, methods, properties, interfaces, files
- Razor views / static assets / DI registrations / localization resources / tests / docs

## The gate

1. **A written description of what is being removed** — the object, the table it is on, and what it
   held.
2. **Evidence it holds nothing in use.** Not "no callers today" from a single grep: check the whole
   history of what was ever *written* there, because a column can be dead in current code and still
   hold rows a past release wrote. The cheap, honest check is a pickaxe search over all history for
   every assignment to the property, e.g. `git log -S 'RawPayload =' --pickaxe-regex -- '<paths>'`,
   and reading each hit. Say in the PR what you found.
3. **Peter's written approval for that specific drop.** Per case, in words, from Peter. An LLM never
   approves its own drop, never infers approval from a general instruction, and never reads an
   earlier authorized exception as covering a new one.
4. **Its own PR, for clarity** — a destructive migration should not be buried in a feature branch.

Once those four are satisfied the drop ships; there is no mandatory soak. When the thing being
dropped is *replaced* rather than simply dead, step 2 normally means the replacement has already
shipped and is carrying the traffic — that is what makes the old object provably unused, not a
calendar.

## What still applies

- **Migrations stay 100% auto-generated** — [`no-hand-edited-migrations`](no-hand-edited-migrations.md).
  Generate the drop with `dotnet ef migrations add`; never hand-write a `DropColumn`.
- **Full-build before `dotnet ef`.** With `--no-build` the tooling reads whatever the startup project
  last built, which yields an empty migration; a following `migrations remove --force` then walks back
  the *wrong* migration. Recover by `git checkout` of the Migrations folder, then rebuild and
  regenerate.
- **`Down()` must restore the shape.** EF writes it — check it is there *and whether it would run*.
  For a non-nullable string column EF scaffolds `defaultValue: ""`, which on a `jsonb` column is
  `DEFAULT ''` and a Postgres error, so that rollback fails the moment anyone tries it. Never hand
  edit it. The only mechanical fix — `HasDefaultValueSql` declared in a preceding migration —
  collides with the one-migration-per-context check, and Peter resolved that collision in favour of
  the single migration (2026-08-18, peterdrier/Humans#1379): ship the drop alone, record the
  broken-rollback caveat in the approval comment, and accept that rolling back means re-adding the
  column by hand.
- [`event-deploy-freeze`](../process/event-deploy-freeze.md) still freezes schema-changing deploys
  during the event.
- The restore procedure behind all of this is
  [`docs/database-restore-runbook.md`](../../docs/database-restore-runbook.md) — measured, not
  theoretical. It is the reason step 2 is not a formality.

## How this is enforced

`NoDestructiveMigrationOpsRule` (`tests/Humans.Web.Tests/Architecture/Rules/`) scans every
migration's `Up()` and fails on any `Drop*` it does not find in
`tests/Humans.Web.Tests/Architecture/Baselines/NoDestructiveMigrationOps.baseline.txt`.

Under this rule the baseline is **the approval ledger**: adding a locator to it is how an approved
drop is recorded, and each entry carries a comment naming what was removed, the evidence, and
Peter's approval. If you cannot write the three facts, you do not have approval yet.

That is enforced, not merely asked for: `Every_baseline_entry_is_covered_by_an_approval_note` fails
any locator whose comment block above it carries no `Approval:` + `Evidence:` pair (or
`Pre-existing:`, for drops that shipped before this rule), or does not name every identifier in
its locator — the table as well as the column, matched on token boundaries so `RawPayloadBackup`
does not cover `RawPayload`. Naming is what stops a new locator appended inside an existing group from
borrowing that group's approval, or one table's approval covering a same-named column on another
table. The ratchet's own reader discards comments and diffs locators only,
so without that second test a bare locator would pass silently and the ledger would be a convention
rather than a guardrail. The test does not judge whether the evidence is *good* — a human does that;
it only refuses an entry that never claimed any.

**Related:** [`no-column-drops-for-decoupling`](no-column-drops-for-decoupling.md) — for *decoupling*
work specifically, the property override is the migration and the column drop stays optional; that
rule is about not manufacturing drops you do not need, and it is unchanged.

## Authorized drops (Peter, per-incident)

Kept as history; each was approved for its own case and none of them authorizes anything else.

- **Containers redesign (PR #389, 2026-05-11)** — `DropColumn ContainerCount` and `DropColumn ContainerNotes` on `camp_seasons`, in the same migration (`20260511114347_AddContainers`) that introduces the replacement `containers` + `container_placements` tables. One-shot redesign, zero remaining readers, structurally different replacement.
- **Per-occurrence event favourites (2026-06-11)** — `DropIndex IX_event_favourites_UserId_GuideEventId` in the same migration (`20260611203803_AddEventFavouriteDayOffset`) that creates the widened unique index `(UserId, GuideEventId, DayOffset)`. The old index actively forbids the rows the feature writes; index-only, no data touched.
- **Team role name index realign (2026-08-11)** — `DropIndex IX_team_role_definitions_team_name_unique` in `20260811145603_RealignTeamRoleNameIndex`, recreating it as the plain `(TeamId, Name)` unique index the model declares. Index-only, rebuildable.
- **Holded ledger single-source (2026-06-15)** — `DropTable holded_creditor_balances`, `DropTable holded_payments`, `DropColumn SepaSentAt`/`PaidAt` on `expense_reports`, in `20260615201620_HoldedLedgerSingleSource`. Peter's call: those creditor read-model tables were not in real use.
- **Dead Holded columns (2026-08-18)** — `DropColumn RawPayload` on `holded_expense_docs` and `DropColumn ArchivedAt` on `holded_category_map`. Both were introduced by #783 and never written: `RawPayload`'s only assignment in all of history is the literal `"{}"`, and `ArchivedAt`'s writer was cut in that same PR's review as having zero callers and never re-added. Verified by pickaxe over full history. Peter's approval, 2026-08-18: "drop both columns".
