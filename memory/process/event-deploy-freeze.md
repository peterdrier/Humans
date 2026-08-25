---
name: No schema-changing deploy during a live event
description: HARD RULE. During a live-event period (build week through strike), deploys with pending EF migrations are frozen. If one truly cannot wait it needs a confirmed pre-deploy snapshot plus an admin awake at a keyboard. Code-only deploys stay allowed.
---

During a live-event period — build week through the end of strike — **do not deploy anything with a
pending EF migration.** Any `Up()` counts: `AddColumn` is as frozen as `DropColumn`.

**Still allowed:** code-only deploys with no pending migrations. They roll back with the image
in minutes.

**If a schema change genuinely cannot wait** (data-corrupting bug, gate admissions broken), all
five, no exceptions:

1. An admin who can reach the server is awake, at a keyboard, and knows the deploy is happening.
2. **Before:** the snapshot volume is real — `docker exec $APP ls -l /app/db-snapshots` shows the
   previous deploy's files on a mounted volume, not an empty directory in the container's own
   filesystem. You cannot pre-check *this* deploy's snapshot: it is taken by the new image on
   boot, immediately before it migrates. What you are confirming is that the mechanism will have
   somewhere durable to write.
3. **After:** a snapshot carrying this deploy's timestamp actually appeared —
   `docker exec $APP ls -lt /app/db-snapshots | head` — before anyone walks away.
4. Whoever is deploying has read [`docs/database-restore-runbook.md`](../../docs/database-restore-runbook.md)
   beforehand, not during the incident.
5. It is not a peak hour (gate opening, ticket scanning surge, shift changeover).

Condition 2 is about durability, not about whether a snapshot gets taken. That part the code
already guarantees: a dump that fails aborts startup *before* any schema change, so a
schema-changing deploy cannot get past boot without one. What a missing volume costs you is the
snapshot surviving a container replacement.

**Why:** migrations apply at startup and rethrow on failure, and the single instance runs with
`restart: unless-stopped` — so a bad migration crash-loops the only instance. Recovery is a
hand-restore. That is a ten-minute job with a snapshot, the runbook, and an awake admin; an
outage of unknown length without them, during the one week the app is load-bearing in real time.

**How to apply:** when planning or executing a deploy during a live-event window, check for pending
migrations first. If there are any, the answer is "after the event" unless the four conditions
above are met and Peter has said go. Outside a live-event window, this freeze does not apply —
normal migration discipline governs instead.

**Related:** [`no-drops-until-prod-verified`](../architecture/no-drops-until-prod-verified.md) —
the hard-storage drop split this rule leans on;
[`docs/database-restore-runbook.md`](../../docs/database-restore-runbook.md) — the recovery
procedure and the pre-deploy snapshot mechanism (nobodies-collective/Humans#845).
