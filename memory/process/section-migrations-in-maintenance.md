---
name: Section migrations are allowed in maintenance work
description: Bug fixes, refactors, and tech-debt work may include section-owned schema changes; substantial architecture transitions remain explicitly scoped tasks with dedicated PRs.
---

Allow section-owned persistence, entity-model, configuration, and EF migration changes when needed for the authorized task. Do not skip an otherwise suitable fix merely because it needs a migration.

**Why:** The blanket maintenance ban came from concurrent PRs sharing one migration chain. Sections now own separate contexts and migration chains. Peter retired that blanket restriction; PRs touching the same context still need coordination.

**How to apply:**

- Keep each migration in its owning section. Independent contexts can progress in separate PRs; coordinate branches that change the same context or depend on another cutover.
- Treat substantial section-independence and ownership transitions as explicit tasks with boundary decisions, acceptance criteria, and dedicated PRs, not unattended debt-sweep items. A maintenance run records these candidates for planning.
- Follow [EF tooling discipline](ef-multi-context-commands.md), [generated migrations and snapshots](../architecture/no-hand-edited-migrations.md), [snapshot inspection](diff-snapshot-after-ef-tool.md), the [migration review gate](ef-migration-review-gate.md), and [interleaved-migration recovery](../architecture/migration-regen-after-rebase.md).
- Existing approval requirements for [storage drops](../architecture/no-drops-until-prod-verified.md), [required columns](../architecture/required-columns-need-approval.md), [public surface](reuse-first-change-discipline.md), and privilege changes remain. Preserve serialization contracts unless the task authorizes changing them.
- This permits code and generated schema changes, not [manual database writes](no-manual-db-writes.md), [data backfills in migrations](no-data-backfills.md), or bypassing the [event deployment freeze](event-deploy-freeze.md).
