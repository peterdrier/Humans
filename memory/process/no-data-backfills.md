---
name: Never put data backfills in EF migrations
description: HARD RULE. No `migrationBuilder.Sql("UPDATE/INSERT/DELETE ...")` data fix-ups inside EF migrations — ever. When a bulk data fix is genuinely needed, build an admin screen with a proper 2-step (review → confirm → apply) UX. Never propose autonomous one-shot runners, post-deploy "run this once" scripts, or backfill services that fire without operator review.
---

EF migrations are schema-only. Any pass that mutates data — `UPDATE`, `INSERT`, `DELETE`, "set this column based on that one" — does **not** belong inside a migration, regardless of how small or "obviously safe" it is.

When a bulk data fix is the right move, the canonical path is an **admin screen** with a two-step flow:

1. **Review** — page lists every row that would be touched, with enough context for the operator to sanity-check (counts, samples, the rule being applied). Same shape as the EmailProblems page (`/Profile/Admin/EmailProblems`) and its `Backfill all (N)` button.
2. **Confirm + apply** — explicit operator click (with `data-confirm` text), POST handler calls a service method, audit log records the action.

**Forbidden:**
- `migrationBuilder.Sql("UPDATE ...")` / `Sql("INSERT ...")` / `Sql("DELETE ...")` inside any migration's `Up()` or `Down()`. (Also covered structurally by [`no-hand-edited-migrations`](../architecture/no-hand-edited-migrations.md).)
- "Run this script once after deploy" instructions in PR descriptions.
- Autonomous one-shot runners — services that loop over rows and fix them without an operator-driven review screen, even if invokable from a button. The rule isn't "no `*BackfillService` classes," it's "no fire-and-forget bulk mutations." If the operator can't see what's about to change before clicking, it's wrong.
- Proposing a backfill as part of the same change that introduces a new invariant. Default scope is: enforce on writes going forward + surface existing violations via scanner. Add a backfill admin screen only if Peter explicitly asks.

**Allowed:**
- Admin screens with the review → confirm UX described above.
- Application-layer self-healing on read paths — idempotent corrections that fire when the data is touched naturally.
- Schema-only migrations (column add/drop/rename) where EF's tooling generated the entire file.

**Why:** Peter, 2026-05-09: *"fucking stop with the backfill passes.. we never do backfill database things ever in migration.. ever.. never again.."* and *"we're allowed to have admin screens which do backfill with a proper 2 step process. just never db migration based backfills."* — said in response to a proposed `EnsureGoogleInvariantAsync` paired with a fire-and-forget one-shot pass over ~200 ZeroIsGoogle users. The invariant change was correct; the autonomous backfill was the violation.

Migration-time data writes are uniquely dangerous: they run unattended at deploy, are invisible afterward (the migration file disappears into the history), can't be reviewed per-row, and couple data correctness to deploy timing. Admin screens with review/confirm keep the operator in the loop and produce an audit record per click.

**How to apply:**

- When designing a fix that tightens an invariant, scope is "enforce on writes + surface existing violations via scanner." Stop there.
- When asked "but what about the rows already in the wrong state?" the default answer is "they show up in `/Profile/Admin/EmailProblems` (or the relevant scanner) and get fixed through the UI." Only propose a bulk admin screen if Peter asks.
- If a bulk admin screen is genuinely warranted, model it on the existing `BackfillLegacyEmails` flow: count + list visible to the operator, explicit `data-confirm`, audit-logged POST handler.
- Migration files themselves: zero data SQL. If EF generated something that includes a data statement, regenerate.
