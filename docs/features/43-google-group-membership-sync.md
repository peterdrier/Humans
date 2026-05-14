# Google Group Membership Sync

## Purpose

Google Group membership is reconciled as expected state from application-owned sources, not as per-user imperative add/remove calls. This keeps group ownership explicit, avoids coupling every membership owner to Google APIs, and lets Google failures retry independently of team/email writes.

## Architecture

`IGoogleGroupMembershipSource` is the plugin contract. A source claims one or more Google group keys and returns expected user IDs for each key. Sources do not hydrate emails, filter users, inspect Google, or mutate Google.

`GoogleGroupSyncService` implements `IGoogleGroupSync` and owns the shared orchestration:

- load all source claims
- detect duplicate group-key claims
- hydrate users, Google emails, and profiles
- filter deleted, merged, suspended, and rejected-email users
- diff expected members against Google group members
- apply adds/removes according to Google Groups sync mode
- audit, notify on removals, and schedule scoped retries

`TeamService` directly implements `IGoogleGroupMembershipSource` for team Google Groups. `ITeamService` intentionally does not inherit the source interface; Google Integration registers the concrete service as a source so the Teams public service boundary does not grow for Google-only ownership.

## Reconciliation Modes

`ReconcileAllAsync` is the daily, hourly system-team-sync, and bulk preview/execute path. It loads every source claim, hydrates expected users once for the pass, records per-group errors, and schedules the capped scoped retry path for groups that fail during Execute.

`ReconcileOneAsync` is the scoped path used by per-row Execute and queued Hangfire requests after team membership or Google-email changes. On Google API failure during Execute, it schedules delayed retries for the same group key, capped at five scoped retry attempts.

## Collision Rule

Group-key collisions are fail-closed. If more than one source claims the same key, the orchestrator logs and audits the collision and skips mutation for that group. First-wins is forbidden because it could silently remove members claimed by another owner.

## Triggers

Team membership and Google-email changes enqueue scoped group reconciliation after the application write commits. Drive permissions still use the Google sync outbox and `IGoogleSyncService`; group membership uses `IGoogleGroupSync`.

When Google rejects a target member email specifically, legacy `User.GoogleEmailStatus` is marked `Rejected` and the warning log includes the email address. Generic Google API failures such as billing, quota, and backend errors do not mutate user email state; they leave the group sync eligible for the capped scoped retry path.
