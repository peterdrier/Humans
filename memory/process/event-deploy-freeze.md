---
name: No schema-changing deploy during the live event
description: HARD RULE. From build week through strike, deploys with pending EF migrations are frozen. If one truly cannot wait it needs a confirmed pre-deploy snapshot plus an admin awake at a keyboard. Code-only deploys stay allowed.
---

During the live event — build week through the end of strike — **do not deploy anything with a
pending EF migration.** Any `Up()` counts: `AddColumn` is as frozen as `DropColumn`.

**Still allowed:** code-only deploys with no pending migrations. They roll back with the image
in minutes.

**If a schema change genuinely cannot wait** (data-corrupting bug, gate admissions broken), all
four, no exceptions:

1. An admin who can reach the server is awake, at a keyboard, and knows the deploy is happening.
2. The pre-deploy snapshot file is confirmed present — `docker exec $APP ls -l /app/db-snapshots`
   — not assumed.
3. Whoever is deploying has read [`docs/database-restore-runbook.md`](../../docs/database-restore-runbook.md)
   beforehand, not during the incident.
4. It is not a peak hour (gate opening, ticket scanning surge, shift changeover).

**Why:** migrations apply at startup and rethrow on failure, and the single instance runs with
`restart: unless-stopped` — so a bad migration crash-loops the only instance. Recovery is a
hand-restore. That is a ten-minute job with a snapshot, the runbook, and an awake admin; an
outage of unknown length without them, during the one week the app is load-bearing in real time.

**How to apply:** when planning or executing a deploy during the event window, check for pending
migrations first. If there are any, the answer is "after the event" unless the four conditions
above are met and Peter has said go.

**Related:** [`no-drops-until-prod-verified`](../architecture/no-drops-until-prod-verified.md) —
the hard-storage drop split this rule leans on;
[`docs/database-restore-runbook.md`](../../docs/database-restore-runbook.md) — the recovery
procedure and the pre-deploy snapshot mechanism (nobodies-collective/Humans#845).
